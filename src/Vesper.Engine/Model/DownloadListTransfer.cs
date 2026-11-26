using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vesper.Engine;

public enum DownloadListState
{
    Queued,
    Paused
}

public sealed record DownloadListEntry(string Url, JobKind Kind, DownloadListState State);

public sealed record DownloadListReadResult(IReadOnlyList<DownloadListEntry> Entries, string? Error)
{
    public bool Success => Error is null;
}

public sealed record DownloadListExportResult(IReadOnlyList<DownloadListEntry> Entries, int CompletedSkipped,
    int BrowserOwnedSkipped, int UnsupportedSkipped, int UnsafeUrlSkipped, int DuplicateSkipped, int LimitSkipped)
{
    public int NonPortableMediaSelectionSkipped { get; init; }
    public int Skipped => CompletedSkipped + BrowserOwnedSkipped + UnsupportedSkipped
        + UnsafeUrlSkipped + DuplicateSkipped + LimitSkipped + NonPortableMediaSelectionSkipped;
}

/// <summary>Versioned JSON and CSV formats for transferring simple download lists.</summary>
public static class DownloadListTransfer
{
    public const int MaxInputBytes = 20 * 1024 * 1024;
    public const int MaxEntries = 2000;
    public const int MaxUrlLength = UrlPolicy.MaxUrlLength;

    private const string Format = "vesper-download-list";
    private const int Version = 1;
    private const string CsvMarker = "#VESPER-DOWNLOAD-LIST,1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] CredentialQueryNames =
    [
        "token", "secret", "auth", "credential", "signature", "sig", "jwt",
        "password", "cookie", "session", "apikey", "privatekey", "secretkey", "bearer"
    ];

