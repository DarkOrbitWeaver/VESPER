using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Vesper.Engine;

public sealed partial class DownloadEngine
{
    private sealed record PreparedMediaFile(long Bytes, string Digest, string Path, string FileName, string Identity);
    private enum AudioMetadataWriteStatus { Written, NoChanges, ExistingTags, UnsupportedAudio }
    // Only encoders need a single slot. Lossless, file-local MP4/M4A remuxes must not wait behind transcoding.
    private readonly SemaphoreSlim _conversionSlot = new(1);
    private readonly SemaphoreSlim _remuxSlots = new(2, 2); // Two simultaneous file copies, even with many forced jobs.

    private static string OutputExtension(MediaOutput output) => output switch
    { MediaOutput.M4a => ".m4a", MediaOutput.Opus => ".opus", MediaOutput.Wav => ".wav", MediaOutput.Mp3 => ".mp3", _ => ".mp4" };
    private static string ConversionPath(DownloadJob job) => job.PartialPath + ".convert"
        + (job.Output is MediaOutput.Opus or MediaOutput.Wav or MediaOutput.Mp3 ? OutputExtension(job.Output) : ".mp4");
    private static string ConversionAudioPath(DownloadJob job) => job.PartialPath + ".convert-audio.mp4";
    private static string ConversionTaggedPath(DownloadJob job) => job.PartialPath + ".convert-tagged.mp4";
    private static string ConversionMp3TaggedPath(DownloadJob job) => job.PartialPath + ".convert-tagged.mp3";
    private static string AudioMetadataPath(DownloadJob job) => job.PartialPath + ".tagged" + Path.GetExtension(job.FileName);

    internal static bool SupportsConversionAudioFormat(MediaOutput output, int channels, int sampleRate) =>
        channels is 1 or 2 && (sampleRate is 44100 or 48000
            || output == MediaOutput.Mp3 && sampleRate == 32000);

    internal static string PublicationStagePath(DownloadJob job) =>
        job.Output == MediaOutput.Original && job.MediaMetadataJson is not null
            ? AudioMetadataPath(job) : ConversionPath(job);

