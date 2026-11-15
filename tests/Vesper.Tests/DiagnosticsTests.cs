using System.IO.Compression;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class DiagnosticsTests
{
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

    private static string ReadEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = Assert.Single(archive.Entries, item => item.FullName == name);
        using StreamReader reader = new(entry.Open());
        return reader.ReadToEnd();
    }
}
