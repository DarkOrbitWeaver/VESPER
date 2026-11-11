using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.FormatTests;

public class FormatSelectorTests
{
    /// <summary>Typical adaptive ladder: separate video streams, two audio streams, one complete 360p stream.</summary>
    private static MediaItem Ladder() => T.Item(
        T.Video("v2160", 2160, "vp9", withAudio: false, kbps: 15000, container: "webm"),
        T.Video("v1080", 1080, withAudio: false, kbps: 4000),
        T.Video("v720", 720, withAudio: false, kbps: 2000),
        T.Video("v480", 480, withAudio: false, kbps: 800),
        T.Video("m360", 360, kbps: 500),
        T.Audio("aac", "aac", 128, "en", isDefault: true),
        T.Audio("opus", "opus", 130, "en", container: "webm"));

    private static FormatSelection Pick(MediaItem item, FormatPreferences p)
    {
        var r = FormatSelector.Select(item, p);
        Assert.True(r.IsSuccess, r.Error?.Diagnostic);
        return r.Selection!;
    }

    [Fact]
    public void Best_PicksHighestResolution_PairsAudio_AndFlagsMuxing()
    {
        var s = Pick(Ladder(), FormatPreferences.BestQuality);
        Assert.Equal("v2160", s.Video!.Id);
        Assert.NotNull(s.Audio);
        Assert.True(s.HasAudio);
        Assert.True(s.NeedsMuxing);
        Assert.True(s.Requirements.HasFlag(ProcessingRequirement.MuxAudioVideo));
        Assert.False(s.IsFallback);
    }

    [Fact]
    public void Best_RespectsMaxHeightWithoutCallingItAFallback()
    {
        var s = Pick(Ladder(), new FormatPreferences { Mode = QualityMode.Best, MaxHeight = 1080 });
        Assert.Equal("v1080", s.Video!.Id);
        Assert.False(s.IsFallback);
    }

    [Fact]
    public void Balanced_CapsAt1080p30()
    {
        var item = Ladder() with
        {
            Representations = Ladder().Representations.Append(T.Video("v1080p60", 1080, withAudio: false, kbps: 6000, fps: 60)).ToList()
        };
        var s = Pick(item, FormatPreferences.BalancedQuality);
        Assert.Equal("v1080", s.Video!.Id);
    }

    [Fact]
    public void Smallest_PicksLowestCostAtOrAboveSuitableHeight()
    {
        var s = Pick(Ladder(), FormatPreferences.SmallestSuitable);
        Assert.Equal("v480", s.Video!.Id);           // 360p exists but is below the "suitable" floor
        Assert.False(s.IsFallback);
    }

    [Fact]
    public void Smallest_WhenNothingIsSuitable_UsesLargestAvailableAndSaysSo()
    {
        var s = Pick(T.Item(T.Video("m360", 360), T.Video("m240", 240, kbps: 300)), FormatPreferences.SmallestSuitable);
        Assert.Equal("m360", s.Video!.Id);
        Assert.True(s.IsFallback);
    }

    [Fact]
    public void AudioOnly_PrefersRequestedLanguage()
    {
        var item = T.Item(
            T.Audio("a-en", lang: "en", isDefault: true),
            T.Audio("a-es", lang: "es"),
            T.Video("m720", 720));
        var s = Pick(item, new FormatPreferences { Mode = QualityMode.AudioOnly, AudioLanguage = "es" });
        Assert.Null(s.Video);
        Assert.Equal("a-es", s.Audio!.Id);
        Assert.False(s.IsFallback);
    }

    [Fact]
    public void AudioOnly_WithoutAudioOnlyStream_RequiresExtraction()
    {
        var s = Pick(T.Item(T.Video("m720", 720), T.Video("m360", 360)), FormatPreferences.AudioOnly);
        Assert.True(s.IsFallback);
        Assert.True(s.Requirements.HasFlag(ProcessingRequirement.ExtractAudio));
        Assert.Equal("m360", s.Video!.Id);
    }

