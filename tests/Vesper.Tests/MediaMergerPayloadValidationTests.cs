using System.Buffers.Binary;
using System.Text;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class MediaMergerPayloadValidationTests
{
    [Fact]
    public void Mp4_remux_validation_rejects_intermediate_timestamp_changes_with_unchanged_payload_and_duration()
    {
        using var expectedSource = new MemoryStream(Fixture("video-only.mp4"));
        using var muxSource = new MemoryStream(Fixture("video-only.mp4"));
        DemuxedTrack expected = Assert.Single(MediaMerger.Demux(expectedSource, expectedSource.Length, CancellationToken.None));
        DemuxedTrack changed = Assert.Single(MediaMerger.Demux(muxSource, muxSource.Length, CancellationToken.None));
        Assert.True(changed.Samples.Count >= 3);

        TrackSample first = changed.Samples[0];
        TrackSample second = changed.Samples[1];
        first.Duration = checked(first.Duration + 1);
        second.Duration = checked(second.Duration - 1);
        changed.Samples[0] = first;
        changed.Samples[1] = second;
        int sampleIndex = Math.Min(2, changed.Samples.Count - 1);
        TrackSample shiftedPresentation = changed.Samples[sampleIndex];
        shiftedPresentation.CtsOffset = checked(shiftedPresentation.CtsOffset + 900);
        changed.Samples[sampleIndex] = shiftedPresentation;

        using var output = new MemoryStream();
        var changedInputs = new[] { (changed, (Stream)muxSource) };
        var expectedInputs = new[] { (expected, (Stream)expectedSource) };
        MediaMerger.Merge(changedInputs, "mp4", output, CancellationToken.None);

        List<DemuxedTrack> remuxed = MediaMerger.Demux(output, output.Length, CancellationToken.None);
        DemuxedTrack actual = Assert.Single(remuxed);
        Assert.Equal(expected.Samples.Count, actual.Samples.Count);
        Assert.Equal(expected.PayloadBytes, actual.PayloadBytes);
        Assert.Equal(expected.DurationTicks, actual.DurationTicks);
        AssertSamePayloads(expected, expectedSource, actual, output);
        MediaMerger.Validate(output, output.Length, [expected], CancellationToken.None);
        Assert.Throws<MediaContainerException>(() => MediaMerger.ValidateRemux(
            output, output.Length, expectedInputs, CancellationToken.None));
    }

    [Fact]
    public void Mp4_remux_validation_rejects_changed_keyframe_flags_with_unchanged_samples_and_duration()
    {
        using var expectedSource = new MemoryStream(Fixture("video-only.mp4"));
        using var muxSource = new MemoryStream(Fixture("video-only.mp4"));
        DemuxedTrack expected = Assert.Single(MediaMerger.Demux(expectedSource, expectedSource.Length, CancellationToken.None));
        DemuxedTrack changed = Assert.Single(MediaMerger.Demux(muxSource, muxSource.Length, CancellationToken.None));
        Assert.True(changed.Samples.Count >= 3);

        TrackSample sample = changed.Samples[1];
        sample.Key = !sample.Key;
        changed.Samples[1] = sample;

        using var output = new MemoryStream();
        var changedInputs = new[] { (changed, (Stream)muxSource) };
        var expectedInputs = new[] { (expected, (Stream)expectedSource) };
        MediaMerger.Merge(changedInputs, "mp4", output, CancellationToken.None);

        DemuxedTrack actual = Assert.Single(MediaMerger.Demux(output, output.Length, CancellationToken.None));
        Assert.Equal(expected.Samples.Count, actual.Samples.Count);
        Assert.Equal(expected.PayloadBytes, actual.PayloadBytes);
        Assert.Equal(expected.DurationTicks, actual.DurationTicks);
        AssertSamePayloads(expected, expectedSource, actual, output);
        MediaMerger.Validate(output, output.Length, [expected], CancellationToken.None);
        Assert.Throws<MediaContainerException>(() => MediaMerger.ValidateRemux(
            output, output.Length, expectedInputs, CancellationToken.None));
    }

    [Fact]
    public void Mp4_remux_preserves_nonzero_decode_origin_and_intertrack_presentation_offset()
    {
        using var videoSource = new MemoryStream(Fixture("video-only.mp4"));
        byte[] audioBytes = [.. Fixture("audio-init.mp4"), .. Fixture("audio-1.m4s")];
        using var audioSource = new MemoryStream(audioBytes);
        DemuxedTrack video = Assert.Single(MediaMerger.Demux(videoSource, videoSource.Length, CancellationToken.None));
        DemuxedTrack audio = Assert.Single(MediaMerger.Demux(audioSource, audioSource.Length, CancellationToken.None));
        long decodeOrigin = Math.Max(1L, video.Timescale / 5L);
        for (int i = 0; i < video.Samples.Count; i++)
        {
            TrackSample sample = video.Samples[i];
            sample.Dts = checked(sample.Dts + decodeOrigin);
            video.Samples[i] = sample;
        }

        var inputs = new[] { (video, (Stream)videoSource), (audio, (Stream)audioSource) };
        using var output = new MemoryStream();
        MediaMerger.Merge(inputs, "mp4", output, CancellationToken.None);
        MediaMerger.ValidateRemux(output, output.Length, inputs, CancellationToken.None);

        DemuxedTrack outputVideo = Assert.Single(MediaMerger.Demux(output, output.Length, CancellationToken.None),
            track => track.Kind == TrackKind.Video);
        DemuxedTrack outputAudio = Assert.Single(MediaMerger.Demux(output, output.Length, CancellationToken.None),
            track => track.Kind == TrackKind.Audio);
        Assert.Equal(0L, outputVideo.Samples[0].Dts);
        Assert.NotEqual(video.Samples[0].Dts, outputVideo.Samples[0].Dts);
        Assert.Equal(PresentationOffset(video, audio), PresentationOffset(outputVideo, outputAudio));
    }

    [Theory]
    [InlineData(1000u, 250L, 1000u, 250L)]
    [InlineData(48000u, 12001L, 48000u, 12001L)]
    public void Mp4_remux_preserves_a_single_media_edit_with_a_leading_time_and_end_trim(
        uint sourceScale, long sourceTicks, uint outputScale, long outputTicks)
    {
        using var originalSource = new MemoryStream(Fixture("video-only.mp4"));
        DemuxedTrack original = Assert.Single(MediaMerger.Demux(originalSource, originalSource.Length, CancellationToken.None));
        long mediaTime = Math.Max(1, original.Timescale / 4);
        ulong segmentDuration = (ulong)outputTicks;
        original.EditList = [new Mp4EditListEntry((ulong)sourceTicks, mediaTime, 1, 0)];
        original.EditMediaTime = mediaTime;
        original.EditMovieTimescale = sourceScale;

        using var editedSource = new MemoryStream();
        Mp4Muxer.Write([(original, (Stream)originalSource)], editedSource, CancellationToken.None,
            null, null, out _);
        DemuxedTrack edited = Assert.Single(MediaMerger.Demux(editedSource, editedSource.Length, CancellationToken.None));
        Mp4EditListEntry sourceEdit = Assert.Single(edited.EditList!);
        Assert.Equal(mediaTime, sourceEdit.MediaTime);
        Assert.Equal(segmentDuration, sourceEdit.SegmentDuration);
        Assert.Equal(segmentDuration, edited.TrackHeaderDurationMovieTicks);
        Assert.Equal(segmentDuration, edited.MovieHeaderDurationMovieTicks);
        Assert.Equal(outputScale, edited.MovieTimescale);
        AssertEditEndsBeforeMedia(edited, sourceEdit);

        var inputs = new[] { (edited, (Stream)editedSource) };
        using var output = new MemoryStream();
        MediaMerger.Merge(inputs, "mp4", output, CancellationToken.None);
        MediaMerger.ValidateRemux(output, output.Length, inputs, CancellationToken.None);

        DemuxedTrack remuxed = Assert.Single(MediaMerger.Demux(output, output.Length, CancellationToken.None));
        Mp4EditListEntry outputEdit = Assert.Single(remuxed.EditList!);
        Assert.Equal(sourceEdit.MediaTime, outputEdit.MediaTime);
        Assert.Equal(sourceEdit.SegmentDuration, outputEdit.SegmentDuration);
        Assert.Equal(sourceEdit.MediaRateInteger, outputEdit.MediaRateInteger);
        Assert.Equal(sourceEdit.MediaRateFraction, outputEdit.MediaRateFraction);
        Assert.Equal(segmentDuration, remuxed.TrackHeaderDurationMovieTicks);
        Assert.Equal(segmentDuration, remuxed.MovieHeaderDurationMovieTicks);
        Assert.Equal(outputScale, remuxed.MovieTimescale);
        AssertEditEndsBeforeMedia(remuxed, outputEdit);
    }

    [Theory]
    [InlineData("tkhd")]
    [InlineData("mvhd")]
    public void Mp4_remux_validation_rejects_a_header_duration_mismatch(string boxType)
    {
        using var source = new MemoryStream(Fixture("video-only.mp4"));
        DemuxedTrack track = Assert.Single(MediaMerger.Demux(source, source.Length, CancellationToken.None));
        var inputs = new[] { (track, (Stream)source) };
        using var output = new MemoryStream();
        MediaMerger.Merge(inputs, "mp4", output, CancellationToken.None);

        byte[] bytes = output.ToArray();
        int typeIndex = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(boxType));
        Assert.NotEqual(-1, typeIndex);
        int durationOffset = boxType == "tkhd" ? typeIndex + 24 : typeIndex + 20;
        uint duration = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(durationOffset, 4));
        Assert.True(duration > 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(durationOffset, 4), duration - 1);
        output.SetLength(0);
        output.Write(bytes);

        Assert.Throws<MediaContainerException>(() => MediaMerger.ValidateRemux(
            output, output.Length, inputs, CancellationToken.None));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("multiple")]
    [InlineData("empty-segment")]
    [InlineData("non-unit-rate")]
    public void Mp4_remux_rejects_unsupported_edit_lists_before_writing(string shape)
    {
        using var source = new MemoryStream(Fixture("video-only.mp4"));
        DemuxedTrack track = Assert.Single(MediaMerger.Demux(source, source.Length, CancellationToken.None));
        ulong duration = (ulong)Math.Max(1, track.DurationTicks * 1000 / track.Timescale);
        track.EditMovieTimescale = 1000;
        track.EditList = shape switch
        {
            "empty" => [],
            "multiple" => [new Mp4EditListEntry(duration / 2, 0, 1, 0),
                new Mp4EditListEntry(duration - duration / 2, 0, 1, 0)],
            "empty-segment" => [new Mp4EditListEntry(duration, -1, 1, 0)],
            "non-unit-rate" => [new Mp4EditListEntry(duration, 0, 2, 0)],
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        using var output = new MemoryStream();
        Assert.Throws<MediaContainerException>(() => Mp4Muxer.Write([(track, (Stream)source)], output,
            CancellationToken.None, null, null, out _));
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData("leading")]
    [InlineData("end")]
    [InlineData("empty")]
    [InlineData("multiple")]
    [InlineData("non-unit-rate")]
    public void Matroska_remux_rejects_unrepresentable_mp4_edit_lists_before_writing(string shape)
    {
        using var source = new MemoryStream(Fixture("video-only.mp4"));
        DemuxedTrack track = Assert.Single(MediaMerger.Demux(source, source.Length, CancellationToken.None));
        ulong fullDuration = (ulong)Math.Max(1, track.DurationTicks * 1000 / track.Timescale);
        track.EditMovieTimescale = 1000;
        track.EditList = shape switch
        {
            "leading" => [new Mp4EditListEntry(fullDuration / 2, Math.Max(1, track.Timescale / 2), 1, 0)],
            "end" => [new Mp4EditListEntry(Math.Max(1, fullDuration / 2), 0, 1, 0)],
            "empty" => [new Mp4EditListEntry(fullDuration, -1, 1, 0)],
            "multiple" => [new Mp4EditListEntry(fullDuration / 2, 0, 1, 0),
                new Mp4EditListEntry(fullDuration - fullDuration / 2, 0, 1, 0)],
            "non-unit-rate" => [new Mp4EditListEntry(fullDuration, 0, 2, 0)],
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        using var output = new MemoryStream();
        Assert.Throws<MediaContainerException>(() => MediaMerger.Merge([(track, (Stream)source)], "mkv",
            output, CancellationToken.None));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Matroska_remux_validation_rejects_changed_sample_durations_and_preserves_key_flags()
    {
        using var expectedSource = new MemoryStream(Fixture("video-only.mp4"));
        using var muxSource = new MemoryStream(Fixture("video-only.mp4"));
        DemuxedTrack expected = Assert.Single(MediaMerger.Demux(expectedSource, expectedSource.Length, CancellationToken.None));
        DemuxedTrack changed = Assert.Single(MediaMerger.Demux(muxSource, muxSource.Length, CancellationToken.None));
        Assert.True(changed.Samples.Count >= 3);

        using var baseline = new MemoryStream();
        var expectedInputs = new[] { (expected, (Stream)expectedSource) };
        MediaMerger.Merge(expectedInputs, "mkv", baseline, CancellationToken.None);
        MediaMerger.ValidateRemux(baseline, baseline.Length, expectedInputs, CancellationToken.None);

        TrackSample first = changed.Samples[0];
        TrackSample second = changed.Samples[1];
        int delta = Math.Max(1, checked((int)(changed.Timescale / 200)));
        Assert.True(first.Duration > delta);
        Assert.True(second.Duration > delta);
        first.Duration = checked(first.Duration + delta);
        second.Duration = checked(second.Duration - delta);
        changed.Samples[0] = first;
        changed.Samples[1] = second;

        using var output = new MemoryStream();
        var changedInputs = new[] { (changed, (Stream)muxSource) };
        MediaMerger.Merge(changedInputs, "mkv", output, CancellationToken.None);

        DemuxedTrack actual = Assert.Single(MediaMerger.Demux(output, output.Length, CancellationToken.None));
        Assert.Equal(expected.Samples.Count, actual.Samples.Count);
        Assert.Equal(expected.PayloadBytes, actual.PayloadBytes);
        Assert.Equal(expected.Samples.Select(sample => sample.Key), actual.Samples.Select(sample => sample.Key));
        AssertSamePayloads(expected, expectedSource, actual, output);
        MatroskaMuxer.Timeline expectedTimeline = MatroskaMuxer.BuildTimeline([expected], includeDecodeTime: false);
        MatroskaMuxer.Timeline actualTimeline = MatroskaMuxer.BuildTimeline([actual], includeDecodeTime: false);
        Assert.Equal(expectedTimeline.DurationMilliseconds, actualTimeline.DurationMilliseconds);
        long expectedFirstDuration = MatroskaMuxer.SampleDurationMilliseconds(expected, expected.Samples[0],
            expectedTimeline.ShiftNanoseconds, expectedTimeline.Tracks[0].PresentationMilliseconds[0]);
        Assert.NotEqual(expectedFirstDuration, (long)actual.Samples[0].Duration);
        MediaMerger.Validate(output, output.Length, [expected], CancellationToken.None);
        Assert.Throws<MediaContainerException>(() => MediaMerger.ValidateRemux(
            output, output.Length, expectedInputs, CancellationToken.None));
    }

    [Fact]
    public void Matroska_remux_validation_rejects_intermediate_pts_changes_after_timecode_quantization()
    {
        using var expectedSource = new MemoryStream(Fixture("video-only.mp4"));
        using var muxSource = new MemoryStream(Fixture("video-only.mp4"));
        DemuxedTrack expected = Assert.Single(MediaMerger.Demux(expectedSource, expectedSource.Length, CancellationToken.None));
        DemuxedTrack changed = Assert.Single(MediaMerger.Demux(muxSource, muxSource.Length, CancellationToken.None));
        Assert.True(changed.Samples.Count >= 5);

        using var baseline = new MemoryStream();
        var expectedInputs = new[] { (expected, (Stream)expectedSource) };
        MediaMerger.Merge(expectedInputs, "mkv", baseline, CancellationToken.None);
        MediaMerger.ValidateRemux(baseline, baseline.Length, expectedInputs, CancellationToken.None);
        MatroskaMuxer.Timeline expectedTimeline = MatroskaMuxer.BuildTimeline([expected], includeDecodeTime: false);

        int deltaTicks = Math.Max(1, checked((int)Math.Ceiling(changed.Timescale / 50d)));
        int deltaMilliseconds = Math.Max(1, checked((int)Math.Ceiling(deltaTicks * 1000d / changed.Timescale)));
        int sampleIndex = Enumerable.Range(1, changed.Samples.Count - 2).FirstOrDefault(index =>
        {
            long presentation = expectedTimeline.Tracks[0].PresentationMilliseconds[index];
            long duration = MatroskaMuxer.SampleDurationMilliseconds(expected, expected.Samples[index],
                expectedTimeline.ShiftNanoseconds, presentation);
            return presentation > expectedTimeline.Tracks[0].PresentationMilliseconds.Min() + deltaMilliseconds
                && presentation + duration + deltaMilliseconds < expectedTimeline.DurationMilliseconds;
        }, -1);
        Assert.NotEqual(-1, sampleIndex);
        TrackSample shifted = changed.Samples[sampleIndex];
        shifted.CtsOffset = checked(shifted.CtsOffset + deltaTicks);
        changed.Samples[sampleIndex] = shifted;

        using var output = new MemoryStream();
        var changedInputs = new[] { (changed, (Stream)muxSource) };
        MediaMerger.Merge(changedInputs, "mkv", output, CancellationToken.None);

        DemuxedTrack actual = Assert.Single(MediaMerger.Demux(output, output.Length, CancellationToken.None));
        Assert.Equal(expected.Samples.Count, actual.Samples.Count);
        Assert.Equal(expected.PayloadBytes, actual.PayloadBytes);
        AssertSamePayloads(expected, expectedSource, actual, output);
        MatroskaMuxer.Timeline actualTimeline = MatroskaMuxer.BuildTimeline([actual], includeDecodeTime: false);
        Assert.Equal(expectedTimeline.DurationMilliseconds, actualTimeline.DurationMilliseconds);
        MediaMerger.Validate(output, output.Length, [expected], CancellationToken.None);
        Assert.Throws<MediaContainerException>(() => MediaMerger.ValidateRemux(
            output, output.Length, expectedInputs, CancellationToken.None));
    }

    [Fact]
    public void Remux_validation_rejects_changed_sample_bytes_with_the_same_container_shape()
    {
        using var source = new MemoryStream(Fixture("video-only.mp4"));
        List<DemuxedTrack> sourceTracks = MediaMerger.Demux(source, source.Length, CancellationToken.None);
        var inputs = new List<(DemuxedTrack Track, Stream Source)> { (Assert.Single(sourceTracks), source) };
        using var output = new MemoryStream();

        MediaMerger.Merge(inputs, "mp4", output, CancellationToken.None);
        long outputLength = output.Length;
        MediaMerger.ValidateRemux(output, outputLength, inputs, CancellationToken.None);

        DemuxedTrack before = Assert.Single(MediaMerger.Demux(output, outputLength, CancellationToken.None));
        TrackSample sample = before.Samples[^1];
        long corruptOffset = sample.Offset + sample.Size - 1;
        output.Position = corruptOffset;
        int original = output.ReadByte();
        Assert.InRange(original, 0, 255);
        output.Position = corruptOffset;
        output.WriteByte((byte)(original ^ 1));

        DemuxedTrack after = Assert.Single(MediaMerger.Demux(output, outputLength, CancellationToken.None));
        Assert.Equal(before.Samples.Count, after.Samples.Count);
        Assert.Equal(before.Samples.Select(item => item.Size), after.Samples.Select(item => item.Size));
        Assert.Equal(before.DurationSeconds, after.DurationSeconds);

        MediaMerger.Validate(output, outputLength, [inputs[0].Track], CancellationToken.None);
        Assert.Throws<MediaContainerException>(() => MediaMerger.ValidateRemux(
            output, outputLength, inputs, CancellationToken.None));
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "Media", name));

    private static void AssertSamePayloads(DemuxedTrack expected, Stream expectedSource,
        DemuxedTrack actual, Stream actualOutput)
    {
        Assert.Equal(expected.Samples.Count, actual.Samples.Count);
        byte[] sourceBytes = new byte[expected.Samples.Max(sample => sample.Size)];
        byte[] outputBytes = new byte[actual.Samples.Max(sample => sample.Size)];
        for (int sampleIndex = 0; sampleIndex < expected.Samples.Count; sampleIndex++)
        {
            TrackSample sourceSample = expected.Samples[sampleIndex];
            TrackSample outputSample = actual.Samples[sampleIndex];
            Assert.Equal(sourceSample.Size, outputSample.Size);
            MuxIo.ReadExact(expectedSource, sourceSample.Offset, sourceBytes.AsSpan(0, sourceSample.Size));
            MuxIo.ReadExact(actualOutput, outputSample.Offset, outputBytes.AsSpan(0, outputSample.Size));
            Assert.True(sourceBytes.AsSpan(0, sourceSample.Size).SequenceEqual(outputBytes.AsSpan(0, outputSample.Size)),
                $"Sample {sampleIndex + 1} changed during remux.");
        }
    }

    private static void AssertEditEndsBeforeMedia(DemuxedTrack track, Mp4EditListEntry edit)
    {
        long mediaEnd = track.Samples.Max(sample =>
            checked(sample.Dts + sample.CtsOffset + sample.Duration));
        Assert.True(edit.MediaTime > 0);
        Assert.True((Int128)edit.MediaTime * track.EditMovieTimescale
            + (Int128)edit.SegmentDuration * track.Timescale
            < (Int128)mediaEnd * track.EditMovieTimescale);
    }

    private static Int128 PresentationOffset(DemuxedTrack video, DemuxedTrack audio)
    {
        TrackSample videoSample = video.Samples[0];
        TrackSample audioSample = audio.Samples[0];
        long videoPts = checked(videoSample.Dts + videoSample.CtsOffset - Math.Max(0, video.EditMediaTime));
        long audioPts = checked(audioSample.Dts + audioSample.CtsOffset - Math.Max(0, audio.EditMediaTime));
        return (Int128)videoPts * audio.Timescale - (Int128)audioPts * video.Timescale;
    }
}