    private async Task<(PreparedMediaFile? File, string? Notice)> ConvertMediaAsync(DownloadJob job, CancellationToken ct)
    {
        PreparedMediaFile? file = null;
        string? notice = null;
        if (job.ExpectedSha256 is not null) return (file, notice);
        UpdateJob(job, j => { j.State = JobState.Converting; j.Reason = ""; j.Message = "Preparing a file for more devices."; });
        string stage = ConversionPath(job);
        string? stageIdentity = null;
        string? taggedIdentity = null;
        bool ready = false;
        bool preserveConversionStagesForAttention = false;
        bool remuxPermit = false;
        bool wavFeaturesRequested = WavFeaturesRequested(job);
        MediaTimeRange? audioClip = job.Kind == JobKind.Http && job.Output == MediaOutput.M4a
            && MediaTimeRange.TryParse(job.MediaPlanJson, out MediaTimeRange m4aRange) ? m4aRange : null;
        if (job.Kind == JobKind.Http && job.Output == MediaOutput.M4a
            && job.MediaPlanJson is not null && audioClip is null) return (null, null);
        try
        {
            if (job.Output == MediaOutput.Wav && FindPreviousWavRollback(job, stage) is { } previousBackup)
                throw new LocalStorageException("A previous WAV stage backup remains at "
                    + PathSafety.ForDisplay(previousBackup) + ". Recover or move the stage out of the download folder, then resume.",
                    new IOException("The WAV stage path is occupied."));

            if (PathSafety.Inspect(job.PartialPath) is not { Exists: true, IsDirectory: false, IsReparsePoint: false, IsCloudPlaceholder: false })
                return (file, notice);

            using var source = PartialFile.OpenOwnedRead(job);
            List<DemuxedTrack> tracks;
            using (ProcessingTiming.Measure(job.Id, ProcessingPhase.Probe, source.Length))
                tracks = MediaMerger.Demux(source, source.Length, ct);
            DemuxedTrack? audio = tracks.Find(t => t.Kind == TrackKind.Audio);
            DemuxedTrack? video = tracks.Find(t => t.Kind == TrackKind.Video);
            bool wantVideo = job.Output == MediaOutput.Mp4;
            bool tracksMatch = wantVideo
                ? tracks.Count == 2 && video is not null
                : job.Output == MediaOutput.Mp3
                    ? tracks.Count == (video is null ? 1 : 2)
                    : tracks.Count == 1 && video is null;
            if (!tracksMatch || audio is null)
                return (file, notice);

            if (audioClip is not null)
                Mp4Clip.Apply(tracks, audioClip, ct);

            file = await TryReusePreparedConversionAsync(job, source, tracks, audio, video, stage, ct).ConfigureAwait(false);
            if (file is not null)
            {
                ready = true;
                return (file, notice);
            }

            if (job.Output == MediaOutput.Opus)
            {
                var tags = ConvertedFileMetadata(job);
                var cover = ReadEmbeddedCover(source)
                    ?? await MediaThumbnail.FetchAsync(job.MediaThumbnailUrl, _http, Options.AllowPrivateNetworks, ct).ConfigureAwait(false);
                using (ProcessingTiming.Measure(job.Id, ProcessingPhase.CopyQueue))
                    await _remuxSlots.WaitAsync(ct).ConfigureAwait(false);
                remuxPermit = true;
                using var opus = CreateOwnedStage(stage, FileShare.None, out stageIdentity);
                string hash;
                using (ProcessingTiming.Measure(job.Id, ProcessingPhase.Remux, source.Length))
                    hash = await OpusMuxer.WriteAsync(audio, source, opus, tags, cover, ct).ConfigureAwait(false);
                using (ProcessingTiming.Measure(job.Id, ProcessingPhase.DiskFlush)) opus.Flush(flushToDisk: true);
                file = new PreparedMediaFile(opus.Length, hash, stage, Path.ChangeExtension(job.FileName, ".opus"),
                    stageIdentity ?? throw new IOException("The Opus stage has no saved identity."));
                PersistPreparedMedia(job, file);
                ready = true;
                return (file, job.MediaThumbnailUrl is not null && cover is null ? "Saved without unavailable source artwork." : null);
            }
            var remuxInputs = new List<(DemuxedTrack Track, Stream Source)>();
            string? remuxContainer = null;
            if (job.Output == MediaOutput.Mp4
                && video is { Codec: "h264", SourceContainer: "mp4", Mp4SampleEntry: not null }
                && audio is { Codec: "aac", SourceContainer: "mp4", Mp4SampleEntry: not null })
            {
                remuxContainer = "mp4";
                remuxInputs.Add((video, source));
                remuxInputs.Add((audio, source));
            }
            else if (job.Output == MediaOutput.M4a && video is null
                && audio is { Codec: "aac", SourceContainer: "mp4", Mp4SampleEntry: not null })
            {
                remuxContainer = "m4a";
                remuxInputs.Add((audio, source));
            }

            bool directRemux = (Options.ConvertMedia is null || audioClip is not null) && remuxContainer is not null
                && MediaMerger.ChooseContainer(remuxInputs.Select(input => input.Track).ToArray(), remuxContainer) == remuxContainer;
            if (audioClip is not null && !directRemux)
                return (null, null); // Never encode or publish the entire untrimmed source.
            if (directRemux)
            {
                MediaMergeMetadata? metadata;
                MediaThumbnail? cover;
                using (ProcessingTiming.Measure(job.Id, ProcessingPhase.Tags))
                {
                    metadata = ConvertedFileMetadata(job);
                    cover = ReadEmbeddedCover(source);
                    if (cover is null && !string.IsNullOrWhiteSpace(job.MediaThumbnailUrl))
                        cover = await MediaThumbnail.FetchAsync(job.MediaThumbnailUrl, _http, Options.AllowPrivateNetworks, ct).ConfigureAwait(false);
                }
                using (ProcessingTiming.Measure(job.Id, ProcessingPhase.CopyQueue))
                    await _remuxSlots.WaitAsync(ct).ConfigureAwait(false);
                remuxPermit = true;
                using var remuxed = CreateOwnedStage(stage, FileShare.None, out stageIdentity);
                MergeResult merged;
                using (ProcessingTiming.Measure(job.Id, ProcessingPhase.Remux, source.Length))
                    merged = MediaMerger.Merge(remuxInputs, remuxContainer!, remuxed, ct, metadata, cover);
                using (ProcessingTiming.Measure(job.Id, ProcessingPhase.Verify, remuxed.Length))
                {
                    var verified = MediaMerger.ValidateRemux(remuxed, remuxed.Length, remuxInputs, ct, merged.Metadata, merged.ThumbnailSha256);
                    ValidateConvertedMp4(remuxed, tracks, audio, video, stageIdentity, ct, verified);
                }
                if (cover is null && !string.IsNullOrWhiteSpace(job.MediaThumbnailUrl)) notice = "Saved without unavailable source artwork.";
            }
            else
            {
                if (!SupportsConversionAudioFormat(job.Output, audio.Channels, audio.SampleRate)
                    || video is { Width: < 16 or > 4096 } or { Height: < 16 or > 2304 } or { BitDepth: > 8 })
                    return (file, notice);

                string extension = audio.SourceContainer == "webm" ? ".mkv" : ".mp4";
                double totalSeconds = Math.Max(audio.DurationSeconds, video?.DurationSeconds ?? 0);
                int lastPercent = -1;
                Action<double> progress = seconds =>
                {
                    int percent = totalSeconds > 0 ? (int)Math.Clamp(seconds / totalSeconds * 100, 0, 99) : 0;
                    if (percent == lastPercent) return;
                    lastPercent = percent;
                    UpdateJob(job, j => j.Message = $"Converting {percent}% ({FormatClock(seconds)} of {FormatClock(totalSeconds)})");
                };
                if (Options.ConvertMedia is null && !OperatingSystem.IsWindows()) return (file, notice);
                // Native and injected encoders remain serialized (including RemuxWithAac's audio transcode).
                // Hold this gate only for the actual conversion, not source inspection, metadata, hashing,
                // or independent direct-remux jobs.
                using (ProcessingTiming.Measure(job.Id, ProcessingPhase.EncoderQueue))
                    await _conversionSlot.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var encoding = ProcessingTiming.Measure(job.Id, ProcessingPhase.Encode, source.Length);
                    if (Options.ConvertMedia is { } convert)
                    {
                        convert(job.PartialPath, extension, stage, job.Output, ct);
                        stageIdentity = CaptureTemporaryFileIdentity(stage);
                    }
                    else if (video is not null && TryRemuxWithAac(job, source, video, extension, stage, ct,
                        out stageIdentity) is { } copied)
                        video = copied;
                    else
                    {
                        using var stageOwner = WindowsMediaConverter.CreateOwnedOutputStage(stage, out stageIdentity);
                        WindowsMediaConverter.ConvertOwnedStage(job.PartialPath, extension, stage, job.Output, ct,
                            stageOwner, stageIdentity ?? throw new IOException("The conversion stage has no saved identity."),
                            video, progress);
                    }
                }
                finally { _conversionSlot.Release(); }
            }

            ct.ThrowIfCancellationRequested();
            if (job.Output == MediaOutput.Wav)
            {
                MediaTimeRange? range = RequestedWaveRange(job);
                MediaMergeMetadata? metadata = ConvertedFileMetadata(job);
                bool rewrite = range is not null || metadata is not null;
                string waveStage = ConversionTaggedPath(job);
                using (var decoded = File.OpenRead(stage))
                {
                    PathSafety.RequireTemporaryIdentity(decoded.SafeFileHandle, stageIdentity);
                    var info = WaveAudio.Read(decoded);
                    if (info.Channels != audio.Channels || info.SampleRate != audio.SampleRate
                        || Math.Abs(info.Duration - audio.DurationSeconds) > Math.Max(.25, audio.DurationSeconds * .01))
                        throw new MediaContainerException("The decoded WAV differs from the source timeline.");
                    if (rewrite)
                    {
                        using var wave = CreateOwnedStage(waveStage, FileShare.None, out taggedIdentity);
                        await WaveAudio.WriteAsync(decoded, wave, range, metadata, ct).ConfigureAwait(false);
                        wave.Flush(flushToDisk: true);
                    }
                }
                if (rewrite)
                {
                    try
                    {
                        ReplaceFile(job, waveStage, taggedIdentity, stage, stageIdentity, out string movedIdentity);
                        stageIdentity = movedIdentity;
                        taggedIdentity = null;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        preserveConversionStagesForAttention = true;
                        throw new LocalStorageException("The requested WAV clip or track details could not be saved safely at "
                            + PathSafety.ForDisplay(stage) + ". No file was published. Move any conflicting temporary stage "
                            + "out of the download folder, then resume.", ex);
                    }
                }
            }
            else if (job.Output == MediaOutput.Mp3)
            {
                using (var converted = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1 << 16, FileOptions.SequentialScan))
                {
                    ValidatePreparedMp3(converted, stageIdentity);
                }

                (notice, stageIdentity) = await EmbedConvertedMp3TagsAsync(job, source, stage,
                    stageIdentity ?? throw new IOException("The converted MP3 has no saved identity."), ct).ConfigureAwait(false);
            }
            else if (!directRemux)
            {
                using (var converted = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1 << 16, FileOptions.SequentialScan))
                {
                    ValidateConvertedMp4(converted, tracks, audio, video, stageIdentity, ct);
                }