    [Fact]
    public void Fallback_1080Unavailable_UsesNearestLowerAndExplains()
    {
        var s = Pick(T.Item(T.Video("m720", 720), T.Video("m480", 480)), FormatPreferences.Height(1080));
        Assert.Equal("m720", s.Video!.Id);
        Assert.True(s.IsFallback);
        Assert.Contains("1080p unavailable", s.FallbackReason);
    }

    [Fact]
    public void Fallback_NearestLowerThenHigher_UsesHigherWhenNothingLower()
    {
        var s = Pick(T.Item(T.Video("m2160", 2160), T.Video("m1440", 1440)), FormatPreferences.Height(1080));
        Assert.Equal("m1440", s.Video!.Id);
    }

    [Theory]
    [InlineData(FallbackPolicy.LowerOnly)]
    [InlineData(FallbackPolicy.Fail)]
    public void Fallback_StrictPolicies_FailInsteadOfSubstituting(FallbackPolicy policy)
    {
        var r = FormatSelector.Select(T.Item(T.Video("m2160", 2160)), FormatPreferences.Height(1080, policy));
        Assert.False(r.IsSuccess);
        Assert.Equal(MediaErrorCode.NoMatchingFormat, r.Error!.Code);
    }

    [Fact]
    public void Fallback_Nearest_AndBestAvailable()
    {
        var item = T.Item(T.Video("m720", 720), T.Video("m2160", 2160));
        Assert.Equal("m720", Pick(item, FormatPreferences.Height(1080, FallbackPolicy.Nearest)).Video!.Id);
        Assert.Equal("m2160", Pick(item, FormatPreferences.Height(1080, FallbackPolicy.BestAvailable)).Video!.Id);
    }

    [Fact]
    public void Custom4K_PicksExactWhenPresent_AndFallsBackFrom8K()
    {
        Assert.Equal("v2160", Pick(Ladder(), FormatPreferences.Height(2160)).Video!.Id);
        var s = Pick(Ladder(), FormatPreferences.Height(4320));
        Assert.Equal("v2160", s.Video!.Id);
        Assert.True(s.IsFallback);
    }

    [Fact]
    public void VideoOnlyWithNoAudioAnywhere_IsNeverPresentedAsHavingSound()
    {
        var s = Pick(T.Item(T.Video("vo", 1080, withAudio: false)), FormatPreferences.BestQuality);
        Assert.False(s.HasAudio);
        Assert.Contains("VIDEO ONLY", s.Summary);
        Assert.False(s.NeedsMuxing);
    }

    [Fact]
    public void MuxingDisabled_ChoosesCompleteStream_OrFails()
    {
        var noMux = new FormatPreferences { AllowMuxing = false };
        Assert.Equal("m360", Pick(Ladder(), noMux).Video!.Id);

        var r = FormatSelector.Select(T.Item(T.Video("vo", 1080, withAudio: false), T.Audio("a")), noMux);
        Assert.False(r.IsSuccess);
        Assert.Equal(MediaErrorCode.NoMatchingFormat, r.Error!.Code);
    }

    [Fact]
    public void MissingMetadata_DoesNotThrowAndStillSelects()
    {
        var a = new MediaRepresentation { Id = "x1", Url = new Uri("https://cdn.example.com/a.mp4") };
        var b = new MediaRepresentation { Id = "x2", Url = new Uri("https://cdn.example.com/b.mp4") };
        foreach (var mode in Enum.GetValues<QualityMode>())
        {
            var r = FormatSelector.Select(T.Item(a, b), new FormatPreferences { Mode = mode, TargetHeight = 720 });
            if (mode == QualityMode.AudioOnly) Assert.True(r.IsSuccess || r.Error is not null);
            else Assert.True(r.IsSuccess);
        }
    }

