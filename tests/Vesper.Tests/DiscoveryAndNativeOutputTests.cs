using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using VESPER;
using Vesper.Engine;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;

namespace Vesper.Tests;

public sealed class DiscoveryAndNativeOutputTests
{
    [Fact]
    public void A_standalone_video_has_a_labelled_direct_choice_even_with_unrelated_audio()
    {
        var video = new MediaRepresentation
        {
            Id = "direct-video", Url = new("https://video.example/clip.mp4"), Container = "mp4",
            Content = StreamContent.VideoOnly, StandaloneVideo = true, VideoCodec = "h264", Height = 720, FileSize = 1200
        };
        var audio = new MediaRepresentation
        {
            Id = "direct-audio", Url = new("https://video.example/other.m4a"), Container = "m4a",
            Content = StreamContent.AudioOnly, AudioCodec = "aac", FileSize = 300
        };
        var item = new MediaItem
        {
            SourceUrl = new("https://video.example/watch"), Provider = "generic", Title = "Clip",
            Representations = [video, audio]
        };

        foreach (MediaRepresentation? exact in new MediaRepresentation?[] { null, video })
        {
            var plan = MediaTransferBridge.Prepare(MediaTransferBridge.PlayableOnly(item), FormatPreferences.BestQuality, exact);
            Assert.NotNull(plan.Choice);
            Assert.Equal(JobKind.Http, plan.Choice.Kind);
            Assert.Equal(video.Url.AbsoluteUri, plan.Choice.Url);
            Assert.Contains("no audio", plan.Choice.DisplayDetail);
        }
    }

