using VESPER;
using Vesper.Engine;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;

namespace Vesper.Tests;

public sealed class AudioOutputTests
{
    private static readonly MediaRepresentation Opus = Audio("opus", "opus", "webm", 132);
    private static readonly MediaRepresentation Aac = Audio("aac", "aac", "m4a", 132);

    [Theory]
    [InlineData(QualityMode.AudioOnly)]
    [InlineData(QualityMode.Best)]
    public void Audio_keeps_the_selected_source_encoding_even_with_compatible_video_enabled(QualityMode mode)
    {
        var item = Item(Aac, Opus);
        MediaDownloadChoice choice = Assert.IsType<MediaDownloadChoice>(MediaTransferBridge.Prepare(item,
            new FormatPreferences { Mode = mode }, output: MediaOutputPreference.CompatibleVideo).Choice);
        Assert.Equal(Opus.Url.AbsoluteUri, choice.Url);
        Assert.Equal("WEBM", choice.DisplayContainer);
        Assert.Equal(MediaOutput.Original, choice.Output);
    }

    [Fact]
    public void Audio_only_prefers_original_Opus_over_higher_advertised_Aac_bitrate()
    {
        var item = Item(Aac with { BitrateKbps = 256 }, Opus, Opus with { Id = "opus-low", BitrateKbps = 96, Url = new Uri("https://example.com/opus-low.webm") });
        MediaDownloadChoice choice = Assert.IsType<MediaDownloadChoice>(
            MediaTransferBridge.Prepare(item, FormatPreferences.AudioOnly).Choice);
        Assert.Equal(Opus.Url.AbsoluteUri, choice.Url);
        Assert.Equal(MediaOutput.Original, choice.Output);
    }

    [Fact]
    public void Audio_only_can_keep_a_lossless_source_when_Opus_is_unavailable()
    {
        var flac = Audio("flac", "flac", "flac", 200);
        var item = Item(Aac with { BitrateKbps = 256 }, flac);
        MediaDownloadChoice choice = Assert.IsType<MediaDownloadChoice>(
            MediaTransferBridge.Prepare(item, FormatPreferences.AudioOnly).Choice);
        Assert.Equal(flac.Url.AbsoluteUri, choice.Url);
        Assert.Equal("FLAC", choice.DisplayContainer);
        Assert.Equal(MediaOutput.Original, choice.Output);
    }

    [Fact]
    public void Explicit_m4a_uses_the_best_source_audio_and_exact_formats_preserve_their_encoding()
    {
        var item = Item(Aac, Opus);
        MediaDownloadChoice m4a = Assert.IsType<MediaDownloadChoice>(
            MediaTransferBridge.Prepare(item, FormatPreferences.AudioOnly, output: MediaOutputPreference.M4a).Choice);
        Assert.Equal((OperatingSystem.IsWindows() ? Opus : Aac).Url.AbsoluteUri, m4a.Url);
        Assert.Equal(OperatingSystem.IsWindows() ? MediaOutput.M4a : MediaOutput.Original, m4a.Output);

        MediaDownloadChoice exact = Assert.IsType<MediaDownloadChoice>(
            MediaTransferBridge.Prepare(item, FormatPreferences.AudioOnly, Aac, MediaOutputPreference.M4a).Choice);
        Assert.Equal(Aac.Url.AbsoluteUri, exact.Url);
        Assert.Equal(MediaOutput.Original, exact.Output);
        Assert.Equal("audio aac 132 kbps", exact.DisplayDetail);
    }

    [Fact]
    public void Source_aac_in_mp4_saves_as_m4a_with_its_encoding_intact()
    {
        var aac = Audio("native", "aac", "mp4", 192);
        MediaDownloadChoice m4a = Assert.IsType<MediaDownloadChoice>(
            MediaTransferBridge.Prepare(Item(aac), FormatPreferences.AudioOnly, output: MediaOutputPreference.M4a).Choice);
        Assert.Equal(aac.Url.AbsoluteUri, m4a.Url);
        Assert.Equal("M4A", m4a.DisplayContainer);
        Assert.Equal(MediaOutput.Original, m4a.Output);
    }

    [Fact]
    public void M4a_keeps_the_highest_bitrate_native_aac_on_the_selected_track()
    {
        var nativeAac = Audio("native-320", "aac", "mp4", 320) with { AudioTrackId = "original", IsDefaultAudio = true };
        var opus = Opus with { AudioTrackId = "original", IsDefaultAudio = true };
        var other = Audio("other-384", "aac", "m4a", 384) with { AudioTrackId = "commentary", IsDefaultAudio = true };
        var item = Item(opus, nativeAac, other, nativeAac with { Id = "native-256", BitrateKbps = 256 });
        MediaDownloadChoice choice = Assert.IsType<MediaDownloadChoice>(
            MediaTransferBridge.Prepare(item, FormatPreferences.AudioOnly, output: MediaOutputPreference.M4a).Choice);
        Assert.Equal(nativeAac.Id, choice.FormatId);
        Assert.Equal(MediaOutput.Original, choice.Output);
        Assert.Equal("M4A", choice.DisplayContainer);
        Assert.Equal("audio aac 320 kbps", choice.DisplayDetail);
    }

    private static MediaItem Item(params MediaRepresentation[] streams) => new()
    {
        SourceUrl = new Uri("https://example.com/recording"), Title = "Recording", MediaId = "1", Provider = "fixture",
        Representations = streams
    };

    private static MediaRepresentation Audio(string id, string codec, string container, int bitrate) => new()
    {
        Id = id, Url = new Uri($"https://example.com/{id}.{container}"), Content = StreamContent.AudioOnly,
        Container = container, AudioCodec = codec, BitrateKbps = bitrate, AudioChannels = 2, AudioSampleRate = 48000
    };
}
