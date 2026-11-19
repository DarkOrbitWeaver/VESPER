using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using VESPER;

namespace Vesper.Setup;

internal enum SetupMode { Install, Repair, Uninstall }
internal enum InstallPhase { Staging, Switching, Committed }

internal sealed record InstallJournal(Guid Id, SetupMode Mode, InstallPhase Phase, bool HadApp,
    RegistrationSnapshot? Registration = null, bool DeleteUserData = false);

internal sealed class InstallTransaction(InstallPaths paths, IInstallRegistration registration, Func<IDisposable>? lockApp = null)
{
    private const int MaxJournalBytes = 64 * 1024 * 1024;
    private const int AppRegistrationValueCount = 9;
    private const int MaxRegistrationFileBytes = 1024 * 1024;
    private const int MaxRegistryValueBytes = 1024 * 1024;
    private const string SettingsFile = "settings.json";
    private const string LegacySettingsFile = "ui-preview-settings.json";
    private const string PodcastFeedsFile = "podcast-feeds.json";
    private const string HistoryDatabase = "vesper.db";

    internal void Run(SetupMode mode, InstallPayload? payload, string? setupExe, IProgress<InstallProgress>? progress,
        CancellationToken ct, bool deleteUserData = false)
    {
        CheckPath(paths.Lock);
        Directory.CreateDirectory(paths.Programs);
        using var setupLock = new FileStream(paths.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.DeleteOnClose);
        RecoverPending();
        ct.ThrowIfCancellationRequested();
        CheckPath(paths.App);
        bool hadApp = Directory.Exists(paths.App);
        if (hadApp && !File.Exists(paths.Executable) && !File.Exists(Path.Combine(paths.App, InstallPayload.Marker)))
            throw new IOException("The installation folder contains files from another application.");
        // Catch a running instance before verifying the package. Recheck under the swap lock below.
        lockApp?.Invoke().Dispose();

        bool removeUserData = mode == SetupMode.Uninstall && deleteUserData;
        var journal = new InstallJournal(Guid.NewGuid(), mode, InstallPhase.Staging, hadApp,
            DeleteUserData: removeUserData);
        string stage = Stage(journal);
        string backup = Backup(journal);
        Save(journal);
        IDisposable? appLock = null;
        try
        {
            if (mode != SetupMode.Uninstall)
            {
                if (payload is null || setupExe is null) throw new InvalidDataException("The setup package is missing.");
                CheckPath(stage);
                Directory.CreateDirectory(stage);
                payload.Extract(stage, progress, ct);
                progress?.Report(new("Preparing repair and uninstall…"));
                CopySetup(setupExe, Path.Combine(stage, InstallPayload.SetupName), ct);
                WriteAtomic(Path.Combine(stage, InstallPayload.Marker), journal.Id.ToByteArray());
            }

            ct.ThrowIfCancellationRequested();
            appLock = lockApp?.Invoke();
            CheckPath(backup);
            journal = journal with
            {
                Phase = InstallPhase.Switching,
                Registration = registration.Capture()
            };
            ValidateRegistrationSnapshot(journal.Registration);
            if (journal.DeleteUserData) _ = FindUserDataFiles(journal);
            Save(journal);
            progress?.Report(new(mode == SetupMode.Uninstall ? "Removing VESPER…" : "Registering VESPER and its browser helper…"));
            if (hadApp) Directory.Move(paths.App, backup);
            if (mode == SetupMode.Uninstall) registration.Uninstall();
            else
            {
                Directory.Move(stage, paths.App);
                registration.Install(payload!.Version);
            }
            if (journal.DeleteUserData) StageUserDataForDeletion(journal);
            ct.ThrowIfCancellationRequested();
            Save(journal with { Phase = InstallPhase.Committed });
        }
        catch (Exception failure)
        {
            try { Recover(); }
            catch (Exception recovery)
            {
                throw new IOException("Setup could not finish restoring the previous installation. Run setup again after closing VESPER.",
                    new AggregateException(failure, recovery));
            }
            throw;
        }
        finally { appLock?.Dispose(); }

        // A locked backup can be cleaned on the next setup run. The live install is already committed.
        try { Recover(); }
        catch (IOException) when (!removeUserData) { }
        catch (UnauthorizedAccessException) when (!removeUserData) { }
    }

    private void RecoverPending()
    {
        if (!File.Exists(paths.Journal)) return;
        using var appLock = lockApp?.Invoke();
        Recover();
    }

