using System.Text.Json;

namespace VESPER;

internal static class NativeHostRegistration
{
    public const string HostName = "com.vesper.downloader";
    public const string ExtensionId = "aidnpdcnkhgojimnkcgieebdjdicfemi";

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
}