                // The converter only writes audio and video. Title, artist and cover go in now, before the file is hashed.
                using (ProcessingTiming.Measure(job.Id, ProcessingPhase.Tags))
                    (notice, stageIdentity) = await EmbedConvertedTagsAsync(job, source, stage,
                    stageIdentity ?? throw new IOException("The converted file has no saved identity."), ct).ConfigureAwait(false);
            }

            using var output = new FileStream(stage, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            PathSafety.RequireTemporaryIdentity(output.SafeFileHandle, stageIdentity);
            using (ProcessingTiming.Measure(job.Id, ProcessingPhase.DiskFlush)) output.Flush(flushToDisk: true);
            string digest;
            using (ProcessingTiming.Measure(job.Id, ProcessingPhase.Hash, output.Length))
                digest = Convert.ToHexString(await SHA256.HashDataAsync(output, ct).ConfigureAwait(false));
            PathSafety.RequireTemporaryIdentity(output.SafeFileHandle, stageIdentity);
            file = new PreparedMediaFile(output.Length, digest, stage,
                Path.ChangeExtension(job.FileName, OutputExtension(job.Output)), stageIdentity
                    ?? throw new IOException("The converted file has no saved identity."));
            PersistPreparedMedia(job, file);
            ready = true;
            return (file, notice);
        }
        catch (LocalStorageException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or NotSupportedException
            or MediaContainerException or DllNotFoundException or EntryPointNotFoundException)
        {
            bool unverifiedStage = job.Output == MediaOutput.Wav
                && (HasUnverifiedConversionStage(stage, stageIdentity)
                    || HasUnverifiedConversionStage(ConversionTaggedPath(job), taggedIdentity));
            if (job.Output == MediaOutput.Wav && (wavFeaturesRequested || unverifiedStage))
            {
                preserveConversionStagesForAttention = true;
                throw new LocalStorageException("The requested WAV output could not be prepared safely at "
                    + PathSafety.ForDisplay(stage) + ". No file was published. Move any conflicting temporary stage "
                    + "out of the download folder, then resume.", ex);
            }
            Log(job.Id, "conversion-unavailable", ex.Message);
            return (null, null);
        }
        finally
        {
            if ((!ready && stageIdentity is not null) || taggedIdentity is not null)
                Failpoint("conversion.beforeTemporaryCleanup");
            if (!preserveConversionStagesForAttention && !ready && stageIdentity is not null)
                DeleteTemporaryFile(job, stage, stageIdentity);
            if (!preserveConversionStagesForAttention && taggedIdentity is not null)
                DeleteTemporaryFile(job, ConversionTaggedPath(job), taggedIdentity);
            if (remuxPermit) _remuxSlots.Release();
        }
    }

    private async Task<PreparedMediaFile?> TryReusePreparedConversionAsync(DownloadJob job, Stream source,
        List<DemuxedTrack> tracks, DemuxedTrack audio, DemuxedTrack? video, string stage, CancellationToken ct)
    {
        if (job.PendingConversionBytes is not long expectedBytes
            || job.PendingConversionDigest is not { Length: 64 } expectedDigest
            || job.PendingConversionIdentity is not { Length: 64 } expectedIdentity)
            return null;
        if (PathSafety.Inspect(stage) is not { Exists: true, IsDirectory: false, IsReparsePoint: false, IsCloudPlaceholder: false })
            return null;

        try
        {
            using var output = PathSafety.OpenTemporaryStream(stage, FileMode.Open, FileAccess.ReadWrite,
                FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            string identity = PathSafety.TemporaryFileIdentity(output.SafeFileHandle)
                ?? throw new IOException("The prepared conversion has no saved identity.");
            if (!string.Equals(identity, expectedIdentity, StringComparison.Ordinal)) return null;
            PathSafety.RequireTemporaryIdentity(output.SafeFileHandle, identity);
            if (output.Length != expectedBytes) return null;

            if (job.Output == MediaOutput.Wav)
            {
                WaveAudio.Info info = WaveAudio.Read(output);
                MediaTimeRange? range = RequestedWaveRange(job);
                double duration = range is null ? audio.DurationSeconds : range.EndSeconds - range.StartSeconds;
                if (info.Channels != audio.Channels || info.SampleRate != audio.SampleRate
                    || Math.Abs(info.Duration - duration) > Math.Max(.25, duration * .01))
                    return null;
            }
            else if (job.Output == MediaOutput.Opus)
            {
                ValidatePreparedOpus(output, source, audio, ct);
            }
            else if (job.Output == MediaOutput.Mp3)
            {
                ValidatePreparedMp3(output, identity);
            }
            else
            {
                ValidateConvertedMp4(output, tracks, audio, video, identity, ct);
            }

            output.Flush(flushToDisk: true);
            PathSafety.RequireTemporaryIdentity(output.SafeFileHandle, identity);
            output.Position = 0;
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(output, ct).ConfigureAwait(false));
            PathSafety.RequireTemporaryIdentity(output.SafeFileHandle, identity);
            if (!digest.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase)) return null;
            return new PreparedMediaFile(output.Length, digest, stage,
                Path.ChangeExtension(job.FileName, OutputExtension(job.Output)), identity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MediaContainerException
            or NotSupportedException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private PreparedMediaFile? TryReusePreparedAudioMetadata(DownloadJob job, string stage)
    {
        if (job.PendingConversionBytes is not long expectedBytes
            || job.PendingConversionDigest is not { Length: 64 } expectedDigest
            || job.PendingConversionIdentity is not { Length: 64 } expectedIdentity
            || PathSafety.Inspect(stage) is not { Exists: true, IsDirectory: false, IsReparsePoint: false, IsCloudPlaceholder: false })
            return null;

        try
        {
            using var output = PathSafety.OpenTemporaryStream(stage, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            PathSafety.RequireTemporaryIdentity(output.SafeFileHandle, expectedIdentity);
            if (output.Length != expectedBytes) return null;
            string digest = Convert.ToHexString(SHA256.HashData(output));
            PathSafety.RequireTemporaryIdentity(output.SafeFileHandle, expectedIdentity);
            if (!digest.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase)) return null;
            return new PreparedMediaFile(expectedBytes, digest, stage, job.FileName, expectedIdentity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private void PersistPreparedMedia(DownloadJob job, PreparedMediaFile prepared)
    {
        UpdateJob(job, saved =>
        {
            saved.PendingConversionBytes = prepared.Bytes;
            saved.PendingConversionDigest = prepared.Digest;
            saved.PendingConversionIdentity = prepared.Identity;
        });
    }

    private MediaTimeRange? RequestedWaveRange(DownloadJob job)
    {
        if (job.Kind == JobKind.Merge
            && MediaMergePlan.TryParse(job.MediaPlanJson, Options.AllowPrivateNetworks, out MediaMergePlan merge, out _))
            return merge.OutputRange;
        return job.MediaPlanJson is not null && MediaTimeRange.TryParse(job.MediaPlanJson, out MediaTimeRange range)
            ? range : null;
    }

    private static void ValidateConvertedMp4(FileStream converted, List<DemuxedTrack> tracks, DemuxedTrack audio,
        DemuxedTrack? video, string? identity, CancellationToken ct, List<DemuxedTrack>? verified = null)
    {
        PathSafety.RequireTemporaryIdentity(converted.SafeFileHandle, identity);
        List<DemuxedTrack> actual = verified ?? Mp4Demuxer.Read(converted, converted.Length, ct);
        DemuxedTrack? outAudio = actual.Find(t => t.Kind == TrackKind.Audio);
        DemuxedTrack? outVideo = actual.Find(t => t.Kind == TrackKind.Video);
        if (actual.Count != tracks.Count || outAudio is not { Codec: "aac" } || outAudio.Channels != audio.Channels
            || outAudio.SampleRate != audio.SampleRate || !SameDuration(audio, outAudio)
            || (video is not null && (outVideo is not { Codec: "h264" } || outVideo.Width != video.Width
                || outVideo.Height != video.Height || !SameDuration(video, outVideo)
                || outVideo.Samples.Count != video.Samples.Count)))
            throw new MediaContainerException("The converted file did not retain the expected audio and video.");
    }

    private static void ValidatePreparedOpus(Stream output, Stream source, DemuxedTrack audio, CancellationToken ct)
    {
        if (audio.Codec != "opus" || audio.HasLacing || audio.Samples.Count == 0)
            throw new MediaContainerException("The source does not have reusable Opus packets.");

        long granule = 0;
        foreach (TrackSample sample in audio.Samples)
        {
            ct.ThrowIfCancellationRequested();
            if (sample.Size is < 1 or > 61_440 || sample.Offset < 0 || sample.Offset > source.Length - sample.Size)
                throw new MediaContainerException("An Opus packet has an invalid size or position.");
            byte[] packet = new byte[sample.Size];
            source.Position = sample.Offset;
            source.ReadExactly(packet);
            granule = checked(granule + OpusMuxer.PacketSamples(packet));
        }

        OggAudioMetadataWriter.ValidateOpus(output, audio.Samples.Count, granule);
    }

    private static void ValidatePreparedMp3(FileStream output, string? identity)
    {
        PathSafety.RequireTemporaryIdentity(output.SafeFileHandle, identity);
        if (!Mp3MetadataWriter.IsPlayableMpegLayer3(output))
            throw new MediaContainerException("The converted file is not a valid MP3 stream.");
    }

    private bool WavFeaturesRequested(DownloadJob job)
    {
        if (job.Output != MediaOutput.Wav) return false;
        if (job.MediaMetadataJson is not null || job.MediaThumbnailUrl is not null) return true;

        if (job.Kind == JobKind.Merge)
        {
            if (!MediaMergePlan.TryParse(job.MediaPlanJson, Options.AllowPrivateNetworks, out MediaMergePlan plan, out _))
                return job.MediaPlanJson is not null;
            return plan.OutputRange is not null || plan.Metadata is not null;
        }

        return job.MediaPlanJson is not null && MediaTimeRange.TryParse(job.MediaPlanJson, out _);
    }

    private static bool HasUnverifiedConversionStage(string path, string? expectedIdentity)
    {
        try
        {
            if (PathSafety.Inspect(path) is not { Exists: true }) return false;
            if (expectedIdentity is null) return true;

            using SafeFileHandle file = PathSafety.OpenTemporaryHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, allowDelete: true);
            return !string.Equals(expectedIdentity, PathSafety.TemporaryFileIdentity(file), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static string FormatClock(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    /// <summary>
    /// Adds the title, artist, album and cover that were found for this media to a converted M4A/MP4.
    /// Returns a note for the user when something could not be embedded. The converted audio is never touched:
    /// a failure here keeps the valid untagged file.
    /// </summary>
    private async Task<(string? Notice, string Identity)> EmbedConvertedTagsAsync(DownloadJob job, Stream source,
        string stage, string stageIdentity, CancellationToken ct)
    {
        MediaMergeMetadata? metadata = ConvertedFileMetadata(job);
        bool artworkRequested = !string.IsNullOrWhiteSpace(job.MediaThumbnailUrl);
        MediaThumbnail? cover = null;
        if (artworkRequested)
        {
            // A merge already fetched and checked the cover and stored it in the file being converted. Use that one first.
            cover = job.Kind == JobKind.Merge ? ReadEmbeddedCover(source) : null;
            cover ??= await MediaThumbnail.FetchAsync(job.MediaThumbnailUrl, _http, Options.AllowPrivateNetworks, ct).ConfigureAwait(false);
        }
        bool music = job.Output == MediaOutput.M4a;
        if (metadata is null && cover is null)
            return (music && artworkRequested ? "Saved without artwork because valid source artwork was unavailable." : null,
                stageIdentity);

        if (!TryTagConvertedFile(job, stage, stageIdentity, metadata, cover, ct, out string updatedIdentity))
            return (music ? "The converted file was saved, but its title, artist and artwork could not be embedded." : null,
                stageIdentity);
        return (music && artworkRequested && cover is null
            ? "Track details were saved, but valid source artwork was unavailable."
            : null, updatedIdentity);
    }

    private async Task<(string? Notice, string Identity)> EmbedConvertedMp3TagsAsync(DownloadJob job, Stream source,
        string stage, string stageIdentity, CancellationToken ct)
    {
        MediaMergeMetadata? metadata = ConvertedFileMetadata(job);
        bool artworkRequested = !string.IsNullOrWhiteSpace(job.MediaThumbnailUrl);
        MediaThumbnail? cover = null;
        if (artworkRequested)
        {
            cover = job.Kind == JobKind.Merge ? ReadEmbeddedCover(source) : null;
            cover ??= await MediaThumbnail.FetchAsync(job.MediaThumbnailUrl, _http, Options.AllowPrivateNetworks, ct)
                .ConfigureAwait(false);
        }

        if (metadata is null && cover is null)
            return (artworkRequested ? "Saved without artwork because valid source artwork was unavailable." : null,
                stageIdentity);

        (bool written, string updatedIdentity) = await TryTagConvertedMp3FileAsync(job, stage, stageIdentity,
            metadata ?? new MediaMergeMetadata(), cover, ct).ConfigureAwait(false);
        if (!written)
            return ("The converted MP3 was saved, but its title, artist and artwork could not be embedded.", stageIdentity);

        return (artworkRequested && cover is null
            ? "Track details were saved, but valid source artwork was unavailable."
            : null, updatedIdentity);
    }

    private async Task<(bool Written, string Identity)> TryTagConvertedMp3FileAsync(DownloadJob job,
        string stage, string stageIdentity, MediaMergeMetadata metadata, MediaThumbnail? cover, CancellationToken ct)
    {
        string tagged = ConversionMp3TaggedPath(job);
        string? taggedIdentity = null;
        try
        {
            using (var converted = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 16, FileOptions.SequentialScan))
            {
                PathSafety.RequireTemporaryIdentity(converted.SafeFileHandle, stageIdentity);
                if (!HasRoomFor(tagged, converted.Length + (cover?.Data.Length ?? 0) + (1 << 20)))
                    return (false, stageIdentity);

                using var output = CreateOwnedStage(tagged, FileShare.None, out taggedIdentity);
                if (await Mp3MetadataWriter.TryWriteAsync(converted, output, metadata, cover, ct).ConfigureAwait(false)
                    != Mp3MetadataWriteResult.Written)
                    return (false, stageIdentity);

                output.Flush(flushToDisk: true);
                if (!Mp3MetadataWriter.IsPlayableMpegLayer3(output))
                    throw new MediaContainerException("The tagged MP3 stage did not retain a valid audio frame.");
            }

            ReplaceFile(job, tagged, taggedIdentity, stage, stageIdentity, out string movedIdentity);
            taggedIdentity = null;
            return (true, movedIdentity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MediaContainerException)
        {
            Log(job.Id, "media-metadata", "Tags were not embedded in the converted MP3: " + ex.Message);
            return (false, stageIdentity);
        }
        finally
        {
            if (taggedIdentity is not null) DeleteTemporaryFile(job, tagged, taggedIdentity);
        }
    }

    /// <summary>Merge jobs keep their tags in the plan; a direct download carries them in MediaMetadataJson.</summary>
    private MediaMergeMetadata? ConvertedFileMetadata(DownloadJob job)
    {
        if (job.Kind == JobKind.Merge)
            return MediaMergePlan.TryParse(job.MediaPlanJson, Options.AllowPrivateNetworks, out MediaMergePlan plan, out _)
                ? plan.Metadata : null;
        return MediaMergePlan.TryParseMetadata(job.MediaMetadataJson, out MediaMergeMetadata? metadata) ? metadata : null;
    }

    private static MediaThumbnail? ReadEmbeddedCover(Stream source)
    {
        try
        {
            return WebmDemuxer.LooksLikeMatroska(source, source.Length)
                ? WebmDemuxer.ReadThumbnail(source, source.Length) : Mp4Demuxer.ReadThumbnail(source, source.Length);
        }
        catch (Exception ex) when (ex is MediaContainerException or IOException) { return null; }
    }

    /// <summary>
    /// Stream-copies the converted samples into a new MP4 that also has the tags, re-reads it to prove every
    /// sample, tag and the cover arrived, and only then swaps it in. Nothing is re-encoded.
    /// </summary>
    private bool TryTagConvertedFile(DownloadJob job, string stage, string stageIdentity, MediaMergeMetadata? metadata,
        MediaThumbnail? cover, CancellationToken ct, out string updatedStageIdentity)
    {
        updatedStageIdentity = stageIdentity;
        string tagged = ConversionTaggedPath(job);
        string? taggedIdentity = null;
        try
        {
            using (var converted = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess))
            {
                PathSafety.RequireTemporaryIdentity(converted.SafeFileHandle, stageIdentity);
                if (!HasRoomFor(tagged, converted.Length + (cover?.Data.Length ?? 0) + (1 << 20))) return false;
                List<DemuxedTrack> tracks = Mp4Demuxer.Read(converted, converted.Length, ct);
                var inputs = new List<(DemuxedTrack Track, Stream Source)>();
                foreach (DemuxedTrack track in tracks) inputs.Add((track, converted));

                using var output = CreateOwnedStage(tagged, FileShare.None, out taggedIdentity);
                string container = job.Output == MediaOutput.M4a ? "m4a" : "mp4";
                MergeResult merged = MediaMerger.Merge(inputs, container, output, ct, metadata, cover);
                output.Flush(flushToDisk: true);
                MediaMerger.ValidateRemux(output, output.Length, inputs, ct, merged.Metadata, merged.ThumbnailSha256);
            }
            ReplaceFile(job, tagged, taggedIdentity, stage, stageIdentity, out string movedIdentity);
            updatedStageIdentity = movedIdentity;
            taggedIdentity = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MediaContainerException)
        {
            Log(job.Id, "media-metadata", "Tags were not embedded in the converted file: " + ex.Message);
            return false;
        }
        finally
        {
            if (taggedIdentity is not null) DeleteTemporaryFile(job, tagged, taggedIdentity);
        }
    }

    private void ReplaceFile(DownloadJob job, string source, string? sourceIdentity, string target,
        string? targetIdentity, out string movedIdentity)
    {
        if (sourceIdentity is null || targetIdentity is null)
            throw new IOException("The media stage has no saved file identity.");

        string? folder = Path.GetDirectoryName(target);
        if (folder is null) throw new IOException("The conversion stage folder is invalid.");
        string backup = Path.Combine(folder, $".vesper-rollback-{job.Id:N}-{targetIdentity}");

        try
        {
            MoveOwnedFile(target, backup, targetIdentity);
        }
        catch (Exception protectError) when (protectError is IOException or UnauthorizedAccessException)
        {
            try { MoveOwnedFile(backup, target, targetIdentity); }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
            {
                Log(job.Id, "conversion-stage-rollback", "A previous conversion stage remains at "
                    + PathSafety.ForDisplay(backup) + ": " + rollbackError.Message);
            }
            throw new IOException("The existing conversion stage could not be protected for the swap.", protectError);
        }

        try
        {
            Failpoint("conversion.afterStageBackup");
            MoveOwnedFile(source, target, sourceIdentity);
            movedIdentity = sourceIdentity;
            if (!PathSafety.TryDeleteTemporaryFile(backup, targetIdentity, out string cleanupError))
                Log(job.Id, "conversion-stage-backup", "An old conversion stage remains at "
                    + PathSafety.ForDisplay(backup) + ": " + cleanupError);
        }
        catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
        {
            try
            {
                MoveOwnedFile(backup, target, targetIdentity);
            }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
            {
                Log(job.Id, "conversion-stage-rollback", "An old conversion stage remains at "
                    + PathSafety.ForDisplay(backup) + ": " + rollbackError.Message);
            }

            throw new IOException("The prepared conversion stage could not replace the previous stage.", moveError);
        }
    }

    private static void MoveOwnedFile(string source, string target, string expectedIdentity)
    {
        RequirePathIdentity(source, expectedIdentity);
        if (!PathSafety.TryMoveTemporaryFile(source, target, expectedIdentity, out string error))
            throw new IOException(error);
        RequirePathIdentity(target, expectedIdentity);
    }

    private string? FindPreviousWavRollback(DownloadJob job, string stage)
    {
        string? folder = Path.GetDirectoryName(stage);
        if (folder is null || !PathSafety.TryCheckUnlinkedDirectory(folder, out _)) return folder ?? stage;
        try
        {
            string pattern = $".vesper-rollback-{job.Id:N}-*";
            return Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log(job.Id, "wav-stage-check", ex.Message);
            return folder;
        }
    }

    private bool HasRoomFor(string path, long bytes)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root is null) return true;
        try
        {
            var drive = new DriveInfo(root);
            return !drive.IsReady || drive.AvailableFreeSpace >= bytes + Options.FreeSpaceReserve;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }

    private async Task<(PreparedMediaFile? File, string? Notice)> PrepareAudioMetadataAsync(DownloadJob job, CancellationToken ct)
    {
        if (job.MediaMetadataJson is null) return (null, null);
        string extension = Path.GetExtension(job.FileName).ToLowerInvariant();
        if (job.Output != MediaOutput.Original || !MediaMergePlan.SupportsAudioMetadataFile(job.FileName))
            return (null, "Track details were not embedded because the saved file name no longer matches a supported audio format.");
        if (!MediaMergePlan.TryParseMetadata(job.MediaMetadataJson, out MediaMergeMetadata? metadata) || metadata is null)
            return (null, "Track details could not be embedded; the original audio file was saved unchanged.");

        string stage = AudioMetadataPath(job);
        if (TryReusePreparedAudioMetadata(job, stage) is { } reusable)
            return (reusable, null);

        bool artworkRequested = !string.IsNullOrWhiteSpace(job.MediaThumbnailUrl);
        MediaThumbnail? cover = artworkRequested
            ? await MediaThumbnail.FetchAsync(job.MediaThumbnailUrl, _http, Options.AllowPrivateNetworks, ct).ConfigureAwait(false)
            : null;
        string? artworkNotice = artworkRequested && cover is null
            ? "Track details were saved, but valid source artwork was unavailable."
            : null;

        string? stageIdentity = null;
        bool ready = false;
        try
        {
            if (PathSafety.Inspect(job.PartialPath) is not { Exists: true, IsDirectory: false, IsReparsePoint: false, IsCloudPlaceholder: false })
                return (null, "Track details could not be embedded; the original audio file was saved unchanged.");

            using var source = PartialFile.OpenOwnedRead(job);
            using var output = CreateOwnedStage(stage, FileShare.None, out stageIdentity, FileOptions.SequentialScan);
            AudioMetadataWriteStatus result = await WriteAudioMetadataAsync(extension, source, output, metadata, cover, ct)
                .ConfigureAwait(false);
            if (result != AudioMetadataWriteStatus.Written)
            {
                string notice = result switch
                {
                    AudioMetadataWriteStatus.ExistingTags => "VESPER kept the MP3's existing tags unchanged; newly discovered details and artwork were not applied.",
                    AudioMetadataWriteStatus.NoChanges => "No supported source details were available to embed; the original audio file was saved unchanged.",
                    _ => $"VESPER could not safely add tags to this {extension[1..].ToUpperInvariant()} file; the original was saved unchanged."
                };
                if (result != AudioMetadataWriteStatus.ExistingTags && artworkNotice is not null)
                    notice += " " + artworkNotice;
                return (null, notice);
            }

            output.Flush(flushToDisk: true);
            PathSafety.RequireTemporaryIdentity(output.SafeFileHandle, stageIdentity);
            output.Position = 0;
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(output, ct).ConfigureAwait(false));
            PathSafety.RequireTemporaryIdentity(output.SafeFileHandle, stageIdentity);
            var prepared = new PreparedMediaFile(output.Length, digest, stage, job.FileName,
                stageIdentity ?? throw new IOException("The metadata file has no saved identity."));
            PersistPreparedMedia(job, prepared);
            ready = true;
            return (prepared, artworkNotice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(job.Id, "media-metadata", ex.Message);
            return (null, "Track details could not be embedded; the original audio file was saved unchanged.");
        }
        finally
        {
            if (!ready && stageIdentity is not null) DeleteTemporaryFile(job, stage, stageIdentity);
        }
    }

    private static async Task<AudioMetadataWriteStatus> WriteAudioMetadataAsync(string extension, Stream source,
        Stream destination, MediaMergeMetadata metadata, MediaThumbnail? cover, CancellationToken ct)
    {
        if (extension == ".mp3")
        {
            Mp3MetadataWriteResult result = await Mp3MetadataWriter.TryWriteAsync(source, destination, metadata, cover, ct)
                .ConfigureAwait(false);
            return result switch
            {
                Mp3MetadataWriteResult.Written => AudioMetadataWriteStatus.Written,
                Mp3MetadataWriteResult.ExistingTags => AudioMetadataWriteStatus.ExistingTags,
                Mp3MetadataWriteResult.NoTags => AudioMetadataWriteStatus.NoChanges,
                _ => AudioMetadataWriteStatus.UnsupportedAudio
            };
        }

        if (extension == ".flac")
        {
            FlacMetadataWriteResult result = await FlacMetadataWriter.TryWriteAsync(source, destination, metadata, cover, ct)
                .ConfigureAwait(false);
            return result switch
            {
                FlacMetadataWriteResult.Written => AudioMetadataWriteStatus.Written,
                FlacMetadataWriteResult.NoChanges => AudioMetadataWriteStatus.NoChanges,
                _ => AudioMetadataWriteStatus.UnsupportedAudio
            };
        }

        if (extension is ".ogg" or ".opus")
        {
            OggAudioMetadataWriteResult result = await OggAudioMetadataWriter.TryWriteAsync(source, destination, metadata, cover, ct)
                .ConfigureAwait(false);
            return result switch
            {
                OggAudioMetadataWriteResult.Written => AudioMetadataWriteStatus.Written,
                OggAudioMetadataWriteResult.NoChanges => AudioMetadataWriteStatus.NoChanges,
                _ => AudioMetadataWriteStatus.UnsupportedAudio
            };
        }

        return AudioMetadataWriteStatus.UnsupportedAudio;
    }

    // Use the original MP4 track when it is available, so its decode timestamps and video samples survive audio conversion.
    [SupportedOSPlatform("windows")]
    private DemuxedTrack? TryRemuxWithAac(DownloadJob job, Stream source, DemuxedTrack mergedVideo, string sourceExtension,
        string stage, CancellationToken ct, out string? stageIdentity)
    {
        stageIdentity = null;
        if (mergedVideo.Codec != "h264") return null;
        if (mergedVideo is { SourceContainer: "mp4", Mp4SampleEntry: not null })
        {
            RemuxWithAac(job, source, mergedVideo, sourceExtension, stage, ct, out stageIdentity);
            return mergedVideo;
        }
        if (job.Kind != JobKind.Merge
            || !MediaMergePlan.TryParse(job.MediaPlanJson, Options.AllowPrivateNetworks, out var plan, out _))
            return null;
        int index = Enumerable.Range(0, plan.Tracks.Count).FirstOrDefault(i => plan.Tracks[i] is { Role: "video", Container: "mp4" }, -1);
        if (index < 0) return null;
        string tracksPath = MergeTransfer.TracksPath(job);
        if (PathSafety.Inspect(tracksPath) is not { Exists: true, IsReparsePoint: false, IsCloudPlaceholder: false, IsDirectory: false } found
            || found.Length != plan.TotalBytes)
            return null;

        using var trackFile = new FileStream(tracksPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var videoRegion = new RegionStream(trackFile, plan.OffsetOf(index), plan.Tracks[index].Size);
        List<DemuxedTrack> tracks = Mp4Demuxer.Read(videoRegion, videoRegion.Length, ct);
        if (tracks.Count != 1 || tracks[0] is not { Kind: TrackKind.Video, Codec: "h264" } video
            || video.Width != mergedVideo.Width || video.Height != mergedVideo.Height
            || video.Samples.Count != mergedVideo.Samples.Count || video.PayloadBytes != mergedVideo.PayloadBytes)
            return null;

        RemuxWithAac(job, videoRegion, video, sourceExtension, stage, ct, out stageIdentity);
        return video;
    }

    [SupportedOSPlatform("windows")]
    private void RemuxWithAac(DownloadJob job, Stream videoSource, DemuxedTrack video, string sourceExtension, string stage,
        CancellationToken ct, out string? stageIdentity)
    {
        stageIdentity = null;
        string audioPath = ConversionAudioPath(job);
        string? audioIdentity = null;
        try
        {
            using var audioOwner = WindowsMediaConverter.CreateOwnedOutputStage(audioPath, out audioIdentity);
            WindowsMediaConverter.ConvertOwnedStage(job.PartialPath, sourceExtension, audioPath, MediaOutput.M4a, ct,
                audioOwner, audioIdentity ?? throw new IOException("The audio stage has no saved identity."));
            using var audioFile = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            PathSafety.RequireTemporaryIdentity(audioFile.SafeFileHandle, audioIdentity);
            List<DemuxedTrack> audioTracks = Mp4Demuxer.Read(audioFile, audioFile.Length, ct);
            if (audioTracks.Count != 1 || audioTracks[0] is not { Kind: TrackKind.Audio, Codec: "aac" } audio)
                throw new MediaContainerException("The audio conversion did not produce an AAC track.");
            using var output = CreateOwnedStage(stage, FileShare.None, out stageIdentity);
            var inputs = new List<(DemuxedTrack Track, Stream Source)> { (video, videoSource), (audio, audioFile) };
            MediaMerger.Merge(inputs, "mp4", output, ct);
            MediaMerger.ValidateRemux(output, output.Length, inputs, ct);
        }
        finally
        {
            if (audioIdentity is not null) DeleteTemporaryFile(job, audioPath, audioIdentity);
        }
    }

    private static bool SameDuration(DemuxedTrack source, DemuxedTrack output) =>
        source.DurationSeconds > 0 && output.DurationSeconds > 0
        && Math.Abs(source.DurationSeconds - output.DurationSeconds) <= Math.Clamp(source.DurationSeconds * .01, .05, .25);

    internal static FileStream CreateOwnedStage(string path, FileShare share, out string identity,
        FileOptions options = FileOptions.None, Func<SafeFileHandle, string?>? identityReader = null)
    {
        SafeFileHandle handle = PathSafety.OpenTemporaryHandle(path, FileMode.CreateNew, FileAccess.ReadWrite,
            share, options, allowDelete: true);
        FileStream? file = null;
        try
        {
            identity = (identityReader ?? PathSafety.TemporaryFileIdentity)(handle)
                ?? throw new IOException("The drive did not provide a stable temporary file ID.");
            file = new FileStream(handle, FileAccess.ReadWrite, 1 << 16,
                (options & FileOptions.Asynchronous) != 0);
            return file;
        }
        catch (Exception creationError)
        {
            try
            {
                PathSafety.DeleteOpenTemporaryFile(file?.SafeFileHandle ?? handle, path);
            }
            catch (Exception cleanupError)
            {
                file?.Dispose();
                if (file is null) handle.Dispose();
                throw new IOException("A newly created media stage could not be removed after identity capture failed.",
                    new AggregateException(creationError, cleanupError));
            }

            file?.Dispose();
            if (file is null) handle.Dispose();
            throw;
        }
    }

    private static string CaptureTemporaryFileIdentity(string path)
    {
        using var file = PathSafety.OpenTemporaryHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return PathSafety.TemporaryFileIdentity(file)
            ?? throw new IOException("The drive did not provide a stable temporary file ID.");
    }

    private static void RequirePathIdentity(string path, string expectedIdentity)
    {
        using var file = PathSafety.OpenTemporaryHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        PathSafety.RequireTemporaryIdentity(file, expectedIdentity);
    }

    private bool DeleteTemporaryFile(DownloadJob job, string path, string? expectedIdentity = null)
    {
        if (PathSafety.Inspect(path) is { IsDirectory: true } or { IsReparsePoint: true } or { IsCloudPlaceholder: true })
            return false;
        try
        {
            if (path == job.PartialPath)
            {
                using var file = PathSafety.OpenTemporaryHandle(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, allowDelete: true);
                PathSafety.RequireTemporaryIdentity(file, job.PartialIdentity);
                PathSafety.DeleteOpenTemporaryFile(file, path);
            }
            else if (!PathSafety.TryDeleteTemporaryFile(path, expectedIdentity, out string error))
                throw new IOException(error);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(job.Id, "temporary-file", "Temporary media file could not be removed: " + ex.Message);
            return false;
        }
    }
}