    private void Recover()
    {
        CheckPath(paths.Journal);
        if (!File.Exists(paths.Journal)) return;
        using var stream = File.OpenRead(paths.Journal);
        if (stream.Length > MaxJournalBytes) throw new InvalidDataException("The setup recovery record is invalid.");
        var journal = JsonSerializer.Deserialize<InstallJournal>(stream);
        stream.Dispose();
        if (journal is null || journal.Id == Guid.Empty || !Enum.IsDefined(journal.Phase) || !Enum.IsDefined(journal.Mode))
            throw new InvalidDataException("The setup recovery record is invalid.");
        if (journal.DeleteUserData && journal.Mode != SetupMode.Uninstall)
            throw new InvalidDataException("The setup recovery record is invalid.");
        if (journal.Phase == InstallPhase.Switching) ValidateRegistrationSnapshot(journal.Registration);
        string stage = Stage(journal);
        string backup = Backup(journal);
        CheckPath(paths.App);
        CheckPath(stage);
        CheckPath(backup);
        if (journal.Phase == InstallPhase.Switching)
        {
            if (IsNewInstallation(paths.App, journal.Id)) Directory.Move(paths.App, stage);
            if (Directory.Exists(backup)) Directory.Move(backup, paths.App);
            registration.Restore(journal.Registration!);
            if (journal.DeleteUserData) RestoreStagedUserData(journal);
        }
        else if (journal.Phase == InstallPhase.Committed)
        {
            DeleteTree(backup);
            if (journal.DeleteUserData) DeleteStagedUserData(journal);
        }
        DeleteTree(stage);
        File.Delete(paths.Journal);
    }

    private static void ValidateRegistrationSnapshot(RegistrationSnapshot? snapshot)
    {
        int expectedCount = NativeHostRegistration.BrowserKeys.Length + AppRegistrationValueCount;
        if (snapshot?.Values is null || snapshot.Values.Length != expectedCount
            || snapshot.Manifest?.Length > MaxRegistrationFileBytes || snapshot.Shortcut?.Length > MaxRegistrationFileBytes)
            throw new InvalidDataException("The setup recovery record is incomplete.");

        foreach (SavedValue? saved in snapshot.Values)
            if (!IsValidSavedValue(saved)) throw new InvalidDataException("The setup recovery record is incomplete.");
    }

