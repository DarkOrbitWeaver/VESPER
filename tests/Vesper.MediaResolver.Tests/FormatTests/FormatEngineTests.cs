using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.FormatTests;

public class DeduplicationTests
{
    [Fact]
    public void PlainHttp_BeatsHls_ForEquivalentQuality()
    {
        var r = FormatDeduplicator.Deduplicate([T.Video("hls720", 720, protocol: StreamProtocol.Hls), T.Video("http720", 720)]);
        Assert.Equal("http720", Assert.Single(r).Id);
    }

    [Fact]
    public void SameQuality_KeepsHighestBitrate()
    {
        var r = FormatDeduplicator.Deduplicate([T.Video("a", 720, kbps: 1000), T.Video("b", 720, kbps: 1500), T.Video("c", 720, kbps: 1200)]);
        Assert.Equal("b", Assert.Single(r).Id);
    }

    [Fact]
    public void DifferentLanguagesAndBitrates_AreKept()
    {
        var r = FormatDeduplicator.Deduplicate([
            T.Audio("en128", lang: "en", kbps: 128), T.Audio("en256", lang: "en", kbps: 256), T.Audio("es128", lang: "es", kbps: 128), T.Audio("en128dup", lang: "en", kbps: 128)]);
        Assert.Equal(3, r.Count);
        Assert.DoesNotContain(r, x => x.Id == "en128dup");
    }

    [Fact]
    public void DifferentHeightsCodecsAndHdr_AreNotMerged()
    {
        var r = FormatDeduplicator.Deduplicate([
            T.Video("a", 720), T.Video("b", 1080), T.Video("c", 720, "vp9", container: "webm"), T.Video("d", 720, hdr: HdrKind.Hdr10)]);
        Assert.Equal(4, r.Count);
    }

    [Fact]
    public void ThirtyNearIdenticalFormats_CollapseToOne()
    {
        var reps = Enumerable.Range(0, 30).Select(i => T.Video($"x{i:00}", 720, kbps: 1000 + i)).ToList();
        Assert.Single(FormatDeduplicator.Deduplicate(reps));
    }
}

public class QualityMenuTests
{
    [Fact]
    public void DefaultMenu_ShowsFourDistinctChoices()
    {
        var item = T.Item(
            T.Video("v2160", 2160, "vp9", withAudio: false, kbps: 15000, container: "webm"),
            T.Video("v1080", 1080, withAudio: false, kbps: 4000),
            T.Video("v480", 480, withAudio: false, kbps: 800),
            T.Audio("aac", isDefault: true));

        var menu = QualityMenuBuilder.BuildDefault(item);
        Assert.True(new[] { QualityOptionKind.BestWithAudio, QualityOptionKind.Balanced, QualityOptionKind.Smallest, QualityOptionKind.AudioOnly }
            .SequenceEqual(menu.Select(m => m.Kind)));
        Assert.Contains("2160p", menu[0].Label);
        Assert.Contains("1080p", menu[1].Label);
        Assert.Contains("480p", menu[2].Label);
    }

    [Fact]
    public void SingleFormatSource_ProducesOneOption_NotThree()
    {
        var menu = QualityMenuBuilder.BuildDefault(T.Item(T.Video("only", 720)));
        Assert.Single(menu);
        Assert.Equal(QualityOptionKind.BestWithAudio, menu[0].Kind);
    }

    [Fact]
    public void Capabilities_DescribeOnlyWhatExists()
    {
        var c = QualityMenuBuilder.Describe(T.Item(T.Video("a", 1080, fps: 60), T.Video("b", 720), T.Audio("x", lang: "de")));
        Assert.True(new[] { 1080, 720 }.SequenceEqual(c.Heights));
        Assert.True(new[] { 60.0, 30.0 }.SequenceEqual(c.FrameRates));
        Assert.False(c.HasHdr);
        Assert.True(new[] { "de" }.SequenceEqual(c.AudioLanguages));
        Assert.True(c.HasAudioOnly);
        Assert.True(c.HasCompleteStreams);
        Assert.False(c.HasSeparateStreams);
    }
}

public class PreferenceStoreTests
{
    [Fact]
    public void ItemOverride_BeatsSite_BeatsDefault()
    {
        var store = new PreferenceStore { Default = FormatPreferences.BestQuality };
        store.SetSite("youtube", FormatPreferences.Height(720));
        store.SetItemOverride("youtube", "abc", FormatPreferences.SmallestSuitable);

        Assert.Equal(QualityMode.Smallest, store.Resolve("youtube", "abc").Mode);
        Assert.Equal(720, store.Resolve("youtube", "other").TargetHeight);
        Assert.Equal(QualityMode.Best, store.Resolve("vimeo", "x").Mode);
        Assert.False(store.TryGetExplicit("vimeo", "x", out _));
        Assert.True(store.TryGetExplicit("youtube", "zzz", out var site));
        Assert.Equal(720, site.TargetHeight);
    }

