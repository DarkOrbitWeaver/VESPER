using System.IO.Compression;
using System.Text;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void Browser_failures_keep_the_original_build_and_time_without_duplicate_rows()
    {
        const string message = """
            {"v":2,"type":"browser-diagnostics","id":"diagnostics-flush",
             "entries":[{"id":"29414d50-bf70-4cb8-bdfa-f9be14bb3cbf","utc":1704067200000,
              "build":"2.0.0-aaa.7","kind":"media-failed","code":"site-refused",
              "page":"https://www.instagram.com/reel/*","resource":"https://cdn.test/*.mp4",
              "facts":{"stage":"formats","native":"site-refused","status":403,"bodyProof":"mse","observedLookups":"source-changed,site-refused"}}]}
            """;
        Assert.True(BridgeProtocol.TryParse(message, out var request, out string error), error);
        Assert.False(BridgeProtocol.TryParse(message.Replace("/reel/*", "/reel/*?token=SECRET"), out _, out _));
        Assert.False(BridgeProtocol.TryParse(message.Replace("\"bodyProof\"", "\"authorization\""), out _, out _));
        using var harness = new Harness();
        Assert.True(harness.Engine.RecordBrowserDiagnostics(request.Diagnostics!));
        Assert.True(harness.Engine.RecordBrowserDiagnostics(request.Diagnostics!));
        harness.Restart();

        string line = Assert.Single(harness.Engine.ReadLog(), line => line.Contains("media-failed", StringComparison.Ordinal));
        Assert.StartsWith("2024-01-01T00:00:00", line);
        Assert.Contains("2.0.0-aaa.7", line);
        Assert.Contains("\"status\":403", line);
    }

    [Fact]
    public void Scrub_redacts_urls_headers_assignments_and_profile_paths()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string raw = string.Join(Environment.NewLine,
            "request failed: https://user:URL-PASSWORD@media.example.test/video.mp4?token=URL-SECRET&signature=URL-SIGNATURE.",
            "Authorization: Bearer HEADER-SECRET",
            "Proxy-Authorization: Basic PROXY-SECRET",
            "Cookie: sessionid=COOKIE-SECRET; theme=green",
            "{\"Cookie\":\"JSON-COOKIE-SECRET\",\"Authorization\":\"JSON-AUTH-SECRET\"}",
            "api_key=ASSIGNMENT-SECRET",
            $"destination: {profile}\\Downloads\\private name.mp4");

        string scrubbed = Diagnostics.Scrub(raw);

        Assert.Contains("https://media.example.test/video.mp4?…", scrubbed);
        Assert.Contains("Authorization: [redacted]", scrubbed);
        Assert.Contains("Proxy-Authorization: [redacted]", scrubbed);
        Assert.Contains("Cookie: [redacted]", scrubbed);
        Assert.Contains("api_key=[redacted]", scrubbed);
        Assert.Contains("%USERPROFILE%\\Downloads\\private name.mp4", scrubbed);
        foreach (string secret in new[] { "URL-PASSWORD", "URL-SECRET", "URL-SIGNATURE", "HEADER-SECRET", "PROXY-SECRET", "COOKIE-SECRET", "JSON-COOKIE-SECRET", "JSON-AUTH-SECRET", "ASSIGNMENT-SECRET", profile })
            Assert.DoesNotContain(secret, scrubbed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportBundle_contains_only_the_reviewable_diagnostics_entries()
    {
        string root = Path.Combine(Path.GetTempPath(), "vesper-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var harness = new Harness();
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            harness.Engine.Log(null, "diagnostic", $"Request failed for https://media.example.test/file.mp4?access_token=BUNDLE-SECRET; path={profile}\\Downloads\\private.mp4");
            string target = Path.Combine(root, "support.zip");

            Diagnostics.ExportBundle(harness.Engine, target, "1.0-test");

            using var file = File.OpenRead(target);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            Assert.Equal(["contents.txt", "events.txt", "system.txt"], archive.Entries.Select(entry => entry.FullName).OrderBy(name => name));
            string system = ReadEntry(archive, "system.txt");
            string events = ReadEntry(archive, "events.txt");
            string contents = ReadEntry(archive, "contents.txt");
            Assert.Contains("VESPER version: 1.0-test", system);
            Assert.Contains("Exported UTC:", system);
            Assert.Contains("https://media.example.test/file.mp4?…", events);
            Assert.Contains("%USERPROFILE%\\Downloads\\private.mp4", events);
            Assert.DoesNotContain("BUNDLE-SECRET", events);
            Assert.DoesNotContain(profile, events, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cookies", contents);
            Assert.Contains("review before sharing", contents);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExportBundle_keeps_the_previous_archive_if_replacement_fails()
    {
        string root = Path.Combine(Path.GetTempPath(), "vesper-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var harness = new Harness();
            string target = Path.Combine(root, "support.zip");
            using (var locked = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                locked.Write(Encoding.UTF8.GetBytes("previous archive"));
                locked.Flush(flushToDisk: true);
                Exception failure = Assert.ThrowsAny<Exception>(() => Diagnostics.ExportBundle(harness.Engine, target, "1.0-test"));
                Assert.True(failure is IOException or UnauthorizedAccessException);
            }

            Assert.Equal("previous archive", File.ReadAllText(target));
            Assert.Single(Directory.GetFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = Assert.Single(archive.Entries, item => item.FullName == name);
        using StreamReader reader = new(entry.Open());
        return reader.ReadToEnd();
    }
}