    private sealed class PlaybackProbe : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentType = new("video/mp4");
            content.Headers.ContentLength = 4500;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request, Content = content });
        }
    }

    [Theory]
    [InlineData("https://video.example/file.mp4", true)]
    [InlineData("https://cdn.example/file.mp4", false)]
    public async Task SignedBrowserEvidenceSurvivesTheResolverAndNativePlanningBoundary(string target, bool sameOrigin)
    {
        const string page = "https://video.example/authorized";
        using var resolver = new DefaultMediaResolver(handler: new PlaybackProbe());
        var resolved = await resolver.ResolveAsync(new()
        {
            Url = new(page), Browser = new BrowserDiscoveryMessage
            {
                Schema = BrowserDiscoveryMessage.CurrentSchema, PageUrl = page,
                Candidates = [new BrowserMediaCandidate { Url = target, Width = 1280, Height = 720, DurationSeconds = 12 }],
                Authorization = new BrowserAuthorization { Origin = "https://video.example", ConsentGranted = true, CookieHeader = "session=fixture" }
            }
        }, CancellationToken.None);

        Assert.True(resolved.IsSuccess, resolved.Error?.Diagnostic);
        var item = MediaTransferBridge.PlayableOnly(resolved.Item!);
        Assert.Equal(720, Assert.Single(item.Representations).Height);
        Assert.Equal(TimeSpan.FromSeconds(12), item.Duration);
        var plan = MediaTransferBridge.Prepare(item, FormatPreferences.BestQuality);
        Assert.NotNull(plan.Choice);
        Assert.Equal(sameOrigin, plan.Choice.Secrets?.ContainsKey("Cookie") == true);
        Assert.DoesNotContain("session=fixture", JsonSerializer.Serialize(plan.Choice));
    }

    [Theory]
    [InlineData("https://v16.tiktokcdn.com/file.mp4", false)]
    [InlineData("https://www.tiktok.com/media/file.mp4", true)]
    public void CookieBearingPlayerStateDoesNotEraseDownloadableFormats(string target, bool carriesCookie)
    {
        var representation = new MediaRepresentation
        {
            Id = "tiktok-play", Url = new Uri(target), Container = "mp4", VideoCodec = "h264",
            Context = new MediaRequestContext
            {
                CookieHeader = "session=private", CredentialOrigin = new Uri("https://www.tiktok.com/"),
                CredentialHosts = ["www.tiktok.com"], Referer = "https://www.tiktok.com/"
            }
        };
        var item = new MediaItem { SourceUrl = new Uri("https://www.tiktok.com/@a/video/123"), Provider = "tiktok", Title = "Video", Representations = [representation] };
        Assert.Single(MediaTransferBridge.PlayableOnly(item).Representations);
        var plan = MediaTransferBridge.Prepare(item, FormatPreferences.BestQuality);
        Assert.NotNull(plan.Choice);
        Assert.Equal(carriesCookie, plan.Choice.Secrets?.ContainsKey("Cookie") == true);
        Assert.DoesNotContain("session=private", JsonSerializer.Serialize(plan.Choice));
    }

    [Fact]
    public async Task NativeOpusRemuxRetainsPacketsAndTitle()
    {
        using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", "quiet-track.webm"));
        var audio = Assert.Single(WebmDemuxer.Read(source, source.Length, CancellationToken.None));
        using var output = new MemoryStream();
        string digest = await OpusMuxer.WriteAsync(audio, source, output, new MediaMergeMetadata { Title = "Native song", Artist = "Fixture" }, null, CancellationToken.None);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(output.ToArray())), digest);
        var bytes = output.ToArray();
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual("OggS"u8));
        Assert.Contains("TITLE=Native song", Encoding.UTF8.GetString(bytes));
        Assert.Contains("ARTIST=Fixture", Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData(0, 480)]
    [InlineData(0x80, 120)]
    [InlineData(0x98, 960)]
    [InlineData(0x18, 2880)]
    public void OpusTocDurationsUseThe48KhzGranuleClock(byte toc, int expected) =>
        Assert.Equal(expected, OpusMuxer.PacketSamples([toc, 0]));

    [Fact]
    public async Task WaveClipIsSampleExactAndHasSourceTags()
    {
        const int rate = 48000, samples = rate * 2;
        using var source = new MemoryStream(new byte[44 + samples * 2]);
        using (var writer = new BinaryWriter(source, Encoding.UTF8, true))
        {
            writer.Write("RIFF"u8); writer.Write((uint)(source.Length - 8)); writer.Write("WAVEfmt "u8); writer.Write(16u);
            writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(samples * 2);
            for (int i = 0; i < samples; i++) writer.Write((short)(i % short.MaxValue));
        }
        using var output = new MemoryStream();
        await WaveAudio.WriteAsync(source, output, new MediaTimeRange(.125, .625), new MediaMergeMetadata { Title = "Clip" }, CancellationToken.None);
        var info = WaveAudio.Read(output);
        Assert.Equal(24000, info.SampleCount);
        Assert.Contains("Clip", Encoding.UTF8.GetString(output.ToArray()));
        output.Position = info.DataOffset;
        Span<byte> first = stackalloc byte[2]; output.ReadExactly(first);
        Assert.Equal(6000, BinaryPrimitives.ReadInt16LittleEndian(first));
    }

    [Fact]
    public void InvalidWaveChunkCannotBePublished()
    {
        using var source = new MemoryStream("RIFF\0\0\0\0WAVE"u8.ToArray());
        Assert.Throws<MediaContainerException>(() => WaveAudio.Read(source));
    }

    [Theory]
    [InlineData("1080.m3u8")]
    [InlineData("playlist?id=1080")]
    public void Compatible_video_preset_keeps_fragmented_mp4_hls_quality_audio_and_subtitle_choices(string source)
    {
        var hls = new MediaRepresentation
        {
            Id = "hls-1080", Url = new Uri("https://cdn.example.test/" + source),
            Protocol = StreamProtocol.Hls, Content = StreamContent.VideoAndAudio,
            Container = "mp4", VideoCodec = "h264", AudioCodec = "aac", Height = 1080,
            AudioTrackId = "english", AudioLanguage = "en", IsDefaultAudio = true,
            Segments = new SegmentPlan
            {
                InitializationUrl = new Uri("https://cdn.example.test/init.mp4"),
                Segments = [new MediaSegment(new Uri("https://cdn.example.test/part.m4s"), 60, StartSeconds: 0)],
                TotalDurationSeconds = 60,
                IsFragmentedMp4 = true
            }
        };
        var http = new MediaRepresentation
        {
            Id = "http-720", Url = new Uri("https://cdn.example.test/720.mp4"),
            Content = StreamContent.VideoAndAudio, Container = "mp4", VideoCodec = "h264", AudioCodec = "aac",
            Height = 720
        };
        var subtitle = new SubtitleTrack(new Uri("https://cdn.example.test/en.vtt"), "en", "English", "vtt");
        var item = new MediaItem
        {
            SourceUrl = new Uri("https://video.example.test/watch/one"), Provider = "test", Title = "Video",
            Duration = TimeSpan.FromSeconds(60), Representations = [http, hls], Subtitles = [subtitle]
        };
        FormatPreferences preferences = FormatPreferences.BestQuality with
        {
            AudioTrackId = "english", SubtitleLanguages = ["en"], EmbedSubtitles = true
        };

        var full = MediaTransferBridge.Prepare(item, preferences, output: MediaOutputPreference.CompatibleVideo);
        MediaDownloadChoice fullChoice = Assert.IsType<MediaDownloadChoice>(full.Choice);
        Assert.Equal(hls.Id, fullChoice.FormatId);
        Assert.Contains("1080p", fullChoice.DisplayDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JobKind.Hls, fullChoice.Kind);
        Assert.Equal(MediaOutput.Original, fullChoice.Output);
        Assert.Equal(subtitle.Url, Assert.Single(fullChoice.SubtitleTracks!).Url);
        Assert.Contains("saved separately", fullChoice.SubtitleNotice, StringComparison.OrdinalIgnoreCase);

        var clipped = MediaTransferBridge.Prepare(item, preferences, output: MediaOutputPreference.CompatibleVideo,
            clipStartSeconds: 10, clipEndSeconds: 20);
        MediaDownloadChoice clipChoice = Assert.IsType<MediaDownloadChoice>(clipped.Choice);
        Assert.Equal(hls.Id, clipChoice.FormatId);
        Assert.Equal(JobKind.Hls, clipChoice.Kind);
        Assert.Equal(new MediaTimeRange(10, 20), JsonSerializer.Deserialize<MediaTimeRange>(clipChoice.MediaPlanJson!));
        Assert.Equal(ClipMode.Segments, ClipSelection.ModeFor(clipChoice));
    }

    [Fact]
    public void Compatible_muxed_mp4_clip_keeps_quality_format_and_refresh_identity()
    {
        MediaRepresentation stream = MuxedMp4("137", "youtube:video:137:modified:2097152");
        MediaItem item = VideoItem(stream);
        MediaDownloadChoice fullChoice = Assert.IsType<MediaDownloadChoice>(MediaTransferBridge.Prepare(item,
            FormatPreferences.BestQuality, output: MediaOutputPreference.CompatibleVideo).Choice);

        Assert.Equal(JobKind.Http, fullChoice.Kind);
        Assert.Equal(MediaOutput.Original, fullChoice.Output);
        Assert.True(fullChoice.CanUseFullFileClip);
        Assert.Equal(ClipMode.FullFile, ClipSelection.ModeFor(fullChoice));

        MediaDownloadChoice clipChoice = Assert.IsType<MediaDownloadChoice>(MediaTransferBridge.Prepare(item,
            FormatPreferences.BestQuality, output: MediaOutputPreference.CompatibleVideo,
            clipStartSeconds: 10, clipEndSeconds: 25).Choice);
        Assert.Equal(JobKind.Merge, clipChoice.Kind);
        Assert.Equal(MediaOutput.Original, clipChoice.Output);
        Assert.Equal(fullChoice.FormatId, clipChoice.FormatId);
        Assert.Equal(fullChoice.DisplayDetail, clipChoice.DisplayDetail);
        Assert.Equal(item.Provider, clipChoice.Provider);
        Assert.Equal(item.MediaId, clipChoice.MediaId);
        Assert.True(MediaMergePlan.TryParse(clipChoice.MediaPlanJson, false, out MediaMergePlan plan, out string error), error);
        Assert.True(plan.CanClipLocalMp4);
        Assert.Equal(new MediaTimeRange(10, 25), plan.ClipRange);
        Assert.Equal("muxed", Assert.Single(plan.Tracks).Role);
        Assert.Equal(stream.ContentIdentity, plan.Tracks[0].Identity);
        Assert.Equal(stream.FileSize, plan.Tracks[0].Size);
    }

    [Fact]
    public void Compatible_split_mp4_clip_keeps_both_source_identities()
    {
        var video = new MediaRepresentation
        {
            Id = "137", Url = new Uri("https://cdn.example.test/video.mp4"), Content = StreamContent.VideoOnly,
            Container = "mp4", VideoCodec = "h264", RawCodecs = "avc1.640028", Height = 1080,
            FileSize = 2_097_152, ContentIdentity = "youtube:video:137:modified:2097152"
        };
        var audio = new MediaRepresentation
        {
            Id = "140", Url = new Uri("https://cdn.example.test/audio.m4a"), Content = StreamContent.AudioOnly,
            Container = "m4a", AudioCodec = "aac", RawCodecs = "mp4a.40.2", AudioTrackId = "english",
            FileSize = 524_288, ContentIdentity = "youtube:video:140:modified:524288"
        };
        MediaItem item = VideoItem(video, audio);
        MediaDownloadChoice fullChoice = Assert.IsType<MediaDownloadChoice>(MediaTransferBridge.Prepare(item,
            FormatPreferences.BestQuality, output: MediaOutputPreference.CompatibleVideo).Choice);
        Assert.Equal(JobKind.Merge, fullChoice.Kind);
        Assert.True(fullChoice.CanUseFullFileClip);
        Assert.Equal(ClipMode.FullFile, ClipSelection.ModeFor(fullChoice));

        MediaDownloadChoice clipChoice = Assert.IsType<MediaDownloadChoice>(MediaTransferBridge.Prepare(item,
            FormatPreferences.BestQuality, output: MediaOutputPreference.CompatibleVideo,
            clipStartSeconds: 10, clipEndSeconds: 25).Choice);
        Assert.True(MediaMergePlan.TryParse(clipChoice.MediaPlanJson, false, out MediaMergePlan plan, out string error), error);
        Assert.True(plan.CanClipLocalMp4);
        Assert.Equal(new[] { video.ContentIdentity, audio.ContentIdentity }, plan.Tracks.Select(track => track.Identity));
        Assert.Equal(new[] { video.FileSize!.Value, audio.FileSize!.Value }, plan.Tracks.Select(track => track.Size));
    }

    [Fact]
    public void Verified_browser_range_keeps_its_validator_in_the_native_plan()
    {
        const string tag = "\"media-v1\"";
        var source = MuxedMp4("range-video", null);
        string urlHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source.Url.AbsoluteUri)));
        string identity = "http-v1:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(urlHash + "|" + tag + "|" + source.FileSize!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        source = source with { BrowserRangeVerified = true, ResourceETag = tag, ResourceUrlSha256 = urlHash, ContentIdentity = identity };
        var item = VideoItem(source);
        var choice = Assert.IsType<MediaDownloadChoice>(MediaTransferBridge.Prepare(item, FormatPreferences.BestQuality).Choice);
        Assert.Equal(JobKind.Merge, choice.Kind);
        Assert.True(MediaMergePlan.TryParse(choice.MediaPlanJson, false, out var plan, out string error), error);
        var track = Assert.Single(plan.Tracks);
        Assert.Equal(tag, track.ResourceETag);
        Assert.Equal(urlHash, track.ResourceUrlSha256);
        Assert.Equal(identity, track.Identity);
    }

    [Fact]
    public void Audio_only_aac_m4a_can_use_the_native_clip_plan()
    {
        var audio = new MediaRepresentation
        {
            Id = "audio-aac", Url = new Uri("https://cdn.example.test/song.m4a"),
            Content = StreamContent.AudioOnly, Container = "m4a", AudioCodec = "aac", RawCodecs = "mp4a.40.2",
            FileSize = 524_288, ContentIdentity = "audio-aac:modified:524288"
        };
        MediaItem item = new()
        {
            SourceUrl = new Uri("https://audio.example.test/song"), Provider = "fixture", MediaId = "song",
            Title = "Song", Kind = MediaKind.Audio, Duration = TimeSpan.FromSeconds(60), Representations = [audio]
        };

        MediaDownloadChoice full = Assert.IsType<MediaDownloadChoice>(MediaTransferBridge.Prepare(item,
            FormatPreferences.AudioOnly, output: MediaOutputPreference.M4a).Choice);
        Assert.Equal(ClipMode.FullFile, ClipSelection.ModeFor(full));

        MediaDownloadChoice clipped = Assert.IsType<MediaDownloadChoice>(MediaTransferBridge.Prepare(item,
            FormatPreferences.AudioOnly, output: MediaOutputPreference.M4a,
            clipStartSeconds: 10, clipEndSeconds: 25).Choice);
        Assert.Equal(JobKind.Merge, clipped.Kind);
        Assert.True(MediaMergePlan.TryParse(clipped.MediaPlanJson, false, out MediaMergePlan plan, out string error), error);
        Assert.Equal("m4a", plan.OutputContainer);
        Assert.False(plan.RequireVideo);
        Assert.Equal(new MediaTimeRange(10, 25), plan.ClipRange);
        Assert.True(plan.CanClipLocalMp4);
        Assert.True(MediaTransferBridge.SupportsTimeRange(full));
    }
    [Fact]
    public void Direct_aac_audio_without_provider_identity_can_be_trimmed_after_full_download()
    {
        var audio = new MediaRepresentation
        {
            Id = "direct-aac", Url = new Uri("https://cdn.example.test/song.m4a"),
            Content = StreamContent.AudioOnly, Container = "m4a", AudioCodec = "aac", RawCodecs = "mp4a.40.2"
        };
        MediaItem item = new()
        {
            SourceUrl = new Uri("https://audio.example.test/song"), Provider = "fixture", MediaId = "song",
            Title = "Song", Kind = MediaKind.Audio, Duration = TimeSpan.FromSeconds(60), Representations = [audio]
        };
        var full = Assert.IsType<MediaDownloadChoice>(MediaTransferBridge.Prepare(item,
            FormatPreferences.AudioOnly, output: MediaOutputPreference.M4a).Choice);
        Assert.Equal(JobKind.Http, full.Kind);
        Assert.Equal(ClipMode.FullFile, ClipSelection.ModeFor(full));
        var clip = Assert.IsType<MediaDownloadChoice>(MediaTransferBridge.Prepare(item,
            FormatPreferences.AudioOnly, output: MediaOutputPreference.M4a,
            clipStartSeconds: 10, clipEndSeconds: 25).Choice);
        Assert.Equal(JobKind.Http, clip.Kind);
        Assert.Equal(MediaOutput.M4a, clip.Output);
        Assert.Equal(full.FormatId, clip.FormatId);
        Assert.Equal(new MediaTimeRange(10, 25), JsonSerializer.Deserialize<MediaTimeRange>(clip.MediaPlanJson!));
    }


    [Fact]
    public void Audio_only_hls_range_uses_the_segment_transfer_plan()
    {
        var audio = new MediaRepresentation
        {
            Id = "hls-audio", Url = new Uri("https://cdn.example.test/audio/index.m3u8"),
            Protocol = StreamProtocol.Hls, Content = StreamContent.AudioOnly, Container = "m4a", AudioCodec = "aac",
            Segments = new SegmentPlan
            {
                Segments = [new MediaSegment(new Uri("https://cdn.example.test/audio/part.m4s"), 60, StartSeconds: 0)],
                TotalDurationSeconds = 60, IsFragmentedMp4 = true
            }
        };
        MediaItem item = new()
        {
            SourceUrl = new Uri("https://audio.example.test/song"), Provider = "fixture", Title = "Song",
            Kind = MediaKind.Audio, Duration = TimeSpan.FromSeconds(60), Representations = [audio]
        };

        MediaDownloadChoice clipped = Assert.IsType<MediaDownloadChoice>(MediaTransferBridge.Prepare(item,
            FormatPreferences.AudioOnly, clipStartSeconds: 10, clipEndSeconds: 25).Choice);

        Assert.Equal(JobKind.Hls, clipped.Kind);
        Assert.Equal(ClipMode.Segments, ClipSelection.ModeFor(clipped));
        Assert.Equal(new MediaTimeRange(10, 25), JsonSerializer.Deserialize<MediaTimeRange>(clipped.MediaPlanJson!));
    }

    [Fact]
    public void Opus_and_mp3_outputs_do_not_claim_audio_clipping()
    {
        var opus = new MediaRepresentation
        {
            Id = "audio-opus", Url = new Uri("https://cdn.example.test/song.webm"),
            Content = StreamContent.AudioOnly, Container = "webm", AudioCodec = "opus",
            FileSize = 524_288, ContentIdentity = "audio-opus:modified:524288"
        };
        MediaItem item = new()
        {
            SourceUrl = new Uri("https://audio.example.test/song"), Provider = "fixture", MediaId = "song",
            Title = "Song", Kind = MediaKind.Audio, Duration = TimeSpan.FromSeconds(60), Representations = [opus]
        };

        MediaDownloadChoice opusChoice = Assert.IsType<MediaDownloadChoice>(MediaTransferBridge.Prepare(item,
            FormatPreferences.AudioOnly, output: MediaOutputPreference.Opus).Choice);
        Assert.Equal(ClipMode.None, ClipSelection.ModeFor(opusChoice));
        Assert.Null(MediaTransferBridge.Prepare(item, FormatPreferences.AudioOnly,
            output: MediaOutputPreference.Opus, clipStartSeconds: 10, clipEndSeconds: 25).Choice);

        var mp3Clip = MediaTransferBridge.Prepare(item, FormatPreferences.AudioOnly,
            output: MediaOutputPreference.Mp3, clipStartSeconds: 10, clipEndSeconds: 25);
        Assert.Null(mp3Clip.Choice);
        Assert.Contains("MP3 clipping", mp3Clip.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Local_mp4_range_refuses_selected_subtitles_and_unsafe_sources()
    {
        MediaRepresentation stream = MuxedMp4("137", "youtube:video:137:modified:2097152");
        MediaItem item = VideoItem(stream) with
        {
            Subtitles = [new SubtitleTrack(new Uri("https://cdn.example.test/en.vtt"), "en", "English", "vtt")]
        };
        FormatPreferences preferences = FormatPreferences.BestQuality with { SubtitleLanguages = ["en"] };
        MediaDownloadChoice fullChoice = Assert.IsType<MediaDownloadChoice>(MediaTransferBridge.Prepare(item,
            preferences, output: MediaOutputPreference.CompatibleVideo).Choice);
        Assert.Equal(ClipMode.FullFile, ClipSelection.ModeFor(fullChoice));
        var captionClip = MediaTransferBridge.Prepare(item, preferences,
            output: MediaOutputPreference.CompatibleVideo, clipStartSeconds: 10, clipEndSeconds: 25);
        Assert.Null(captionClip.Choice);
        Assert.Contains("embedded subtitles", captionClip.Error, StringComparison.OrdinalIgnoreCase);

        MediaRepresentation[] unsafeSources =
        [
            MuxedMp4("no-identity", null),
            MuxedMp4("estimated", "youtube:estimated") with { FileSizeIsEstimate = true },
            MuxedMp4("he-aac", "youtube:he-aac") with { RawCodecs = "avc1.640028, mp4a.40.5" }
        ];
        foreach (MediaRepresentation source in unsafeSources)
        {
            MediaDownloadChoice choice = Assert.IsType<MediaDownloadChoice>(MediaTransferBridge.Prepare(
                VideoItem(source), FormatPreferences.BestQuality, output: MediaOutputPreference.CompatibleVideo).Choice);
            Assert.False(choice.CanUseFullFileClip);
            Assert.Equal(ClipMode.None, ClipSelection.ModeFor(choice));
        }
    }

    private static MediaRepresentation MuxedMp4(string id, string? identity) => new()
    {
        Id = id, Url = new Uri("https://cdn.example.test/video.mp4"), Content = StreamContent.VideoAndAudio,
        Container = "mp4", VideoCodec = "h264", AudioCodec = "aac", RawCodecs = "avc1.640028, mp4a.40.2",
        Height = 1080, FileSize = 2_097_152, ContentIdentity = identity
    };

    private static MediaItem VideoItem(params MediaRepresentation[] representations) => new()
    {
        SourceUrl = new Uri("https://video.example.test/watch/one"), Provider = "youtube", MediaId = "video",
        Title = "Video", Duration = TimeSpan.FromSeconds(60), Representations = representations
    };

    [Theory]
    [InlineData("CONIN$.txt")]
    [InlineData("CONOUT$")]
    [InlineData("COM¹.log")]
    [InlineData("LPT³.txt")]
    [InlineData("NUL .txt")]
    public void EngineAndResolverShareWindowsDeviceNameRules(string name)
    {
        Assert.StartsWith("_", FileNames.Sanitize(name));
        Assert.StartsWith("_", Vesper.MediaResolver.Security.FileNameSanitizer.Sanitize(name));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":42,\"html_url\":\"https://github.com/DarkOrbitWeaver/VESPER/releases/tag/v1\"}")]
    [InlineData("{\"tag_name\":\"v1\",\"html_url\":\"https://attacker.example/release\"}")]
    public void UpdateResponseCannotOpenMissingOrUntrustedReleaseLinks(string json) =>
        Assert.Throws<IOException>(() => ReleaseCheck.ParseRelease(json));

}