    [Fact]
    public void Hdr_Preferences()
    {
        var item = T.Item(T.Video("sdr", 1080), T.Video("hdr", 1080, "vp9", container: "webm", hdr: HdrKind.Hdr10));
        Assert.Equal("sdr", Pick(item, new FormatPreferences { Hdr = HdrPreference.SdrOnly }).Video!.Id);
        Assert.Equal("hdr", Pick(item, new FormatPreferences { Hdr = HdrPreference.HdrOnly }).Video!.Id);
        Assert.Equal("hdr", Pick(item, new FormatPreferences { Hdr = HdrPreference.PreferHdr }).Video!.Id);
        Assert.Equal("sdr", Pick(item, new FormatPreferences { Hdr = HdrPreference.Any }).Video!.Id);

        var none = FormatSelector.Select(T.Item(T.Video("sdr", 1080)), new FormatPreferences { Hdr = HdrPreference.HdrOnly });
        Assert.Equal(MediaErrorCode.NoMatchingFormat, none.Error!.Code);
    }

    [Fact]
    public void ContainerAndCodecPreferences()
    {
        var item = T.Item(
            T.Video("mp4v", 1080, "h264", kbps: 4000),
            T.Video("webmv", 1080, "vp9", container: "webm", kbps: 3000),
            T.Video("av1v", 1080, "av1", kbps: 2500));

        Assert.Equal("mp4v", Pick(item, FormatPreferences.BestQuality).Video!.Id);                         // highest bitrate wins by default
        Assert.Equal("av1v", Pick(item, new FormatPreferences { VideoCodecs = ["av1", "h264"] }).Video!.Id);
        Assert.Equal("webmv", Pick(item, new FormatPreferences { Container = "webm" }).Video!.Id);
        Assert.Equal("webmv", Pick(item, new FormatPreferences { Container = "webm", ContainerStrict = true }).Video!.Id);
        Assert.Equal("h264", Pick(item, new FormatPreferences { VideoCodecs = ["h264"], RestrictVideoCodecs = true }).Video!.VideoCodec);

        var none = FormatSelector.Select(item, new FormatPreferences { VideoCodecs = ["h265"], RestrictVideoCodecs = true });
        Assert.False(none.IsSuccess);
    }

    [Fact]
    public void FileSizeAndFrameRateLimits_AreHardFilters()
    {
        var item = T.Item(
            T.Video("big", 2160, size: 900_000_000),
            T.Video("small", 1080, size: 100_000_000),
            T.Video("fast", 1080, fps: 60, size: 120_000_000));
        Assert.Equal("small", Pick(item, new FormatPreferences { MaxFileSizeBytes = 200_000_000, MaxFrameRate = 30 }).Video!.Id);
        Assert.False(FormatSelector.Select(item, new FormatPreferences { MaxFileSizeBytes = 1000 }).IsSuccess);
    }

    [Fact]
    public void AudioChoice_PrefersContainerCompatibleCodec_AndSurroundOnRequest()
    {
        var item = T.Item(
            T.Video("v", 1080, "vp9", withAudio: false, container: "webm"),
            T.Audio("stereo", "opus", 128, "en", channels: 2, container: "webm"),
            T.Audio("surround", "opus", 256, "en", channels: 6, container: "webm"));
        Assert.Equal("stereo", Pick(item, FormatPreferences.BestQuality).Audio!.Id);
        Assert.Equal("surround", Pick(item, new FormatPreferences { PreferSurround = true }).Audio!.Id);
        Assert.Equal("stereo", Pick(item, new FormatPreferences { PreferSurround = true, MaxAudioChannels = 2 }).Audio!.Id);
    }

    [Fact]
    public void HlsVideo_PairsOnlyWithItsOwnAudioGroup()
    {
        var v = T.Video("hv", 1080, withAudio: false, protocol: StreamProtocol.Hls, audioGroup: "g2");
        var a1 = T.Audio("g1-en") with { Protocol = StreamProtocol.Hls, AudioGroupId = "g1", IsDefaultAudio = true };
        var a2 = T.Audio("g2-en") with { Protocol = StreamProtocol.Hls, AudioGroupId = "g2" };
        var s = Pick(T.Item(v, a1, a2), FormatPreferences.BestQuality);
        Assert.Equal("g2-en", s.Audio!.Id);
    }

