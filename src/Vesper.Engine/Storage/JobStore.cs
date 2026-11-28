using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Vesper.Engine;

public sealed class StoreTooNewException(int found, int supported)
    : Exception($"The VESPER database was created by a newer version (schema {found}; this build understands {supported}). It was left untouched.");

/// <summary>
/// The authoritative durable state. SQLite in WAL mode with synchronous=FULL, so a committed
/// checkpoint survives power loss. Only the engine writes here.
/// </summary>
public sealed class JobStore : IDisposable
{
    private static readonly string[] JournalSuffixes = ["-wal", "-shm", "-journal"];
    // Index + 1 is the schema version. Append only; never edit a shipped migration.
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE jobs (
          id TEXT PRIMARY KEY, kind INTEGER NOT NULL, source_url TEXT NOT NULL, previous_urls TEXT NOT NULL DEFAULT '',
          file_name TEXT NOT NULL, name_locked INTEGER NOT NULL DEFAULT 0, destination TEXT NOT NULL, partial_path TEXT NOT NULL,
          final_path TEXT, pending_final_path TEXT, total_bytes INTEGER, extents TEXT NOT NULL DEFAULT '', committed_bytes INTEGER NOT NULL DEFAULT 0,
          state INTEGER NOT NULL, reason TEXT NOT NULL DEFAULT '', message TEXT NOT NULL DEFAULT '', priority INTEGER NOT NULL DEFAULT 0,
          queue_order INTEGER NOT NULL DEFAULT 0, speed_limit INTEGER NOT NULL DEFAULT 0,
          created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, completed_utc TEXT, retry_count INTEGER NOT NULL DEFAULT 0, next_retry_utc TEXT,
          etag TEXT, last_modified TEXT, accepts_ranges INTEGER NOT NULL DEFAULT 0, identity_level INTEGER NOT NULL DEFAULT 0, content_type TEXT,
          category TEXT NOT NULL DEFAULT '', referer TEXT, user_agent TEXT, expected_sha256 TEXT, piece_map TEXT,
          rescue_candidate INTEGER NOT NULL DEFAULT 0, rescue_mode INTEGER NOT NULL DEFAULT 0, sha256_verified INTEGER NOT NULL DEFAULT 0,
          unverified_resume INTEGER NOT NULL DEFAULT 0, non_resumable INTEGER NOT NULL DEFAULT 0, media_state TEXT, generation INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_jobs_state ON jobs(state);
        CREATE TABLE submissions (request_id TEXT PRIMARY KEY, job_id TEXT NOT NULL, created_utc TEXT NOT NULL);
        CREATE TABLE events (id INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT, utc TEXT NOT NULL, kind TEXT NOT NULL, detail TEXT NOT NULL);
        """,
        """
        ALTER TABLE jobs ADD COLUMN original_page_url TEXT;
        ALTER TABLE jobs ADD COLUMN media_provider TEXT;
        ALTER TABLE jobs ADD COLUMN media_id TEXT;
        ALTER TABLE jobs ADD COLUMN media_quality TEXT;
        ALTER TABLE jobs ADD COLUMN media_thumbnail_url TEXT;
        ALTER TABLE jobs ADD COLUMN media_expires_at TEXT;
        """,
        """
        ALTER TABLE jobs ADD COLUMN media_plan_json TEXT;
        ALTER TABLE jobs ADD COLUMN media_format_id TEXT;
        """,
        """
        ALTER TABLE jobs ADD COLUMN media_output INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE jobs ADD COLUMN pending_conversion_bytes INTEGER;
        ALTER TABLE jobs ADD COLUMN pending_conversion_digest TEXT;
        """,
        """
        ALTER TABLE jobs ADD COLUMN browser_proof TEXT;
        """,
        """
        ALTER TABLE jobs ADD COLUMN media_metadata_json TEXT;
        """,
        """
        ALTER TABLE jobs ADD COLUMN published_sha256 TEXT;
        """,
        """
        ALTER TABLE jobs ADD COLUMN partial_identity TEXT;
        """,
        """
        ALTER TABLE jobs ADD COLUMN final_resource_identity TEXT;
        ALTER TABLE jobs ADD COLUMN published_file_identity TEXT;
        """,
        """
        ALTER TABLE jobs ADD COLUMN pending_conversion_identity TEXT;
        """,
        """
        ALTER TABLE jobs ADD COLUMN publication_copy_identity TEXT;
        """,
        """
        ALTER TABLE jobs ADD COLUMN delivery_elapsed_ms INTEGER;
        """
    ];

    public static int SchemaVersion => Migrations.Length;

    private readonly SqliteConnection _db;
    private readonly string _path;
    private readonly object _gate = new();
    private int _eventsSinceTrim;

    public bool RecoveredFromCorruption { get; private set; }
    public string? CorruptBackupPath { get; private set; }

    private JobStore(string path)
    {
        _path = path;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        try { _db.Open(); }
        catch { _db.Dispose(); throw; }
    }

    /// <summary>
    /// Opens the database, migrating if needed. A damaged database is quarantined (renamed, never deleted)
    /// and replaced with an empty one so the app still starts; the caller may then import recovery manifests.
    /// </summary>
    public static JobStore Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string? interruptedBackup = RecoverQuarantine(path);
        if (interruptedBackup is not null) return OpenAfterQuarantine(path, interruptedBackup);
        try { return OpenInitialized(path); }
        catch (SqliteException ex) when (File.Exists(path) && (ex.SqliteErrorCode is 11 or 26))
        {
            return OpenAfterQuarantine(path, Quarantine(path, keepMarker: true));
        }
    }

    private static JobStore OpenInitialized(string path)
    {
        var store = new JobStore(path);
        try { store.Initialize(); return store; }
        catch { store.Dispose(); throw; }
    }

    private static JobStore OpenAfterQuarantine(string path, string backup)
    {
        JobStore store = OpenInitialized(path);
        store.RecoveredFromCorruption = true;
        store.CorruptBackupPath = backup;
        return store;
    }

    internal void FinishQuarantineRecovery() => File.Delete(QuarantineMarker(_path));

    internal static string Quarantine(string path, bool keepMarker = false)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string target = $"{path}.corrupt-{stamp}-{Guid.NewGuid():N}";
        var moved = new List<(string Original, string Archived)>(JournalSuffixes.Length + 1);
        string pending = QuarantineMarker(path);
        string staged = target + ".marker.tmp";
        try
        {
            using (var marker = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                marker.Write(Encoding.UTF8.GetBytes(Path.GetFileName(target)));
                marker.Flush(flushToDisk: true);
            }
            File.Move(staged, pending);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }

        try
        {
            // Move the database last so startup can tell whether to restore the journals.
            foreach (string suffix in JournalSuffixes.Append(""))
            {
                string original = path + suffix;
                if (suffix.Length != 0 && !QuarantineFileExists(original)) continue;
                string archived = target + suffix;
                File.Move(original, archived);
                moved.Add((original, archived));
            }
        }
        catch (Exception originalError)
        {
            var restoreErrors = new List<Exception>();
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                try { File.Move(moved[i].Archived, moved[i].Original); }
                catch (Exception restoreError) { restoreErrors.Add(restoreError); }
            }

            if (restoreErrors.Count == 0)
            {
                try { File.Delete(pending); }
                catch (Exception markerError) { restoreErrors.Add(markerError); }
            }
            if (restoreErrors.Count != 0)
                throw new IOException(
                    "Database recovery could not restore every original file. The archived files were kept for manual recovery.",
                    new AggregateException([originalError, .. restoreErrors]));
            throw;
        }

        if (!keepMarker) File.Delete(pending);
        return target;
    }

    private static string QuarantineMarker(string path) => path + ".quarantine-pending";

    private static string? RecoverQuarantine(string path)
    {
        string marker = QuarantineMarker(path);
        if (!QuarantineFileExists(marker)) return null;

        string prefix = Path.GetFileName(path) + ".corrupt-";
        int nameLength = Encoding.UTF8.GetByteCount(prefix) + 14 + 1 + 32;
        string name;
        using (var file = File.OpenRead(marker))
        {
            if (file.Length != nameLength) throw new IOException("The database recovery marker is incomplete.");
            byte[] bytes = new byte[nameLength];
            file.ReadExactly(bytes);
            name = Encoding.UTF8.GetString(bytes);
        }
        if (name.Length != prefix.Length + 14 + 1 + 32 || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !DateTime.TryParseExact(name.AsSpan(prefix.Length, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)
            || name[prefix.Length + 14] != '-' || !Guid.TryParseExact(name.AsSpan(prefix.Length + 15), "N", out _))
            throw new IOException("The database recovery marker is invalid.");

        string archive = Path.Combine(Path.GetDirectoryName(path)!, name);
        bool liveDatabase = QuarantineFileExists(path);
        if (QuarantineFileExists(archive))
        {
            if (!liveDatabase && JournalSuffixes.Any(suffix => QuarantineFileExists(path + suffix)))
                throw new IOException("Database recovery found journals without their database.");
            return archive;
        }

        if (!liveDatabase) throw new IOException("Database recovery could not find the original database.");
        foreach (string suffix in JournalSuffixes)
        {
            if (QuarantineFileExists(archive + suffix))
                File.Move(archive + suffix, path + suffix);
        }
        File.Delete(marker);
        return null;
    }

    private static bool QuarantineFileExists(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("Database recovery found a directory or link where a file was expected.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
    }

    private void Initialize()
    {
        Scalar("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=FULL;");

        if (Scalar("PRAGMA quick_check;") is not "ok")
            throw new SqliteException("Database integrity check failed.", 11);

        int version = Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > Migrations.Length) throw new StoreTooNewException(version, Migrations.Length);
        if (version == Migrations.Length) return;

        if (version > 0) BackupBeforeMigration(version);   // rollback = restore this copy next to the database
        for (int v = version; v < Migrations.Length; v++)
        {
            using SqliteTransaction tx = _db.BeginTransaction();
            using SqliteCommand command = _db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = Migrations[v];
            command.ExecuteNonQuery();
            command.CommandText = $"PRAGMA user_version = {v + 1};";
            command.ExecuteNonQuery();
            tx.Commit();
        }
    }

    private void BackupBeforeMigration(int version)
    {
        string backup = $"{_path}.bak-v{version}";
        if (File.Exists(backup)) backup += "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        string temporary = backup + ".new-" + Guid.NewGuid().ToString("N");
        using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        try
        {
            using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporary, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString()))
            {
                copy.Open();
                _db.BackupDatabase(copy);
            }
            File.Move(temporary, backup);
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // jobs
    private static readonly string[] Columns =
    [
        "id", "kind", "source_url", "previous_urls", "file_name", "name_locked", "destination", "partial_path", "final_path",
        "pending_final_path", "total_bytes", "extents", "committed_bytes", "state", "reason", "message", "priority", "queue_order",
        "speed_limit", "created_utc", "updated_utc", "completed_utc", "retry_count", "next_retry_utc", "etag", "last_modified",
        "accepts_ranges", "identity_level", "content_type", "category", "referer", "user_agent", "expected_sha256", "piece_map",
        "rescue_candidate", "rescue_mode", "sha256_verified", "unverified_resume", "non_resumable", "media_state", "generation",
        "original_page_url", "media_provider", "media_id", "media_quality", "media_thumbnail_url", "media_expires_at", "media_plan_json", "media_format_id",
        "media_output", "pending_conversion_bytes", "pending_conversion_digest", "browser_proof", "media_metadata_json", "published_sha256",
        "partial_identity", "final_resource_identity", "published_file_identity", "pending_conversion_identity", "publication_copy_identity",
        "delivery_elapsed_ms"
    ];

    private static readonly string UpsertSql =
        $"INSERT INTO jobs ({string.Join(',', Columns)}) VALUES ({string.Join(',', Columns.Select(c => "$" + c))}) " +
        $"ON CONFLICT(id) DO UPDATE SET {string.Join(',', Columns.Skip(1).Select(c => $"{c}=excluded.{c}"))};";

    /// <summary>Durably writes the job and optional browser submission ID together.</summary>
    public void Save(DownloadJob job, string? submissionId = null)
    {
        lock (_gate)
        {
            using SqliteTransaction tx = _db.BeginTransaction();
            using SqliteCommand command = _db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = UpsertSql;
            foreach ((string name, object? value) in Values(job))
                command.Parameters.AddWithValue("$" + name, value ?? DBNull.Value);
            command.ExecuteNonQuery();
            if (!string.IsNullOrEmpty(submissionId))
            {
                using SqliteCommand submitted = _db.CreateCommand();
                submitted.Transaction = tx;
                submitted.CommandText = "INSERT OR IGNORE INTO submissions(request_id, job_id, created_utc) VALUES($r,$j,$t);";
                submitted.Parameters.AddWithValue("$r", submissionId);
                submitted.Parameters.AddWithValue("$j", job.Id.ToString("D"));
                submitted.Parameters.AddWithValue("$t", Iso(DateTime.UtcNow));
                submitted.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Reorder existing jobs atomically. Only queue_order may change:
    /// a full-row save could overwrite a newer download checkpoint from another caller.
    /// Caller holds DownloadEngine._lock until the committed positions become live.
    /// </summary>
    public void SaveQueueOrders(IReadOnlyList<(Guid JobId, long Order)> changes)
    {
        if (changes.Count < 1 || changes.Select(c => c.JobId).Distinct().Count() != changes.Count)
            throw new ArgumentException("A queue update needs distinct existing jobs.", nameof(changes));

        lock (_gate)
        {
            using SqliteTransaction transaction = _db.BeginTransaction();
            using SqliteCommand update = _db.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE jobs SET queue_order=$order WHERE id=$id;";
            SqliteParameter order = update.Parameters.Add("$order", SqliteType.Integer);
            SqliteParameter id = update.Parameters.Add("$id", SqliteType.Text);
            foreach ((Guid jobId, long position) in changes)
            {
                order.Value = position;
                id.Value = jobId.ToString("D");
                if (update.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("A queue entry disappeared before its order could be saved.");
            }
            transaction.Commit();
        }
    }

    public IReadOnlyList<DownloadJob> LoadAll()
    {
        lock (_gate)
        {
            using SqliteCommand command = _db.CreateCommand();
            command.CommandText = $"SELECT {string.Join(',', Columns)} FROM jobs ORDER BY queue_order";
            using SqliteDataReader reader = command.ExecuteReader();
            var jobs = new List<DownloadJob>();
            while (reader.Read()) jobs.Add(Read(reader));
            return jobs;
        }
    }

    /// <summary>Removing a job and its browser request claim is one SQLite transaction.</summary>
    public void Delete(Guid id)
    {
        lock (_gate)
        {
            using SqliteTransaction tx = _db.BeginTransaction();
            using SqliteCommand command = _db.CreateCommand();
            command.Transaction = tx;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.CommandText = "DELETE FROM submissions WHERE job_id=$id;";
            command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM jobs WHERE id=$id;";
            command.ExecuteNonQuery();
            tx.Commit();
        }
    }

    private static IEnumerable<(string, object?)> Values(DownloadJob j)
    {
        yield return ("id", j.Id.ToString("D"));
        yield return ("kind", (int)j.Kind);
        yield return ("source_url", ProtectedMediaState.Protect(j.SourceUrl));
        yield return ("previous_urls", ProtectedMediaState.Protect(j.PreviousUrls));
        yield return ("file_name", j.FileName);
        yield return ("name_locked", j.NameLocked ? 1 : 0);
        yield return ("destination", j.DestinationFolder);
        yield return ("partial_path", j.PartialPath);
        yield return ("final_path", j.FinalPath);
        yield return ("pending_final_path", j.PendingFinalPath);
        yield return ("total_bytes", j.TotalBytes);
        yield return ("extents", j.Extents.Serialize());
        yield return ("committed_bytes", j.Extents.TotalBytes);
        yield return ("state", (int)j.State);
        yield return ("reason", j.Reason);
        yield return ("message", j.Message);
        yield return ("priority", (int)j.Priority);
        yield return ("queue_order", j.QueueOrder);
        yield return ("speed_limit", j.SpeedLimitBytes);
        yield return ("created_utc", Iso(j.CreatedUtc));
        yield return ("updated_utc", Iso(DateTime.UtcNow));
        yield return ("completed_utc", j.CompletedUtc is { } c ? Iso(c) : null);
        yield return ("retry_count", j.RetryCount);
        yield return ("next_retry_utc", j.NextRetryUtc is { } n ? Iso(n) : null);
        yield return ("etag", j.ETag);
        yield return ("last_modified", j.LastModified?.ToString("O", CultureInfo.InvariantCulture));
        yield return ("accepts_ranges", j.AcceptsRanges ? 1 : 0);
        yield return ("identity_level", (int)j.Identity);
        yield return ("content_type", j.ContentType);
        yield return ("category", j.Category);
        yield return ("referer", ProtectedMediaState.Protect(j.Referer));
        yield return ("user_agent", j.UserAgent);
        yield return ("expected_sha256", j.ExpectedSha256);
        yield return ("piece_map", j.PieceMapJson);
        yield return ("rescue_candidate", j.RescueCandidate ? 1 : 0);
        yield return ("rescue_mode", (int)j.RescueMode);
        yield return ("sha256_verified", j.Sha256Verified ? 1 : 0);
        yield return ("unverified_resume", j.UnverifiedResumeApproved ? 1 : 0);
        yield return ("non_resumable", j.NonResumable ? 1 : 0);
        yield return ("media_state", ProtectedMediaState.Protect(j.MediaState));
        yield return ("media_plan_json", ProtectedMediaState.Protect(j.MediaPlanJson));
        yield return ("media_format_id", j.MediaFormatId);
        yield return ("media_output", (int)j.Output);
        yield return ("pending_conversion_bytes", j.PendingConversionBytes);
        yield return ("pending_conversion_digest", j.PendingConversionDigest);
        yield return ("pending_conversion_identity", j.PendingConversionIdentity);
        yield return ("publication_copy_identity", j.PublicationCopyIdentity);
        yield return ("published_sha256", j.PublishedSha256);
        yield return ("delivery_elapsed_ms", j.DeliveryElapsedMilliseconds);
        yield return ("partial_identity", j.PartialIdentity);
        yield return ("final_resource_identity", j.FinalResourceIdentity);
        yield return ("published_file_identity", j.PublishedFileIdentity);
        yield return ("browser_proof", j.BrowserProof is null ? null : JsonSerializer.Serialize(j.BrowserProof));
        yield return ("media_metadata_json", ProtectedMediaState.Protect(j.MediaMetadataJson));
        yield return ("generation", j.Generation);
        yield return ("original_page_url", ProtectedMediaState.Protect(j.OriginalPageUrl));
        yield return ("media_provider", j.MediaProvider);
        yield return ("media_id", j.MediaId);
        yield return ("media_quality", j.MediaQuality);
        yield return ("media_thumbnail_url", ProtectedMediaState.Protect(j.MediaThumbnailUrl));
        yield return ("media_expires_at", j.MediaExpiresAt?.ToString("O", CultureInfo.InvariantCulture));
    }

    private static DownloadJob Read(SqliteDataReader r)
    {
        string Str(string c) => r.IsDBNull(r.GetOrdinal(c)) ? "" : r.GetString(r.GetOrdinal(c));
        string? NStr(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetString(r.GetOrdinal(c));
        long Int(string c) => r.IsDBNull(r.GetOrdinal(c)) ? 0 : r.GetInt64(r.GetOrdinal(c));
        long? NInt(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetInt64(r.GetOrdinal(c));
        DateTime? Date(string c) => NStr(c) is { } s ? DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime() : null;

        ExtentSet.TryParse(Str("extents"), out ExtentSet extents);   // an unparsable value yields an empty set: nothing is trusted
        return new DownloadJob
        {
            Id = Guid.Parse(Str("id")),
            Kind = (JobKind)Int("kind"),
            SourceUrl = ProtectedMediaState.Unprotect(Str("source_url")) ?? "",
            PreviousUrls = ProtectedMediaState.Unprotect(Str("previous_urls")) ?? "",
            FileName = Str("file_name"),
            NameLocked = Int("name_locked") != 0,
            DestinationFolder = Str("destination"),
            PartialPath = Str("partial_path"),
            FinalPath = NStr("final_path"),
            PendingFinalPath = NStr("pending_final_path"),
            PendingConversionBytes = NInt("pending_conversion_bytes"),
            PendingConversionDigest = NStr("pending_conversion_digest"),
            PendingConversionIdentity = NStr("pending_conversion_identity"),
            PublicationCopyIdentity = NStr("publication_copy_identity"),
            PublishedSha256 = NStr("published_sha256"),
            PartialIdentity = NStr("partial_identity"),
            FinalResourceIdentity = NStr("final_resource_identity"),
            PublishedFileIdentity = NStr("published_file_identity"),
            BrowserProof = NStr("browser_proof") is { } proof ? JsonSerializer.Deserialize<BrowserTransferProof>(proof) : null,
            TotalBytes = NInt("total_bytes"),
            Extents = extents,
            State = (JobState)Int("state"),
            Reason = Str("reason"),
            Message = Str("message"),
            Priority = (JobPriority)Int("priority"),
            QueueOrder = Int("queue_order"),
            SpeedLimitBytes = Int("speed_limit"),
            CreatedUtc = Date("created_utc") ?? DateTime.UtcNow,
            UpdatedUtc = Date("updated_utc") ?? DateTime.UtcNow,
            CompletedUtc = Date("completed_utc"),
            DeliveryElapsedMilliseconds = NInt("delivery_elapsed_ms"),
            RetryCount = (int)Int("retry_count"),
            NextRetryUtc = Date("next_retry_utc"),
            ETag = NStr("etag"),
            LastModified = NStr("last_modified") is { } lm ? DateTimeOffset.Parse(lm, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
            AcceptsRanges = Int("accepts_ranges") != 0,
            Identity = (IdentityLevel)Int("identity_level"),
            ContentType = NStr("content_type"),
            Category = Str("category"),
            Referer = ProtectedMediaState.Unprotect(NStr("referer")),
            UserAgent = NStr("user_agent"),
            ExpectedSha256 = NStr("expected_sha256"),
            PieceMapJson = NStr("piece_map"),
            RescueCandidate = Int("rescue_candidate") != 0,
            RescueMode = (RescueMode)Int("rescue_mode"),
            Sha256Verified = Int("sha256_verified") != 0,
            UnverifiedResumeApproved = Int("unverified_resume") != 0,
            NonResumable = Int("non_resumable") != 0,
            MediaState = ProtectedMediaState.Unprotect(NStr("media_state")),
            MediaPlanJson = ProtectedMediaState.Unprotect(NStr("media_plan_json")),
            MediaMetadataJson = ProtectedMediaState.Unprotect(NStr("media_metadata_json")),
            MediaFormatId = NStr("media_format_id"),
            Output = (MediaOutput)Int("media_output"),
            Generation = Int("generation"),
            OriginalPageUrl = ProtectedMediaState.Unprotect(NStr("original_page_url")),
            MediaProvider = NStr("media_provider"),
            MediaId = NStr("media_id"),
            MediaQuality = NStr("media_quality"),
            MediaThumbnailUrl = ProtectedMediaState.Unprotect(NStr("media_thumbnail_url")),
            MediaExpiresAt = NStr("media_expires_at") is { } expiry ? DateTimeOffset.Parse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null
        };
    }

    // idempotent browser submissions
    public bool TryGetSubmission(string requestId, out Guid jobId)
    {
        lock (_gate)
        {
            using SqliteCommand command = _db.CreateCommand();
            command.CommandText = "SELECT job_id FROM submissions WHERE request_id=$r";
            command.Parameters.AddWithValue("$r", requestId);
            object? value = command.ExecuteScalar();
            jobId = value is string s ? Guid.Parse(s) : Guid.Empty;
            return value is not null;
        }
    }

    // New jobs use Save(job, submissionId); adoption can claim an existing job separately.
    public void RecordSubmission(string requestId, Guid jobId)
    {
        lock (_gate)
            Execute("INSERT OR IGNORE INTO submissions(request_id, job_id, created_utc) VALUES($r,$j,$t);",
                ("$r", requestId), ("$j", jobId.ToString("D")), ("$t", Iso(DateTime.UtcNow)));
    }

    // bounded event log (privacy: callers pass redacted text)
    public void LogEvent(Guid? jobId, string kind, string detail, DateTime? utc = null, bool once = false)
    {
        lock (_gate)
        {
            const string insert = "INSERT INTO events(job_id, utc, kind, detail) SELECT $j,$t,$k,$d";
            const string onceOnly = " WHERE NOT EXISTS (SELECT 1 FROM events WHERE job_id=$j AND kind=$k)";
            Execute(insert + (once ? onceOnly : ""), ("$j", jobId?.ToString("D")), ("$t", Iso(utc ?? DateTime.UtcNow)),
                ("$k", kind), ("$d", detail));
            if (++_eventsSinceTrim >= 200)
            {
                _eventsSinceTrim = 0;
                Execute("DELETE FROM events WHERE id <= (SELECT MAX(id) FROM events) - 5000;");
            }
        }
    }

    public IReadOnlyList<string> ReadEvents(int max = 1000)
    {
        lock (_gate)
        {
            using SqliteCommand command = _db.CreateCommand();
            command.CommandText = "SELECT utc, kind, job_id, detail FROM events ORDER BY id DESC LIMIT $n";
            command.Parameters.AddWithValue("$n", max);
            using SqliteDataReader reader = command.ExecuteReader();
            var lines = new List<string>();
            while (reader.Read())
                lines.Add($"{reader.GetString(0)}  {reader.GetString(1),-12} {(reader.IsDBNull(2) ? "-" : reader.GetString(2)[..8])}  {reader.GetString(3)}");
            lines.Reverse();
            return lines;
        }
    }

    // helpers
    private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private void Execute(string sql, params (string, object?)[] args)
    {
        using SqliteCommand command = _db.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using SqliteCommand command = _db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }
}
