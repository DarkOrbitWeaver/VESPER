using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Tests.TestSupport;
using Xunit;

namespace Vesper.MediaResolver.Tests.FormatTests;

public sealed class FormatDeduplicatorRegressionTests
{
    [Fact]
    public void KeepsDistinctAudioTracksAndEncodingChoices()
    {
        var original = T.Audio("original", lang: "en", isDefault: true) with
        {
            AudioSampleRate = 44100,
            AudioTrackId = "en.0",
            ContentIdentity = "encoded-a"
        };
        var same = original with { Id = "same" };
        var variants = new[]
        {
            original,
            same,
            original with { Id = "rate", AudioSampleRate = 48000 },
            original with { Id = "track", AudioTrackId = "en.1" },
            original with { Id = "default", IsDefaultAudio = false },
            original with { Id = "description", IsAudioDescription = true },
            original with { Id = "drc", IsDrc = true },
            original with { Id = "content", ContentIdentity = "encoded-b" }
        };

        var result = FormatDeduplicator.Deduplicate(variants);

        Assert.Equal(7, result.Count);
        Assert.DoesNotContain(result, r => r.Id == "same");
    }

    [Fact]
    public void KeepsDifferentWidthsAndProcessingRequirements()
    {
        var original = T.Video("original", 720) with
        {
            Width = 1280,
            Requirements = ProcessingRequirement.HlsSegmentAssembly
        };
        var variants = new[]
        {
            original,
            original with { Id = "same transport", Protocol = StreamProtocol.Dash,
                Requirements = ProcessingRequirement.DashSegmentAssembly },
            original with { Id = "portrait", Width = 720 },
            original with { Id = "encrypted", Requirements = ProcessingRequirement.HlsSegmentAssembly | ProcessingRequirement.DecryptAes128 }
        };

        var result = FormatDeduplicator.Deduplicate(variants);

        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(result, r => r.Id == "same transport");
        Assert.Contains(result, r => r.Id == "portrait");
        Assert.Contains(result, r => r.Id == "encrypted");
    }

    [Fact]
    public void DoesNotDiscardFrameRateVariantThatPassesTheRequestedLimit()
    {
        var highBitrateOverLimit = T.Video("high-fps", 720, kbps: 1500, fps: 50.49);
        var lowerBitrateWithinLimit = T.Video("within-limit", 720, kbps: 1000, fps: 50.0);
        var item = T.Item(highBitrateOverLimit, lowerBitrateWithinLimit);
        item = item with { Representations = FormatDeduplicator.Deduplicate(item.Representations) };

        var selected = FormatSelector.Select(item, new FormatPreferences { MaxFrameRate = 49.5 });

        Assert.Equal("within-limit", selected.Selection?.Video?.Id);
    }
}
