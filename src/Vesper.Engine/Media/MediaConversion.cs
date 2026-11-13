using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Vesper.Engine;

public sealed partial class DownloadEngine
{
    private sealed record PreparedMediaFile(long Bytes, string Digest, string Path, string FileName);
    private enum AudioMetadataWriteStatus { Written, NoChanges, ExistingTags, UnsupportedAudio }
    private readonly SemaphoreSlim _conversionSlot = new(1);

    private static string OutputExtension(MediaOutput output) => output == MediaOutput.M4a ? ".m4a" : ".mp4";
    private static string ConversionPath(DownloadJob job) => job.PartialPath + ".convert.mp4";
    private static string ConversionAudioPath(DownloadJob job) => job.PartialPath + ".convert-audio.mp4";
    private static string AudioMetadataPath(DownloadJob job) => job.PartialPath + ".tagged" + Path.GetExtension(job.FileName);

    internal static string PublicationStagePath(DownloadJob job) =>
        job.Output == MediaOutput.Original && job.MediaMetadataJson is not null
            ? AudioMetadataPath(job) : ConversionPath(job);

    private async Task<PreparedMediaFile?> ConvertMediaAsync(DownloadJob job, CancellationToken ct)
    {
        if (job.ExpectedSha256 is not null) return null;
        UpdateJob(job, j => { j.State = JobState.Converting; j.Reason = ""; j.Message = "Preparing a file for more devices."; });
        await _conversionSlot.WaitAsync(ct).ConfigureAwait(false);
        string stage = ConversionPath(job);
        bool ready = false;
        bool owned = false;
        try
        {
            if (!DeleteTemporaryFile(job, stage)) return null;
            owned = true;
            if (PathSafety.Inspect(job.PartialPath) is not { Exists: true, IsDirectory: false, IsReparsePoint: false, IsCloudPlaceholder: false })
                return null;

            using var source = new FileStream(job.PartialPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            List<DemuxedTrack> tracks = MediaMerger.Demux(source, source.Length, ct);
            DemuxedTrack? audio = tracks.Find(t => t.Kind == TrackKind.Audio);
            DemuxedTrack? video = tracks.Find(t => t.Kind == TrackKind.Video);
            bool wantVideo = job.Output == MediaOutput.Mp4;
            if (tracks.Count != (wantVideo ? 2 : 1) || audio is null || wantVideo != (video is not null))
                return null;
            if (audio.Channels is not (1 or 2) || audio.SampleRate is not (44100 or 48000)
                || video is { Width: < 16 or > 4096 } or { Height: < 16 or > 2304 } or { BitDepth: > 8 })
                return null;

            string extension = audio.SourceContainer == "webm" ? ".mkv" : ".mp4";
            if (Options.ConvertMedia is { } convert) convert(job.PartialPath, extension, stage, job.Output, ct);
            else if (OperatingSystem.IsWindows())
            {
                if (video is not null && TryRemuxWithAac(job, source, video, extension, stage, ct) is { } copied)
                    video = copied;
                else WindowsMediaConverter.Convert(job.PartialPath, extension, stage, job.Output, ct, video);
            }
            else return null;

            ct.ThrowIfCancellationRequested();
            using var output = new FileStream(stage, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            List<DemuxedTrack> actual = Mp4Demuxer.Read(output, output.Length, ct);
            DemuxedTrack? outAudio = actual.Find(t => t.Kind == TrackKind.Audio);
            DemuxedTrack? outVideo = actual.Find(t => t.Kind == TrackKind.Video);
            if (actual.Count != tracks.Count || outAudio is not { Codec: "aac" } || outAudio.Channels != audio.Channels
                || outAudio.SampleRate != audio.SampleRate || !SameDuration(audio, outAudio)
                || (video is not null && (outVideo is not { Codec: "h264" } || outVideo.Width != video.Width
                    || outVideo.Height != video.Height || !SameDuration(video, outVideo)
                    || outVideo.Samples.Count != video.Samples.Count)))
                throw new MediaContainerException("The converted file did not retain the expected audio and video.");

            output.Flush(flushToDisk: true);
            output.Position = 0;
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(output, ct).ConfigureAwait(false));
            ready = true;
            return new PreparedMediaFile(output.Length, digest, stage,
                Path.ChangeExtension(job.FileName, OutputExtension(job.Output)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or NotSupportedException
            or MediaContainerException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log(job.Id, "conversion-unavailable", ex.Message);
            return null;
        }
        finally
        {
            if (owned && !ready) DeleteTemporaryFile(job, stage);
            _conversionSlot.Release();
        }
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

            using var source = new FileStream(job.PartialPath, FileMode.Open, FileAccess.Read, FileShare.Read);
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

    private static bool MatchesConversion(DownloadJob job, string path)
    {
        if (job.PendingConversionBytes is not > 0 || job.PendingConversionDigest is null
            || !PathSafety.IsInside(job.DestinationFolder, path)
            || PathSafety.Inspect(path) is not { Exists: true, IsReparsePoint: false, IsCloudPlaceholder: false, IsDirectory: false } found
            || found.Length != job.PendingConversionBytes)
            return false;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(file)) == job.PendingConversionDigest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private bool DeleteTemporaryFile(DownloadJob job, string path)
    {
        if (PathSafety.Inspect(path) is { IsDirectory: true } or { IsReparsePoint: true } or { IsCloudPlaceholder: true })
            return false;
        try { File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(job.Id, "temporary-file", "Temporary media file could not be removed: " + ex.Message);
            return false;
        }
    }
}
