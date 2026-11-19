using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Vesper.Engine;

public sealed partial class DownloadEngine
{
    private sealed record PreparedMediaFile(long Bytes, string Digest, string Path, string FileName);
    private enum AudioMetadataWriteStatus { Written, NoChanges, ExistingTags, UnsupportedAudio }
    // Only encoders need a single slot. Lossless, file-local MP4/M4A remuxes must not wait behind transcoding.
    private readonly SemaphoreSlim _conversionSlot = new(1);
    private readonly SemaphoreSlim _remuxSlots = new(2, 2); // Two simultaneous file copies, even with many forced jobs.

    private static string OutputExtension(MediaOutput output) => output switch
    { MediaOutput.M4a => ".m4a", MediaOutput.Opus => ".opus", MediaOutput.Wav => ".wav", _ => ".mp4" };
    private static string ConversionPath(DownloadJob job) => job.PartialPath + ".convert" + (job.Output is MediaOutput.Opus or MediaOutput.Wav ? OutputExtension(job.Output) : ".mp4");
    private static string ConversionAudioPath(DownloadJob job) => job.PartialPath + ".convert-audio.mp4";
    private static string ConversionTaggedPath(DownloadJob job) => job.PartialPath + ".convert-tagged.mp4";
    private static string AudioMetadataPath(DownloadJob job) => job.PartialPath + ".tagged" + Path.GetExtension(job.FileName);

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
        bool ready = false;
        bool owned = false;
        bool remuxPermit = false;
        try
        {
            if (!DeleteTemporaryFile(job, stage)) return (file, notice);
            owned = true;
            if (PathSafety.Inspect(job.PartialPath) is not { Exists: true, IsDirectory: false, IsReparsePoint: false, IsCloudPlaceholder: false })
                return (file, notice);

            using var source = PartialFile.OpenOwnedRead(job);
            List<DemuxedTrack> tracks = MediaMerger.Demux(source, source.Length, ct);
            DemuxedTrack? audio = tracks.Find(t => t.Kind == TrackKind.Audio);
            DemuxedTrack? video = tracks.Find(t => t.Kind == TrackKind.Video);
            bool wantVideo = job.Output == MediaOutput.Mp4;
            if (tracks.Count != (wantVideo ? 2 : 1) || audio is null || wantVideo != (video is not null))
                return (file, notice);
            if (job.Output == MediaOutput.Opus)
            {
                var tags = ConvertedFileMetadata(job);
                var cover = ReadEmbeddedCover(source)
                    ?? await MediaThumbnail.FetchAsync(job.MediaThumbnailUrl, _http, Options.AllowPrivateNetworks, ct).ConfigureAwait(false);
                using var opus = new FileStream(stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
                await OpusMuxer.WriteAsync(audio, source, opus, tags, cover, ct).ConfigureAwait(false);
                opus.Flush(flushToDisk: true); opus.Position = 0;
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(opus, ct).ConfigureAwait(false));
                file = new PreparedMediaFile(opus.Length, hash, stage, Path.ChangeExtension(job.FileName, ".opus"));
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

            bool directRemux = Options.ConvertMedia is null && remuxContainer is not null
                && MediaMerger.ChooseContainer(remuxInputs.Select(input => input.Track).ToArray(), remuxContainer) == remuxContainer;
            if (directRemux)
            {
                await _remuxSlots.WaitAsync(ct).ConfigureAwait(false);
                remuxPermit = true;
                using var remuxed = new FileStream(stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                MediaMerger.Merge(remuxInputs, remuxContainer!, remuxed, ct);
                // Disposal flushes buffered bytes for validation. The single durability flush happens
                // after all metadata edits, just before the output is hashed and staged for publication.
            }
            else
            {
                if (audio.Channels is not (1 or 2) || audio.SampleRate is not (44100 or 48000)
                    || video is { Width: < 16 or > 4096 } or { Height: < 16 or > 2304 } or { BitDepth: > 8 })
                    return (file, notice);

                string extension = audio.SourceContainer == "webm" ? ".mkv" : ".mp4";
                double totalSeconds = Math.Max(audio.DurationSeconds, video?.DurationSeconds ?? 0);
                Action<double> progress = seconds =>
                {
                    int percent = totalSeconds > 0 ? (int)Math.Clamp(seconds / totalSeconds * 100, 0, 99) : 0;
                    UpdateJob(job, j => j.Message = $"Converting {percent}% ({FormatClock(seconds)} of {FormatClock(totalSeconds)})");
                };
                if (Options.ConvertMedia is null && !OperatingSystem.IsWindows()) return (file, notice);
                // Native and injected encoders remain serialized (including RemuxWithAac's audio transcode).
                // Hold this gate only for the actual conversion, not source inspection, metadata, hashing,
                // or independent direct-remux jobs.
                await _conversionSlot.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (Options.ConvertMedia is { } convert)
                        convert(job.PartialPath, extension, stage, job.Output, ct);
                    else if (video is not null && TryRemuxWithAac(job, source, video, extension, stage, ct) is { } copied)
                        video = copied;
                    else
                        WindowsMediaConverter.Convert(job.PartialPath, extension, stage, job.Output, ct, video, progress);
                }
                finally { _conversionSlot.Release(); }
            }

            ct.ThrowIfCancellationRequested();
            if (job.Output == MediaOutput.Wav)
            {
                MediaTimeRange? range = null;
                if (job.Kind == JobKind.Merge && MediaMergePlan.TryParse(job.MediaPlanJson, Options.AllowPrivateNetworks, out var merge, out _)) range = merge.OutputRange;
                else if (job.MediaPlanJson is not null && MediaTimeRange.TryParse(job.MediaPlanJson, out var parsed)) range = parsed;
                string waveStage = ConversionTaggedPath(job);
                using (var decoded = File.OpenRead(stage))
                using (var wave = new FileStream(waveStage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
                {
                    var info = WaveAudio.Read(decoded);
                    if (info.Channels != audio.Channels || info.SampleRate != audio.SampleRate
                        || Math.Abs(info.Duration - audio.DurationSeconds) > Math.Max(.25, audio.DurationSeconds * .01))
                        throw new MediaContainerException("The decoded WAV differs from the source timeline.");
                    await WaveAudio.WriteAsync(decoded, wave, range, ConvertedFileMetadata(job), ct).ConfigureAwait(false);
                    wave.Flush(flushToDisk: true);
                }
                ReplaceFile(waveStage, stage);
            }
            else
            {
            using (var converted = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            {
                List<DemuxedTrack> actual = Mp4Demuxer.Read(converted, converted.Length, ct);
                DemuxedTrack? outAudio = actual.Find(t => t.Kind == TrackKind.Audio);
                DemuxedTrack? outVideo = actual.Find(t => t.Kind == TrackKind.Video);
                if (actual.Count != tracks.Count || outAudio is not { Codec: "aac" } || outAudio.Channels != audio.Channels
                    || outAudio.SampleRate != audio.SampleRate || !SameDuration(audio, outAudio)
                    || (video is not null && (outVideo is not { Codec: "h264" } || outVideo.Width != video.Width
                        || outVideo.Height != video.Height || !SameDuration(video, outVideo)
                        || outVideo.Samples.Count != video.Samples.Count)))
                    throw new MediaContainerException("The converted file did not retain the expected audio and video.");
            }

            // The converter only writes audio and video. Title, artist and cover go in now, before the file is hashed.
            notice = await EmbedConvertedTagsAsync(job, source, stage, ct).ConfigureAwait(false);
            }

            using var output = new FileStream(stage, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            output.Flush(flushToDisk: true);
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(output, ct).ConfigureAwait(false));
            ready = true;
            file = new PreparedMediaFile(output.Length, digest, stage,
                Path.ChangeExtension(job.FileName, OutputExtension(job.Output)));
            return (file, notice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or NotSupportedException
            or MediaContainerException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log(job.Id, "conversion-unavailable", ex.Message);
            return (null, null);
        }
        finally
        {
            if (owned && !ready) DeleteTemporaryFile(job, stage);
            if (remuxPermit) _remuxSlots.Release();
            DeleteTemporaryFile(job, ConversionTaggedPath(job));
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
    private async Task<string?> EmbedConvertedTagsAsync(DownloadJob job, Stream source, string stage, CancellationToken ct)
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
            return music && artworkRequested ? "Saved without artwork because valid source artwork was unavailable." : null;

        if (!TryTagConvertedFile(job, stage, metadata, cover, ct))
            return music ? "The converted file was saved, but its title, artist and artwork could not be embedded." : null;
        return music && artworkRequested && cover is null
            ? "Track details were saved, but valid source artwork was unavailable."
            : null;
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
    private bool TryTagConvertedFile(DownloadJob job, string stage, MediaMergeMetadata? metadata, MediaThumbnail? cover,
        CancellationToken ct)
    {
        string tagged = ConversionTaggedPath(job);
        if (!DeleteTemporaryFile(job, tagged)) return false;
        try
        {
            using (var converted = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess))
            {
                if (!HasRoomFor(tagged, converted.Length + (cover?.Data.Length ?? 0) + (1 << 20))) return false;
                List<DemuxedTrack> tracks = Mp4Demuxer.Read(converted, converted.Length, ct);
                var inputs = new List<(DemuxedTrack Track, Stream Source)>();
                foreach (DemuxedTrack track in tracks) inputs.Add((track, converted));

                using var output = new FileStream(tagged, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
                string container = job.Output == MediaOutput.M4a ? "m4a" : "mp4";
                MergeResult merged = MediaMerger.Merge(inputs, container, output, ct, metadata, cover);
                output.Flush(flushToDisk: true);
                MediaMerger.Validate(output, output.Length, tracks, ct, merged.Metadata, merged.ThumbnailSha256);
            }
            // dont touch the converted file until the tagged copy is fully written and checked
            ReplaceFile(tagged, stage);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MediaContainerException)
        {
            Log(job.Id, "media-metadata", "Tags were not embedded in the converted file: " + ex.Message);
            return false;
        }
    }

    // windows antivirus likes to open a brand new file for a moment, so give the swap a few tries
    private static void ReplaceFile(string source, string target)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 4)
            {
                Thread.Sleep(150);
            }
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

        bool artworkRequested = !string.IsNullOrWhiteSpace(job.MediaThumbnailUrl);
        MediaThumbnail? cover = artworkRequested
            ? await MediaThumbnail.FetchAsync(job.MediaThumbnailUrl, _http, Options.AllowPrivateNetworks, ct).ConfigureAwait(false)
            : null;
        string? artworkNotice = artworkRequested && cover is null
            ? "Track details were saved, but valid source artwork was unavailable."
            : null;

        string stage = AudioMetadataPath(job);
        if (!DeleteTemporaryFile(job, stage))
            return (null, "Track details could not be embedded; the original audio file was saved unchanged.");
        bool owned = false;
        bool ready = false;
        try
        {
            if (PathSafety.Inspect(job.PartialPath) is not { Exists: true, IsDirectory: false, IsReparsePoint: false, IsCloudPlaceholder: false })
                return (null, "Track details could not be embedded; the original audio file was saved unchanged.");

            using var source = PartialFile.OpenOwnedRead(job);
            using var output = new FileStream(stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                FileOptions.SequentialScan);
            owned = true;
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
            output.Position = 0;
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(output, ct).ConfigureAwait(false));
            ready = true;
            return (new PreparedMediaFile(output.Length, digest, stage, job.FileName), artworkNotice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(job.Id, "media-metadata", ex.Message);
            return (null, "Track details could not be embedded; the original audio file was saved unchanged.");
        }
        finally
        {
            if (owned && !ready) DeleteTemporaryFile(job, stage);
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
        string stage, CancellationToken ct)
    {
        if (mergedVideo.Codec != "h264") return null;
        if (mergedVideo is { SourceContainer: "mp4", Mp4SampleEntry: not null })
        {
            RemuxWithAac(job, source, mergedVideo, sourceExtension, stage, ct);
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

        RemuxWithAac(job, videoRegion, video, sourceExtension, stage, ct);
        return video;
    }

    [SupportedOSPlatform("windows")]
    private void RemuxWithAac(DownloadJob job, Stream videoSource, DemuxedTrack video, string sourceExtension, string stage,
        CancellationToken ct)
    {
        string audioPath = ConversionAudioPath(job);
        if (!DeleteTemporaryFile(job, audioPath)) throw new IOException("A previous conversion could not be cleared.");
        try
        {
            WindowsMediaConverter.Convert(job.PartialPath, sourceExtension, audioPath, MediaOutput.M4a, ct);
            using var audioFile = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            List<DemuxedTrack> audioTracks = Mp4Demuxer.Read(audioFile, audioFile.Length, ct);
            if (audioTracks.Count != 1 || audioTracks[0] is not { Kind: TrackKind.Audio, Codec: "aac" } audio)
                throw new MediaContainerException("The audio conversion did not produce an AAC track.");
            using var output = new FileStream(stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            MediaMerger.Merge([(video, videoSource), (audio, audioFile)], "mp4", output, ct);
        }
        finally { DeleteTemporaryFile(job, audioPath); }
    }

    private static bool SameDuration(DemuxedTrack source, DemuxedTrack output) =>
        source.DurationSeconds > 0 && output.DurationSeconds > 0
        && Math.Abs(source.DurationSeconds - output.DurationSeconds) <= Math.Clamp(source.DurationSeconds * .01, .05, .25);

    private bool DeleteTemporaryFile(DownloadJob job, string path)
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
            else if (!PathSafety.TryDeleteTemporaryFile(path, out string error))
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
