using System.Text.Json;
using VESPER;
using Vesper.Engine;

namespace Vesper.Windows.Tests;

public sealed class SiteRuleSettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vesper-site-rules-").FullName;

    [Fact]
    public void Host_rules_are_canonical_exact_and_do_not_accept_url_parts()
    {
        Assert.True(SiteRule.TryNormalizeHost("  BÜCHER.Example. ", out string normalized));
        Assert.Equal("xn--bcher-kva.example", normalized);
        Assert.False(SiteRule.TryNormalizeHost("https://example.com/path", out _));
        Assert.False(SiteRule.TryNormalizeHost("example.com:443", out _));
        Assert.False(SiteRule.TryNormalizeHost("*.example.com", out _));

        var rules = new[] { new SiteRule { Host = "EXAMPLE.com", AlwaysAudio = true } };
        Assert.True(SiteRule.ForPage(rules, "https://example.com/watch?token=private")?.AlwaysAudio == true);
        Assert.Null(SiteRule.ForPage(rules, "https://sub.example.com/watch"));
        Assert.Null(SiteRule.ForPage(rules, "https://notexample.com/watch"));
        Assert.Null(SiteRule.ForPage(rules, "file:///C:/video.mp4"));
    }

    [Fact]
    public void Preferences_clone_and_settings_store_keep_independent_site_rules()
    {
        string folder = Path.Combine(_root, "site downloads");
        var original = new VesperPreferences
        {
            SiteRules = [new SiteRule { Host = "MEDIA.Example.", AlwaysAudio = true, Folder = folder, NeverCapture = true }]
        };
        VesperPreferences clone = original.Clone();
        original.SiteRules[0].Folder = Path.Combine(_root, "changed");
        Assert.Equal(folder, clone.SiteRules[0].Folder);

        Assert.Null(SettingsStore.Save(clone, _root));
        VesperPreferences loaded = SettingsStore.Load(_root);
        SiteRule saved = Assert.Single(loaded.SiteRules);
        Assert.Equal("media.example", saved.Host);
        Assert.True(saved.AlwaysAudio);
        Assert.True(saved.NeverCapture);
        Assert.Equal(Path.GetFullPath(folder), saved.Folder);
    }

    [Fact]
    public void Copy_preferences_keeps_site_rule_edits_separate()
    {
        var source = new VesperPreferences
        {
            SiteRules = [new SiteRule { Host = "video.example", Folder = _root, AlwaysAudio = true }]
        };
        var destination = new VesperPreferences();

        Form1.CopyPreferences(source, destination);
        source.SiteRules[0].Host = "other.example";

        Assert.Equal("video.example", Assert.Single(destination.SiteRules).Host);
    }

    [Fact]
    public async Task Browser_hello_exposes_only_never_capture_hosts()
    {
        var preferences = new VesperPreferences
        {
            SiteRules =
            [
                new SiteRule { Host = "safe.example", NeverCapture = true, Folder = Path.Combine(_root, "private folder") },
                new SiteRule { Host = "audio.example", AlwaysAudio = true, Folder = Path.Combine(_root, "another private folder") }
            ]
        };
        var bridge = new BrowserBridgeHandler(null!, null!, () => preferences);
        BridgeResponse response = await bridge.HandleAsync(new BridgeRequest("hello-site-rules", "hello", "ask", null, []),
            CancellationToken.None);
        using JsonDocument data = JsonDocument.Parse(JsonSerializer.Serialize(response.Data, BridgeProtocol.Json));

        string[] hosts = data.RootElement.GetProperty("siteRules").GetProperty("neverCaptureHosts")
            .EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Equal(new[] { "safe.example" }, hosts);
        Assert.DoesNotContain(_root, data.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Browser_auto_capture_is_refused_before_the_engine_sees_a_never_capture_site()
    {
        var preferences = new VesperPreferences
        {
            SiteRules = [new SiteRule { Host = "video.example", NeverCapture = true }]
        };
        var bridge = new BrowserBridgeHandler(null!, null!, () => preferences);
        var item = new BridgeItem("https://cdn.example/video.mp4?token=private", "video.mp4", "http",
            "https://video.example/watch?token=private", null, null, null, null);
        var request = new BridgeRequest("browser-capture-site-rule", "browser-submit", "download", null, [item]);

        BridgeResponse response = await bridge.HandleAsync(request, CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal("site-capture-disabled", response.Code);
        Assert.DoesNotContain("private", response.Message);
    }

    [Fact]
    public async Task Browser_auto_capture_refuses_a_batch_if_a_later_item_matches_never_capture()
    {
        var preferences = new VesperPreferences
        {
            SiteRules = [new SiteRule { Host = "video.example", NeverCapture = true }]
        };
        var bridge = new BrowserBridgeHandler(null!, null!, () => preferences);
        BridgeItem safeFirst = new("https://cdn.example/first.zip", "first.zip", "http",
            "https://safe.example/page", null, null, null, null);
        BridgeItem blockedLater = new("https://cdn.example/second.zip", "second.zip", "http",
            "https://video.example/watch", null, null, null, null);
        var request = new BridgeRequest("browser-capture-mixed-site-rules", "browser-submit", "download", null,
            [safeFirst, blockedLater]);

        BridgeResponse response = await bridge.HandleAsync(request, CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal("site-capture-disabled", response.Code);
    }

    [Fact]
    public void One_site_folder_applies_to_a_same_host_batch_but_not_a_mixed_host_batch()
    {
        string folder = Path.Combine(_root, "specific");
        var preferences = new VesperPreferences
        {
            SiteRules = [new SiteRule { Host = "media.example", Folder = folder }]
        };
        var bridge = new BrowserBridgeHandler(null!, null!, () => preferences);
        BridgeItem first = Item("https://media.example/watch/one");
        BridgeItem second = Item("https://media.example/watch/two");
        BridgeItem other = Item("https://other.example/watch");

        Assert.Equal(Path.GetFullPath(folder), bridge.SiteFolderFor([first, second]));
        Assert.Null(bridge.SiteFolderFor([first, other]));

        static BridgeItem Item(string referer) => new("https://cdn.example/file.mp4", null, "http", referer,
            null, null, null, null);
    }

    [Fact]
    public void Imported_entries_use_their_matching_site_folder()
    {
        string folder = Path.Combine(_root, "imported media");
        var preferences = new VesperPreferences
        {
            SiteRules = [new SiteRule { Host = "media.example", Folder = folder }]
        };
        var entry = new DownloadListEntry("https://media.example/files/episode.mp3", JobKind.Http, DownloadListState.Queued);

        Assert.Equal(Path.GetFullPath(folder), Form1.ImportSiteFolder(preferences, entry));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