    [Fact]
    public void Selection_IsDeterministicForAnyInputOrder()
    {
        var reps = Ladder().Representations.ToList();
        reps.Add(T.Video("v1080b", 1080, "h264", withAudio: false, kbps: 4000));   // deliberate near-tie
        foreach (var mode in new[] { QualityMode.Best, QualityMode.Balanced, QualityMode.Smallest, QualityMode.AudioOnly })
        {
            string? expected = null;
            for (int seed = 0; seed < 25; seed++)
            {
                var rng = new Random(seed);
                var shuffled = reps.OrderBy(_ => rng.Next()).ToList();
                var s = Pick(T.Item(shuffled.ToArray()), new FormatPreferences { Mode = mode });
                string sig = $"{s.Video?.Id}|{s.Audio?.Id}|{s.OutputContainer}";
                expected ??= sig;
                Assert.Equal(expected, sig);
            }
        }
    }

    [Fact]
    public void Subtitles_AreChosenByLanguage_PreferringManualOverAuto()
    {
        var item = T.Item(T.Video("m720", 720)) with
        {
            Subtitles =
            [
                new SubtitleTrack(new Uri("https://s.example.com/en-auto.vtt"), "en", "auto", "vtt", true),
                new SubtitleTrack(new Uri("https://s.example.com/en.vtt"), "en", "English", "vtt"),
                new SubtitleTrack(new Uri("https://s.example.com/fr.vtt"), "fr", "French", "vtt")
            ]
        };
        var s = Pick(item, new FormatPreferences { SubtitleLanguages = ["en", "fr", "de"] });
        Assert.Equal(2, s.Subtitles.Count);
        Assert.Equal("https://s.example.com/en.vtt", s.Subtitles[0].Url.AbsoluteUri);
        Assert.Empty(Pick(item, new FormatPreferences { SubtitleLanguages = ["de"] }).Subtitles);
    }

    [Fact]
    public void LiveRepresentations_AreIgnored()
    {
        var live = T.Video("live", 1080) with { IsLive = true };
        var r = FormatSelector.Select(T.Item(live), FormatPreferences.BestQuality);
        Assert.False(r.IsSuccess);
    }

    [Theory]
    [InlineData("avc1.640028", "h264")]
    [InlineData("hvc1.1.6.L93.B0", "h265")]
    [InlineData("vp09.00.10.08", "vp9")]
    [InlineData("av01.0.05M.08", "av1")]
    [InlineData("dvh1.05.06", "h265")]
    public void CodecInfo_NormalisesVideo(string raw, string family) => Assert.Equal(family, CodecInfo.NormalizeVideo(raw));

    [Theory]
    [InlineData("mp4a.40.2", "aac")]
    [InlineData("mp4a.40.34", "mp3")]
    [InlineData("opus", "opus")]
    [InlineData("ec-3", "eac3")]
    public void CodecInfo_NormalisesAudio(string raw, string family) => Assert.Equal(family, CodecInfo.NormalizeAudio(raw));

    [Theory]
    [InlineData("h264", "aac", null, "mp4")]
    [InlineData("vp9", "opus", "webm", "webm")]
    [InlineData("vp8", "vorbis", null, "webm")]
    [InlineData("h264", "vorbis", null, "mkv")]
    [InlineData("h264", "aac", "webm", "mp4")]
    [InlineData("h264", "aac", "mkv", "mkv")]
    public void CodecInfo_ChoosesStreamCopyContainer(string v, string a, string? preferred, string expected) =>
        Assert.Equal(expected, CodecInfo.ChooseMuxContainer(v, a, preferred));
}
