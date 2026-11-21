using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using VESPER;
using Vesper.Engine;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;

namespace Vesper.Tests;

public sealed class DiscoveryAndNativeOutputTests
{
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

    [Fact]
    public void Compatible_video_preset_keeps_fragmented_mp4_hls_quality_audio_and_subtitle_choices()
    {
        var hls = new MediaRepresentation
        {
            Id = "hls-1080", Url = new Uri("https://cdn.example.test/1080.m3u8"),
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
        Assert.Contains("remain separate", fullChoice.SubtitleNotice, StringComparison.OrdinalIgnoreCase);

        var clipped = MediaTransferBridge.Prepare(item, preferences, output: MediaOutputPreference.CompatibleVideo,
            clipStartSeconds: 10, clipEndSeconds: 20);
        MediaDownloadChoice clipChoice = Assert.IsType<MediaDownloadChoice>(clipped.Choice);
        Assert.Equal(hls.Id, clipChoice.FormatId);
        Assert.Equal(JobKind.Hls, clipChoice.Kind);
        Assert.Equal(new MediaTimeRange(10, 20), JsonSerializer.Deserialize<MediaTimeRange>(clipChoice.MediaPlanJson!));
        Assert.Equal(ClipMode.Segments, ClipSelection.ModeFor(clipChoice));
    }

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
