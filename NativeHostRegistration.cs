using System.Text.Json;

namespace VESPER;

internal enum NativeHostRegistrationStatus
{
    NotRegistered,
    Registered,
    RepairRequired,
    AnotherCopyRegistered,
    Unknown
}

internal static class NativeHostRegistration
{
    public const string HostName = "com.vesper.downloader";
    public const string ExtensionId = "aidnpdcnkhgojimnkcgieebdjdicfemi";
    public const string BrowserList = "Chrome, Microsoft Edge, and Brave";

    public static readonly string[] BrowserKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName,
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + HostName
    ];

    public static string ManifestPath(string dataDirectory) =>
        Path.Combine(dataDirectory, "native-host", HostName + ".json");

    public static string Manifest(string executable) => JsonSerializer.Serialize(new
    {
        name = HostName,
        description = "VESPER download manager",
        path = executable,
        type = "stdio",
        allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" }
    }, new JsonSerializerOptions { WriteIndented = true });

    public static NativeHostRegistrationStatus GetRegistrationStatus(
        IReadOnlyList<string?> registeredManifestPaths,
        string? manifestContents,
        string expectedManifestPath,
        string expectedExecutablePath,
        Func<string, bool> hostExists)
    {
        if (registeredManifestPaths.Count != BrowserKeys.Length
            || !TryNormalizePath(expectedManifestPath, out string normalizedManifestPath)
            || !TryNormalizePath(expectedExecutablePath, out string normalizedExecutablePath))
            return NativeHostRegistrationStatus.Unknown;

        bool[] registeredHere = new bool[registeredManifestPaths.Count];
        for (int i = 0; i < registeredManifestPaths.Count; i++)
        {
            string? registeredPath = registeredManifestPaths[i];
            if (string.IsNullOrWhiteSpace(registeredPath)) continue;
            if (!TryNormalizePath(registeredPath, out string normalizedRegisteredPath))
                return NativeHostRegistrationStatus.Unknown;
            if (!PathEquals(normalizedRegisteredPath, normalizedManifestPath))
                return NativeHostRegistrationStatus.AnotherCopyRegistered;
            registeredHere[i] = true;
        }

        if (!registeredHere.Any(registered => registered))
            return NativeHostRegistrationStatus.NotRegistered;

        if (!TryReadManifestPath(manifestContents, out string? manifestExecutablePath))
            return NativeHostRegistrationStatus.RepairRequired;

        if (!PathEquals(manifestExecutablePath!, normalizedExecutablePath))
            return hostExists(manifestExecutablePath!)
                ? NativeHostRegistrationStatus.AnotherCopyRegistered
                : NativeHostRegistrationStatus.RepairRequired;

        if (!hostExists(normalizedExecutablePath)
            || !IsValidManifest(manifestContents!, normalizedExecutablePath)
            || registeredHere.Any(registered => !registered))
            return NativeHostRegistrationStatus.RepairRequired;

        return NativeHostRegistrationStatus.Registered;
    }

    public static bool TryReadManifestPath(string? manifestContents, out string? executablePath)
    {
        executablePath = null;
        if (string.IsNullOrWhiteSpace(manifestContents)) return false;

        try
        {
            using JsonDocument manifest = JsonDocument.Parse(manifestContents);
            if (!manifest.RootElement.TryGetProperty("path", out JsonElement path)
                || path.ValueKind != JsonValueKind.String
                || !Path.IsPathFullyQualified(path.GetString() ?? ""))
                return false;

            return TryNormalizePath(path.GetString()!, out executablePath);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsValidManifest(string manifestContents, string expectedExecutablePath)
    {
        try
        {
            using JsonDocument manifest = JsonDocument.Parse(manifestContents);
            JsonElement root = manifest.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out JsonElement name)
                || name.ValueKind != JsonValueKind.String
                || name.GetString() != HostName
                || !root.TryGetProperty("path", out JsonElement path)
                || path.ValueKind != JsonValueKind.String
                || !TryNormalizePath(path.GetString() ?? "", out string normalizedPath)
                || !PathEquals(normalizedPath, expectedExecutablePath)
                || !root.TryGetProperty("type", out JsonElement type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "stdio"
                || !root.TryGetProperty("allowed_origins", out JsonElement origins)
                || origins.ValueKind != JsonValueKind.Array
                || origins.GetArrayLength() != 1)
                return false;

            JsonElement origin = origins[0];
            return origin.ValueKind == JsonValueKind.String
                && origin.GetString() == $"chrome-extension://{ExtensionId}/";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryNormalizePath(string path, out string normalizedPath)
    {
        try
        {
            normalizedPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalizedPath = "";
            return false;
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