    [Fact]
    public void Json_RoundTripsEnumsAndLists()
    {
        var store = new PreferenceStore();
        store.SetSite("bilibili", new FormatPreferences
        {
            Mode = QualityMode.Custom, TargetHeight = 1080, Fallback = FallbackPolicy.LowerOnly,
            VideoCodecs = ["av1", "h264"], AudioLanguage = "ja", SubtitleLanguages = ["en"]
        });

        string json = store.ToJson();
        Assert.Contains("\"Custom\"", json);                    // enums are stored by name, not number
        var back = PreferenceStore.FromJson(json).Resolve("bilibili", null);
        Assert.Equal(QualityMode.Custom, back.Mode);
        Assert.Equal(FallbackPolicy.LowerOnly, back.Fallback);
        Assert.True(new[] { "av1", "h264" }.SequenceEqual(back.VideoCodecs));
        Assert.Equal("ja", back.AudioLanguage);
    }
}

public class DownloadPlanTests
{
    [Fact]
    public void MuxingSelection_ProducesTwoStepsAndFlagsHelperRequirement()
    {
        var item = T.Item(T.Video("v", 1080, withAudio: false), T.Audio("a", isDefault: true)) with { Title = "My: <Video>/\\Name?" };
        var sel = FormatSelector.Select(item, FormatPreferences.BestQuality).Selection!;
        var plan = DownloadPlanBuilder.Build(item, sel).Plan!;

        Assert.True(new[] { TransferRole.Video, TransferRole.Audio }.SequenceEqual(plan.Steps.Select(s => s.Role)));
        Assert.True(plan.RequiresExternalHelper);
        Assert.True(plan.HasAudio);
        Assert.DoesNotContain(plan.SuggestedFileName, c => "<>:\"/\\|?*".Contains(c));
        Assert.EndsWith(".mp4", plan.SuggestedFileName);
        Assert.Contains(plan.Warnings, w => w.Contains("merged"));
    }

    [Fact]
    public void CompleteStream_NeedsNoHelper()
    {
        var item = T.Item(T.Video("m", 720));
        var plan = DownloadPlanBuilder.Build(item, FormatSelector.Select(item, FormatPreferences.BestQuality).Selection!).Plan!;
        Assert.Single(plan.Steps);
        Assert.Equal(TransferRole.Muxed, plan.Steps[0].Role);
        Assert.False(plan.RequiresExternalHelper);
    }

    [Fact]
    public void ExpiredSource_IsRefusedWithRetryableError()
    {
        var item = T.Item(T.Video("m", 720) with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        var sel = FormatSelector.Select(item, FormatPreferences.BestQuality).Selection!;
        var r = DownloadPlanBuilder.Build(item, sel);
        Assert.Null(r.Plan);
        Assert.Equal(MediaErrorCode.SourceExpired, r.Error!.Code);
        Assert.True(r.Error.IsRetryable);
    }

    [Fact]
    public void SoonExpiringSource_Warns()
    {
        var item = T.Item(T.Video("m", 720) with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(60) });
        var plan = DownloadPlanBuilder.Build(item, FormatSelector.Select(item, FormatPreferences.BestQuality).Selection!).Plan!;
        Assert.Contains(plan.Warnings, w => w.Contains("expires"));
        Assert.NotNull(plan.ExpiresAt);
    }

    [Fact]
    public void PrivateNetworkUrl_IsBlockedAtPlanTime()
    {
        var item = T.Item(T.Video("m", 720) with { Url = new Uri("http://127.0.0.1/secret.mp4") });
        var r = DownloadPlanBuilder.Build(item, FormatSelector.Select(item, FormatPreferences.BestQuality).Selection!);
        Assert.Equal(MediaErrorCode.BlockedBySecurityPolicy, r.Error!.Code);
    }

    [Fact]
    public void DashWithoutSegmentPlan_IsNotAcceptedAsADownload()
    {
        var rep = T.Video("d", 720) with { Protocol = StreamProtocol.Dash, Segments = null };
        var item = T.Item(rep);
        var r = DownloadPlanBuilder.Build(item, FormatSelector.Select(item, FormatPreferences.BestQuality).Selection!);
        Assert.Equal(MediaErrorCode.UnsupportedManifest, r.Error!.Code);
    }

    [Fact]
    public void Subtitles_BecomeSeparateSteps()
    {
        var item = T.Item(T.Video("m", 720)) with
        {
            Subtitles = [new SubtitleTrack(new Uri("https://s.example.com/en.vtt"), "en", "English", "vtt")]
        };
        var sel = FormatSelector.Select(item, new FormatPreferences { SubtitleLanguages = ["en"] }).Selection!;
        var plan = DownloadPlanBuilder.Build(item, sel).Plan!;
        var sub = Assert.Single(plan.Subtitles);
        Assert.Equal(TransferRole.Subtitle, sub.Role);
    }
}