    private sealed record JsonList(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("downloads")] IReadOnlyList<JsonEntry> Downloads);

    private sealed record JsonEntry(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("state")] string State);

    public static DownloadListExportResult CreateExport(IEnumerable<JobSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var entries = new List<DownloadListEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int completedSkipped = 0;
        int browserOwnedSkipped = 0;
        int unsupportedSkipped = 0;
        int nonPortableMediaSelectionSkipped = 0;
        int unsafeUrlSkipped = 0;
        int duplicateSkipped = 0;
        int limitSkipped = 0;
        foreach (JobSnapshot snapshot in snapshots)
        {
            if (entries.Count >= MaxEntries)
            {
                limitSkipped++;
                continue;
            }

            if (snapshot.IsUnacceptedBrowserCapture)
            {
                browserOwnedSkipped++;
                continue;
            }
            if (snapshot.State == JobState.Completed)
            {
                completedSkipped++;
                continue;
            }
            if (snapshot.HasNonPortableMediaSelection)
            {
                nonPortableMediaSelectionSkipped++;
                continue;
            }
            if (snapshot.Kind is not (JobKind.Http or JobKind.Hls) || snapshot.Output != MediaOutput.Original)
            {
                unsupportedSkipped++;
                continue;
            }
            if (!TryNormalizeUrl(snapshot.SourceUrl, out string url, out _))
            {
                unsafeUrlSkipped++;
                continue;
            }

            string key = $"{snapshot.Kind}:{UrlPolicy.Normalize(new Uri(url))}";
            if (!seen.Add(key))
            {
                duplicateSkipped++;
                continue;
            }

            DownloadListState state = snapshot.State == JobState.Paused || JobStates.NeedsUser(snapshot.State)
                ? DownloadListState.Paused
                : DownloadListState.Queued;
            entries.Add(new DownloadListEntry(url, snapshot.Kind, state));
        }

        return new DownloadListExportResult(entries, completedSkipped, browserOwnedSkipped, unsupportedSkipped,
            unsafeUrlSkipped, duplicateSkipped, limitSkipped)
        { NonPortableMediaSelectionSkipped = nonPortableMediaSelectionSkipped };
    }

    public static string WriteJson(IEnumerable<DownloadListEntry> entries)
    {
        DownloadListEntry[] checkedEntries = ValidateForWrite(entries);
        var file = new JsonList(Format, Version, checkedEntries.Select(ToJsonEntry).ToArray());
        return JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string WriteCsv(IEnumerable<DownloadListEntry> entries)
    {
        DownloadListEntry[] checkedEntries = ValidateForWrite(entries);
        var output = new StringBuilder(CsvMarker).Append("\r\nurl,kind,state\r\n");
        foreach (DownloadListEntry entry in checkedEntries)
        {
            output.Append(QuoteCsv(entry.Url)).Append(',')
                .Append(entry.Kind).Append(',')
                .Append(entry.State).Append("\r\n");
        }

        return output.ToString();
    }

    public static DownloadListReadResult Read(string? text, string? extension)
    {
        if (string.IsNullOrEmpty(text)) return Failure("The file is empty or unreadable.");
        if (string.IsNullOrWhiteSpace(extension)) return Failure("Choose a VESPER JSON or CSV list.");
        if (text[0] == '\uFEFF') text = text[1..];

        try
        {
            if (StrictUtf8.GetByteCount(text) > MaxInputBytes)
                return Failure("The list is too large to import.");
        }
        catch (EncoderFallbackException)
        {
            return Failure("The list contains invalid text encoding.");
        }

        return extension.ToLowerInvariant() switch
        {
            ".json" => ReadJson(text),
            ".csv" => ReadCsv(text),
            _ => Failure("Choose a VESPER JSON or CSV list.")
        };
    }

    private static DownloadListReadResult ReadJson(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (!TryReadProperties(root, ["format", "version", "downloads"], out string rootError))
                return Failure(rootError);
            if (!root.TryGetProperty("format", out JsonElement format) || format.ValueKind != JsonValueKind.String
                || format.GetString() != Format
                || !root.TryGetProperty("version", out JsonElement version) || !version.TryGetInt32(out int foundVersion)
                || foundVersion != Version
                || !root.TryGetProperty("downloads", out JsonElement downloads) || downloads.ValueKind != JsonValueKind.Array)
                return Failure("The JSON list version or structure is not supported.");
            if (downloads.GetArrayLength() > MaxEntries)
                return Failure($"A list can contain at most {MaxEntries} downloads.");

            var entries = new List<DownloadListEntry>();
            foreach (JsonElement item in downloads.EnumerateArray())
            {
                if (!TryReadProperties(item, ["url", "kind", "state"], out string entryError))
                    return Failure(entryError);
                if (!item.TryGetProperty("url", out JsonElement url) || url.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("kind", out JsonElement kind) || kind.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("state", out JsonElement state) || state.ValueKind != JsonValueKind.String)
                    return Failure("Each JSON entry needs a URL, kind, and state.");
                if (!TryCreateEntry(url.GetString(), kind.GetString(), state.GetString(), out DownloadListEntry? entry, out string error))
                    return Failure($"Entry {entries.Count + 1}: {error}");
                entries.Add(entry);
            }

            return ValidateDistinct(entries);
        }
        catch (JsonException)
        {
            return Failure("The JSON list is malformed.");
        }
    }

    private static DownloadListReadResult ReadCsv(string text)
    {
        if (!TryReadCsvRows(text, out List<string[]> rows, out string parseError))
            return Failure(parseError);
        if (rows.Count < 2 || rows[0].Length != 2 || rows[0][0] != "#VESPER-DOWNLOAD-LIST" || rows[0][1] != "1"
            || rows[1].Length != 3 || !HeaderEquals(rows[1], "url", "kind", "state"))
            return Failure("The CSV list marker, version, or header is not supported.");
        if (rows.Count - 2 > MaxEntries)
            return Failure($"A list can contain at most {MaxEntries} downloads.");

        var entries = new List<DownloadListEntry>(rows.Count - 2);
        for (int i = 2; i < rows.Count; i++)
        {
            if (rows[i].Length == 1 && rows[i][0].Length == 0) continue;
            if (rows[i].Length != 3)
                return Failure($"Row {i + 1} needs a URL, kind, and state.");
            if (!TryCreateEntry(rows[i][0], rows[i][1], rows[i][2], out DownloadListEntry? entry, out string error))
                return Failure($"Row {i + 1}: {error}");
            entries.Add(entry);
        }

        return ValidateDistinct(entries);
    }

    private static bool TryCreateEntry(string? urlText, string? kindText, string? stateText,
        out DownloadListEntry entry, out string error)
    {
        entry = null!;
        error = "";
        if (!TryNormalizeUrl(urlText, out string url, out error)) return false;
        JobKind kind;
        if (string.Equals(kindText, nameof(JobKind.Http), StringComparison.OrdinalIgnoreCase))
            kind = JobKind.Http;
        else if (string.Equals(kindText, nameof(JobKind.Hls), StringComparison.OrdinalIgnoreCase))
            kind = JobKind.Hls;
        else
        {
            error = "Only HTTP file and HLS links can be imported.";
            return false;
        }

        DownloadListState state;
        if (string.Equals(stateText, nameof(DownloadListState.Queued), StringComparison.OrdinalIgnoreCase))
            state = DownloadListState.Queued;
        else if (string.Equals(stateText, nameof(DownloadListState.Paused), StringComparison.OrdinalIgnoreCase))
            state = DownloadListState.Paused;
        else
        {
            error = "The state must be Queued or Paused.";
            return false;
        }

        entry = new DownloadListEntry(url, kind, state);
        return true;
    }

    private static bool TryNormalizeUrl(string? text, out string normalized, out string error)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(text) || !string.Equals(text, text.Trim(), StringComparison.Ordinal))
        {
            error = "The URL must not have leading or trailing whitespace.";
            return false;
        }
        if (!HasValidEscapes(text))
        {
            error = "The URL contains an incomplete percent escape.";
            return false;
        }
        if (!UrlPolicy.TryParse(text, out Uri uri, out error)) return false;
        if (uri.AbsoluteUri.Length > MaxUrlLength)
        {
            error = "The URL is too long.";
            return false;
        }
        if (HasCredentialData(uri))
        {
            error = "The URL contains a query parameter that may grant access. Remove it before transferring this list.";
            return false;
        }

        normalized = uri.AbsoluteUri;
        error = "";
        return true;
    }

    private static bool HasValidEscapes(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '%') continue;
            if (i + 2 >= text.Length || !Uri.IsHexDigit(text[i + 1]) || !Uri.IsHexDigit(text[i + 2]))
                return false;
            i += 2;
        }
        return true;
    }

    private static bool HasCredentialData(Uri uri)
    {
        foreach (string component in new[] { uri.Query, uri.Fragment })
        {
            if (string.IsNullOrEmpty(component)) continue;
            foreach (string part in component.TrimStart('?', '#').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string rawName = part.Split('=', 2)[0].Replace('+', ' ');
                string name;
                try { name = Uri.UnescapeDataString(rawName).ToLowerInvariant(); }
                catch (UriFormatException) { return true; }
                string compact = new(name.Where(char.IsLetterOrDigit).ToArray());
                if (compact == "key" || compact.EndsWith("key", StringComparison.Ordinal)
                    || CredentialQueryNames.Any(suspicious => compact.Contains(suspicious, StringComparison.Ordinal)))
                    return true;
            }
        }

        return false;
    }

    private static DownloadListEntry[] ValidateForWrite(IEnumerable<DownloadListEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        DownloadListEntry[] values = entries.ToArray();
        if (values.Length > MaxEntries) throw new ArgumentOutOfRangeException(nameof(entries), $"A list can contain at most {MaxEntries} downloads.");

        var normalized = new List<DownloadListEntry>(values.Length);
        foreach (DownloadListEntry? value in values)
        {
            if (value is null) throw new ArgumentException("The list contains an invalid entry.", nameof(entries));
            if (!TryCreateEntry(value.Url, value.Kind.ToString(), value.State.ToString(), out DownloadListEntry? entry, out string error))
                throw new ArgumentException(error, nameof(entries));
            normalized.Add(entry);
        }

        DownloadListReadResult validated = ValidateDistinct(normalized);
        if (!validated.Success) throw new ArgumentException(validated.Error, nameof(entries));
        return [.. validated.Entries];
    }

    private static JsonEntry ToJsonEntry(DownloadListEntry entry) =>
        new(entry.Url, entry.Kind.ToString(), entry.State.ToString());

    private static string QuoteCsv(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0
        ? value
        : '"' + value.Replace("\"", "\"\"") + '"';

    private static bool TryReadProperties(JsonElement element, string[] allowed, out string error)
    {
        error = "The JSON list has an unsupported or duplicate field.";
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal)) return false;
        error = "";
        return true;
    }

    private static bool TryReadCsvRows(string text, out List<string[]> rows, out string error)
    {
        rows = [];
        error = "The CSV list is malformed.";
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        bool closedQuote = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                    continue;
                }
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                    closedQuote = true;
                }
                continue;
            }

            if (closedQuote && c is not (',' or '\r' or '\n')) return false;
            if (c == '"')
            {
                if (field.Length != 0 || closedQuote) return false;
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
                closedQuote = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add([.. row]);
                row.Clear();
                closedQuote = false;
                if (rows.Count > MaxEntries + 2) return false;
            }
            else
            {
                field.Append(c);
            }
        }

        if (quoted) return false;
        if (field.Length > 0 || row.Count > 0 || closedQuote)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }
        error = "";
        return true;
    }

    private static bool HeaderEquals(string[] fields, string first, string second, string third) =>
        fields[0].Equals(first, StringComparison.OrdinalIgnoreCase)
        && fields[1].Equals(second, StringComparison.OrdinalIgnoreCase)
        && fields[2].Equals(third, StringComparison.OrdinalIgnoreCase);

    private static DownloadListReadResult ValidateDistinct(List<DownloadListEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (DownloadListEntry entry in entries)
        {
            Uri uri = new(entry.Url);
            string key = $"{entry.Kind}:{UrlPolicy.Normalize(uri)}";
            if (!seen.Add(key)) return Failure("The list contains a duplicate URL and kind.");
        }
        return new DownloadListReadResult(entries, null);
    }

    private static DownloadListReadResult Failure(string error) => new([], error);
}