    private static bool IsValidSavedValue(SavedValue? saved)
    {
        if (saved is null) return false;
        if (saved.Value is not JsonElement value) return saved.Kind == RegistryValueKind.Unknown;

        return saved.Kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString =>
                IsRegistryString(value),
            RegistryValueKind.DWord => value.TryGetInt32(out _),
            RegistryValueKind.QWord => value.TryGetInt64(out _),
            RegistryValueKind.Binary or RegistryValueKind.None => IsBase64(value),
            RegistryValueKind.MultiString => IsStringArray(value),
            _ => false
        };
    }

    private static bool IsBase64(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return false;
        try
        {
            return value.GetBytesFromBase64().Length <= MaxRegistryValueBytes;
        }
        catch (FormatException) { return false; }
    }

    private static bool IsRegistryString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text) return false;
        return ((long)text.Length + 1) * sizeof(char) <= MaxRegistryValueBytes;
    }

    private static bool IsStringArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return false;
        long bytes = sizeof(char);
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not string text) return false;
            bytes += ((long)text.Length + 1) * sizeof(char);
            if (bytes > MaxRegistryValueBytes) return false;
        }
        return true;
    }

    private string[] FindUserDataFiles(InstallJournal journal)
    {
        string root = UserDataRoot();
        if (!Directory.Exists(root))
        {
            if (File.Exists(root)) throw new IOException("The VESPER user-data path is not a directory.");
            return [];
        }

        string stage = UserDataStage(journal, root);
        CheckUserDataPath(root, stage);
        if (File.Exists(stage) || Directory.Exists(stage))
            throw new IOException("The VESPER user-data recovery folder already exists.");

        var files = new List<string>();
        foreach (FileSystemInfo entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if (!IsVesperUserDataFile(entry.Name)) continue;
            if (entry is not FileInfo)
                throw new IOException("A VESPER settings or history path is not a file.");
            CheckUserDataFile(root, entry.FullName);
            files.Add(entry.FullName);
        }
        return files.ToArray();
    }

    private void StageUserDataForDeletion(InstallJournal journal)
    {
        string[] files = FindUserDataFiles(journal);
        if (files.Length == 0) return;

        string root = UserDataRoot();
        string stage = UserDataStage(journal, root);
        Directory.CreateDirectory(stage);
        CheckUserDataDirectory(root, stage);
        foreach (string file in files)
        {
            CheckUserDataFile(root, file);
            string target = Path.Combine(stage, Path.GetFileName(file));
            CheckUserDataPath(root, target);
            if (File.Exists(target) || Directory.Exists(target))
                throw new IOException("The VESPER user-data recovery folder contains an unexpected file.");
            File.Move(file, target);
        }
    }

    private void RestoreStagedUserData(InstallJournal journal)
    {
        string root = UserDataRoot();
        string stage = UserDataStage(journal, root);
        CheckUserDataPath(root, stage);
        if (!Directory.Exists(stage))
        {
            if (File.Exists(stage)) throw new IOException("The VESPER user-data recovery folder is invalid.");
            return;
        }

        CheckUserDataDirectory(root, stage);
        string[] files = GetStagedUserDataFiles(root, stage);
        foreach (string file in files)
        {
            string target = Path.Combine(root, Path.GetFileName(file));
            CheckUserDataPath(root, target);
            if (File.Exists(target) || Directory.Exists(target))
                throw new IOException("Setup cannot restore VESPER user data because a file already exists at its original path.");
        }

        foreach (string file in files)
        {
            string target = Path.Combine(root, Path.GetFileName(file));
            File.Move(file, target);
        }
        Directory.Delete(stage);
    }

    private void DeleteStagedUserData(InstallJournal journal)
    {
        string root = UserDataRoot();
        string stage = UserDataStage(journal, root);
        CheckUserDataPath(root, stage);
        if (!Directory.Exists(stage))
        {
            if (File.Exists(stage)) throw new IOException("The VESPER user-data recovery folder is invalid.");
            return;
        }

        CheckUserDataDirectory(root, stage);
        string[] files = GetStagedUserDataFiles(root, stage);
        foreach (string file in files) File.Delete(file);
        Directory.Delete(stage);
    }

    private static string[] GetStagedUserDataFiles(string root, string stage)
    {
        var files = new List<string>();
        foreach (FileSystemInfo entry in new DirectoryInfo(stage).EnumerateFileSystemInfos())
        {
            if (entry is not FileInfo || !IsVesperUserDataFile(entry.Name))
                throw new IOException("The VESPER user-data recovery folder contains an unexpected entry.");
            CheckUserDataFile(root, entry.FullName, stage);
            files.Add(entry.FullName);
        }
        return files.ToArray();
    }

    private string UserDataRoot()
    {
        string root = NormalizeDirectoryPath(paths.Data);
        string volumeRoot = Path.GetPathRoot(root) ?? "";
        if (root.Equals(volumeRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(root).Equals("VESPER", StringComparison.OrdinalIgnoreCase)
            || IsWithinPath(root, paths.App) || IsWithinPath(paths.App, root)
            || IsWithinPath(root, paths.Programs) || IsWithinPath(paths.Programs, root))
            throw new IOException("The VESPER user-data path is unsafe.");
        CheckPath(root);
        return root;
    }

    private static string UserDataStage(InstallJournal journal, string root) =>
        Path.Combine(root, ".vesper-uninstall-" + journal.Id.ToString("N"));

    private static void CheckUserDataFile(string root, string path, string? parent = null)
    {
        CheckUserDataPath(root, path);
        parent ??= root;
        if (!NormalizeDirectoryPath(Path.GetDirectoryName(Path.GetFullPath(path))!).Equals(
                NormalizeDirectoryPath(parent), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A VESPER user-data file is outside its store.");
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Setup cannot remove a linked VESPER user-data file.");
    }

    private static void CheckUserDataDirectory(string root, string path)
    {
        CheckUserDataPath(root, path);
        if (!NormalizeDirectoryPath(Path.GetDirectoryName(Path.GetFullPath(path))!).Equals(
                NormalizeDirectoryPath(root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The VESPER user-data recovery folder is outside its store.");
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new IOException("The VESPER user-data recovery folder is invalid.");
    }

    private static void CheckUserDataPath(string root, string path)
    {
        string fullRoot = NormalizeDirectoryPath(root);
        string fullPath = Path.GetFullPath(path);
        if (!IsWithinPath(fullRoot, fullPath) || fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A VESPER user-data path resolves outside its store.");
        CheckPath(fullPath);
    }

    private static bool IsWithinPath(string root, string path)
    {
        string fullRoot = NormalizeDirectoryPath(root);
        string fullPath = Path.GetFullPath(path);
        if (fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsVesperUserDataFile(string name)
    {
        if (name.Equals(SettingsFile, StringComparison.OrdinalIgnoreCase)
            || name.Equals(SettingsFile + ".tmp", StringComparison.OrdinalIgnoreCase)
            || name.Equals(LegacySettingsFile, StringComparison.OrdinalIgnoreCase)
            || name.Equals(LegacySettingsFile + ".tmp", StringComparison.OrdinalIgnoreCase)
            || name.Equals(PodcastFeedsFile, StringComparison.OrdinalIgnoreCase)
            || IsSettingsBackup(name, SettingsFile) || IsSettingsBackup(name, LegacySettingsFile))
            return true;

        return IsHistoryFile(name);
    }

    private static bool IsSettingsBackup(string name, string baseName)
    {
        string prefix = baseName + ".bad";
        if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (!name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(name.AsSpan(prefix.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int suffix)
            && suffix > 0;
    }

    private static bool IsHistoryFile(string name)
    {
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return IsHistoryBase(name[..^suffix.Length]);
        }
        return IsHistoryBase(name) || IsQuarantineMarkerTemporary(name);
    }

    private static bool IsHistoryBase(string name)
    {
        if (name.Equals(HistoryDatabase, StringComparison.OrdinalIgnoreCase)
            || name.Equals(HistoryDatabase + ".quarantine-pending", StringComparison.OrdinalIgnoreCase))
            return true;

        string corruptPrefix = HistoryDatabase + ".corrupt-";
        if (name.StartsWith(corruptPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string suffix = name[corruptPrefix.Length..];
            return suffix.Length == 47 && DateTime.TryParseExact(suffix.AsSpan(0, 14), "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                && suffix[14] == '-'
                && Guid.TryParseExact(suffix.AsSpan(15), "N", out _);
        }

        string backupPrefix = HistoryDatabase + ".bak-v";
        if (!name.StartsWith(backupPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        string backup = name[backupPrefix.Length..];
        int temporaryIndex = backup.IndexOf(".new-", StringComparison.OrdinalIgnoreCase);
        if (temporaryIndex >= 0)
        {
            if (!Guid.TryParseExact(backup.AsSpan(temporaryIndex + 5), "N", out _)) return false;
            backup = backup[..temporaryIndex];
        }

        int separator = backup.IndexOf('-');
        ReadOnlySpan<char> version = separator < 0 ? backup.AsSpan() : backup.AsSpan(0, separator);
        if (!int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out int versionNumber) || versionNumber <= 0)
            return false;
        if (separator < 0) return true;

        string unique = backup[(separator + 1)..];
        return unique.Length == 23 && DateTime.TryParseExact(unique.AsSpan(0, 14), "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && unique[14] == '-' && unique.AsSpan(15).ToString().All(Uri.IsHexDigit);
    }

    private static bool IsQuarantineMarkerTemporary(string name)
    {
        const string suffix = ".marker.tmp";
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && IsHistoryBase(name[..^suffix.Length]);
    }

    private string Stage(InstallJournal journal) => paths.App + $".stage-{journal.Id:N}";
    private string Backup(InstallJournal journal) => paths.App + $".rollback-{journal.Id:N}";
    private void Save(InstallJournal journal)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
        if (bytes.Length > MaxJournalBytes)
            throw new InvalidDataException("The setup recovery record is too large to save safely.");
        WriteAtomic(paths.Journal, bytes);
    }

    private static bool IsNewInstallation(string path, Guid id)
    {
        string marker = Path.Combine(path, InstallPayload.Marker);
        CheckPath(marker);
        var info = new FileInfo(marker);
        return info.Exists && info.Length == 16 && new Guid(File.ReadAllBytes(marker)) == id;
    }

    private static void CopySetup(string source, string destination, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var to = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count;
        while ((count = from.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            to.Write(buffer, 0, count);
            hash.AppendData(buffer, 0, count);
        }
        to.Position = 0;
        if (!hash.GetHashAndReset().AsSpan().SequenceEqual(SHA256.HashData(to)))
            throw new InvalidDataException("The repair and uninstall program failed verification.");
    }

    internal static void CheckPath(string path)
    {
        for (string? part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Setup cannot use a link at {part}.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void WriteAtomic(string path, byte[] content)
    {
        CheckPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static void DeleteTree(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) return;
        foreach (var child in directory.EnumerateFileSystemInfos())
        {
            if (child is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
                DeleteTree(sub.FullName);
            else child.Delete();
        }
        directory.Delete();
    }
}
