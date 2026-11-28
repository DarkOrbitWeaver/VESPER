using System.Buffers.Binary;
using System.Text;
using Vesper.Shared;

namespace Vesper.Engine;

/// <summary>
/// Bounded, seek-based track-presence guard for plain HTTP media handoffs. Prevents an observed adaptive video URL
/// being published as an audio/video download just because its MIME type is video/mp4 or video/webm.
/// This checks container layout/track declarations, not full codec decodability. No media payload is buffered.
/// The MP4 check reads box headers straight from the file and skips the sample tables, so a long video with
/// many megabytes of movie metadata costs the same few small reads as a short one.
/// </summary>
internal static class MediaTrackProbe
{
    private readonly record struct IsoBox(string Type, int Payload, int End);
    private readonly record struct FileBox(string Type, long Payload, long End);
    private const int MaxSampleDescriptionBytes = 1024 * 1024;
    private const int MaxBoxesPerLevel = 10_000;
    private readonly record struct Mp4ProbeResult(bool HasAudio, bool HasVideo, bool HasFragments, bool HasMovieExtends);

    internal static void RequireAudio(PartialFile file, string format, CancellationToken ct,
        bool allowSilentProgressiveMp4 = false, string? expectedIdentity = null)
    {
        if (format == "webm")
        {
            if (WebmHasAudio(file, ct, out bool finiteSegment)) return;
            if (!allowSilentProgressiveMp4) throw MissingAudio();
            if (!finiteSegment) throw Bad("The WebM has no complete finite segment extent.");
            RequireSilentWebmSamples(file, expectedIdentity, ct);
            return;
        }

        Mp4ProbeResult result = ProbeMp4(file, ct);
        if (result.HasAudio) return;
        if (!allowSilentProgressiveMp4) throw MissingAudio();
        if (!result.HasVideo || result.HasFragments || result.HasMovieExtends)
            throw Bad("Only a complete progressive MP4 video can be saved without audio.");

        RequireVideoSamples(file, expectedIdentity, ct);
    }

    internal static void RequireMp4Media(PartialFile file, CancellationToken ct)
    {
        var tracks = ProbeMp4(file, ct);
        if (!tracks.HasVideo && !tracks.HasAudio) throw Bad("The MP4 has no playable audio or video track.");
    }

    private static Mp4ProbeResult ProbeMp4(PartialFile file, CancellationToken ct)
    {
        long offset = 0;
        FileBox? moov = null;
        bool media = false;
        bool fragment = false;
        for (int count = 0; offset < file.Length; count++)
        {
            ct.ThrowIfCancellationRequested();
            if (count >= 100_000 || file.Length - offset < 8) throw Bad("Invalid MP4 top-level structure.");
            var header = Read(file, offset, (int)Math.Min(16, file.Length - offset));
            if (Mp4TopLevelExtent.ReadBox(header, file.Length - offset, out int prefix, out long size)
                != Mp4TopLevelExtent.BoxStatus.Known) throw Bad("An MP4 box exceeds the downloaded file.");
            string type = Encoding.ASCII.GetString(header, 4, 4);
            if (type == "moov")
            {
                if (moov is not null) throw Bad("The MP4 has more than one set of movie metadata.");
                moov = new FileBox(type, offset + prefix, offset + size);
            }
            if (type == "mdat" && size > prefix) media = true;
            if (type == "moof") fragment = true;
            if (type == "pssh") throw Bad("Protected MP4 initialization is unsupported.");
            offset += size;
        }
        if (moov is null && media && fragment)
            throw Bad("The site sent only part of the video. Open the original page and choose another format.");
        if (moov is null || !media) throw Bad("The MP4 file has no movie metadata or media payload.");
        var movie = FileBoxes(file, moov.Value.Payload, moov.Value.End, ct);
        if (movie.Any(b => b.Type == "pssh")) throw Bad("Protected MP4 movie metadata is unsupported.");
        bool audio = false;
        bool video = false;
        foreach (var trak in movie.Where(b => b.Type == "trak"))
        {
            var mdia = One(FileBoxes(file, trak.Payload, trak.End, ct), "mdia");
            var md = FileBoxes(file, mdia.Payload, mdia.End, ct);
            var handler = One(md, "hdlr");
            if (handler.End - handler.Payload < 12) throw Bad("Truncated MP4 track handler.");
            string kind = Encoding.ASCII.GetString(Read(file, handler.Payload + 8, 4));
            if (kind is not ("soun" or "vide")) continue;
            var minf = One(md, "minf");
            var stbl = One(FileBoxes(file, minf.Payload, minf.End, ct), "stbl");
            // Only the sample descriptions are read. The tables beside them grow with the video's length.
            var stsd = One(FileBoxes(file, stbl.Payload, stbl.End, ct), "stsd");
            long descriptionBytes = stsd.End - stsd.Payload;
            if (descriptionBytes < 8) throw Bad("Truncated MP4 sample descriptions.");
            if (descriptionBytes > MaxSampleDescriptionBytes) throw Bad("Oversized MP4 sample descriptions.");
            byte[] descriptions = Read(file, stsd.Payload, (int)descriptionBytes);
            var entries = IsoBoxes(descriptions, 8, descriptions.Length);
            if (entries.Count == 0 || entries.Count > 16 || BinaryPrimitives.ReadUInt32BigEndian(descriptions.AsSpan(4)) != entries.Count)
                throw Bad("Invalid MP4 sample-description count.");
            if (entries.Any(b => b.Type is "enca" or "encv")) throw Bad("Encrypted MP4 samples are unsupported.");
            if (kind == "soun") audio = true;
            if (kind == "vide") video = true;
        }
        return new Mp4ProbeResult(audio, video, fragment, movie.Any(b => b.Type == "mvex"));
    }

    private static void RequireVideoSamples(PartialFile file, string? expectedIdentity, CancellationToken ct)
    {
        PathSafety.RequireTemporaryIdentity(file.Handle, expectedIdentity);
        long expectedLength = file.Length;
        using var source = PathSafety.OpenTemporaryStream(file.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
        PathSafety.RequireTemporaryIdentity(source.SafeFileHandle, expectedIdentity);
        if (source.Length != expectedLength) throw Bad("The MP4 changed during media validation.");

        List<DemuxedTrack> tracks = Mp4Demuxer.Read(source, source.Length, ct);
        PathSafety.RequireTemporaryIdentity(file.Handle, expectedIdentity);
        PathSafety.RequireTemporaryIdentity(source.SafeFileHandle, expectedIdentity);
        if (!tracks.Any(track => track.Kind == TrackKind.Video && track.Samples.Count > 0)
            || tracks.Any(track => track.Kind == TrackKind.Audio && track.Samples.Count > 0))
            throw Bad("The MP4 does not contain complete video-only samples.");
    }

    internal static void RequireSilentWebmHeader(PartialFile file, CancellationToken ct)
    {
        if (WebmHasAudio(file, ct, out bool finite) || !finite)
            throw Bad("The selected clip is not a finite silent WebM.");
    }

    private static void RequireSilentWebmSamples(PartialFile file, string? expectedIdentity, CancellationToken ct)
    {
        PathSafety.RequireTemporaryIdentity(file.Handle, expectedIdentity);
        using var source = PathSafety.OpenTemporaryStream(file.Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
        PathSafety.RequireTemporaryIdentity(source.SafeFileHandle, expectedIdentity);
        if (source.Length != file.Length) throw Bad("The WebM changed during validation.");
        List<DemuxedTrack> tracks = WebmDemuxer.Read(source, source.Length, ct);
        PathSafety.RequireTemporaryIdentity(source.SafeFileHandle, expectedIdentity);
        if (tracks.Count != 1 || tracks[0] is not { Kind: TrackKind.Video, Codec: "vp8" or "vp9" or "av1" }
            || tracks[0].Samples.Count == 0 || !tracks[0].Samples[0].Key)
            throw Bad("The WebM does not contain a supported standalone video.");
    }

    private static bool WebmHasAudio(PartialFile file, CancellationToken ct, out bool finiteSegment)
    {
        long position = 0;
        var ebml = FileElement(file, ref position, file.Length);
        if (ebml.Id != WebmDemuxer.IdEbml) throw Bad("Missing EBML header.");
        position = ebml.End;
        var segment = FileElement(file, ref position, file.Length);
        if (segment.Id != WebmDemuxer.IdSegment) throw Bad("Missing WebM segment.");
        finiteSegment = !segment.UnknownSize && segment.End == file.Length;
        for (int count = 0; position < segment.End && count < 100_000; count++)
        {
            ct.ThrowIfCancellationRequested();
            var element = FileElement(file, ref position, segment.End);
            if (element.Id == WebmDemuxer.IdTracks)
                return ReadWebmTracks(file, position, element, requirePlayable: false).Audio;
            position = element.End;
        }
        throw Bad("No bounded WebM track table was found.");
    }

    /// <summary>
    /// Validates an explicitly requested complete browser WebM copy without
    /// reading its media payload into memory or allocating a sample index.
    /// This is a container/track/payload-presence check, not codec decoding.
    /// </summary>
    internal static void RequireWebmMedia(PartialFile file, CancellationToken ct)
    {
        long position = 0;
        var ebml = FileElement(file, ref position, file.Length);
        if (ebml.Id != WebmDemuxer.IdEbml || ebml.UnknownSize || ebml.End - position > 4096)
            throw Bad("Missing or oversized EBML header.");
        byte[] header = Read(file, position, (int)(ebml.End - position));
        string? docType = null;
        for (int i = 0; i < header.Length;)
        {
            var field = MemoryElement(header, ref i, header.Length);
            if (field.Id == 0x4282 && field.End - i <= 32)
                docType = Encoding.ASCII.GetString(header, i, (int)field.End - i);
            i = (int)field.End;
        }
        if (docType is not ("webm" or "matroska")) throw Bad("Unsupported EBML document type.");
        position = ebml.End;
        var segment = FileElement(file, ref position, file.Length);
        if (segment.Id != WebmDemuxer.IdSegment || segment.End != file.Length)
            throw Bad("Missing or incomplete WebM segment.");
        bool tracks = false, media = false;
        int remaining = 100_000;
        while (position < segment.End)
        {
            ct.ThrowIfCancellationRequested();
            if (--remaining < 0) throw Bad("Too many WebM elements.");
            var element = FileElement(file, ref position, segment.End);
            if (element.Id == WebmDemuxer.IdCluster)
            {
                if (!tracks) throw Bad("A WebM cluster precedes the track list.");
                media |= WebmClusterHasBlock(file, ref position, element, ref remaining, ct);
                continue;
            }
            if (element.UnknownSize) throw Bad("Unsupported unknown-size WebM element.");
            if (element.Id == WebmDemuxer.IdTracks)
            {
                if (tracks) throw Bad("Duplicate WebM track list.");
                var facts = ReadWebmTracks(file, position, element, requirePlayable: true);
                tracks = facts.Audio || facts.Video;
                if (!tracks) throw Bad("The WebM has no playable audio or video track.");
            }
            position = element.End;
        }
        if (!tracks || !media) throw Bad("The WebM has no complete media block.");
    }

    private static (bool Audio, bool Video) ReadWebmTracks(PartialFile file, long position, Element element, bool requirePlayable)
    {
        if (element.UnknownSize || element.End - position > 4 * 1024 * 1024)
            throw Bad("Oversized or unbounded WebM track metadata.");
        byte[] tracks = Read(file, position, (int)(element.End - position));
        int p = 0; bool audio = false, video = false;
        while (p < tracks.Length)
        {
            var track = MemoryElement(tracks, ref p, tracks.Length);
            if (track.Id == 0xae)
            {
                int q = p; ulong? kind = null; string? codec = null;
                while (q < track.End)
                {
                    var property = MemoryElement(tracks, ref q, (int)track.End);
                    if (property.Id == 0x6d80) throw Bad("Encoded/encrypted WebM tracks are unsupported.");
                    if (property.Id == 0x83)
                    {
                        if (property.End - q is < 1 or > 8) throw Bad("Invalid WebM track type.");
                        ulong value = 0; for (int i = q; i < property.End; i++) value = value * 256 + tracks[i]; kind = value;
                    }
                    if (property.Id == 0x86 && property.End - q <= 128) codec = Encoding.ASCII.GetString(tracks, q, (int)property.End - q);
                    q = (int)property.End;
                }
                if (kind == 2)
                {
                    if (codec is not ("A_OPUS" or "A_VORBIS" or "A_AAC" or "A_MPEG/L3" or "A_FLAC"))
                        throw Bad("The WebM declares an unsupported audio track.");
                    audio = true;
                }
                if (kind == 1)
                {
                    if (requirePlayable && codec is not ("V_VP8" or "V_VP9" or "V_AV1" or "V_MPEG4/ISO/AVC" or "V_MPEGH/ISO/HEVC"))
                        throw Bad("The WebM declares an unsupported video track.");
                    video = true;
                }
            }
            p = (int)track.End;
        }
        return (audio, video);
    }

    private static bool WebmClusterHasBlock(PartialFile file, ref long position, Element cluster, ref int remaining, CancellationToken ct)
    {
        bool media = false;
        while (position < cluster.End)
        {
            ct.ThrowIfCancellationRequested();
            if (--remaining < 0) throw Bad("Too many WebM elements.");
            long start = position;
            var child = FileElement(file, ref position, cluster.End);
            if (cluster.UnknownSize && child.Id is WebmDemuxer.IdCluster or WebmDemuxer.IdCues or WebmDemuxer.IdTags
                or WebmDemuxer.IdChapters or WebmDemuxer.IdAttachments or WebmDemuxer.IdSeekHead or WebmDemuxer.IdInfo or WebmDemuxer.IdTracks)
            { position = start; return media; }
            if (child.UnknownSize) throw Bad("Unknown-size WebM cluster child.");
            if (child.Id == 0xa3 && child.End - position >= 5) media = true;
            if (child.Id == 0xa0)
            {
                long groupPosition = position;
                bool block = false;
                while (groupPosition < child.End)
                {
                    ct.ThrowIfCancellationRequested();
                    if (--remaining < 0) throw Bad("Too many WebM elements.");
                    var member = FileElement(file, ref groupPosition, child.End);
                    if (member.UnknownSize) throw Bad("Unknown-size WebM block group child.");
                    if (member.Id == 0xa1 && member.End - groupPosition >= 5) block = true;
                    groupPosition = member.End;
                }
                if (!block) throw Bad("Missing WebM block.");
                media = true;
            }
            position = child.End;
            // A finite cluster's declared extent is already checked. Skip
            // its remaining blocks rather than indexing a long movie.
            if (media && !cluster.UnknownSize) { position = cluster.End; break; }
        }
        return media;
    }

    private readonly record struct Element(ulong Id, long End, bool UnknownSize = false);
    private static Element FileElement(PartialFile file, ref long position, long parentEnd)
    {
        if (parentEnd - position < 2) throw Bad("Truncated EBML element.");
        var header = Read(file, position, (int)Math.Min(16, parentEnd - position));
        int p = 0; ulong id = Vint(header, ref p, true, out _); ulong length = Vint(header, ref p, false, out bool unknown);
        position += p;
        if (!unknown && length > (ulong)(parentEnd - position)) throw Bad("EBML element exceeds its container.");
        return new(id, unknown ? parentEnd : position + (long)length, unknown);
    }
    private static Element MemoryElement(byte[] bytes, ref int p, int end)
    {
        ulong id = Vint(bytes.AsSpan(0, end), ref p, true, out _), length = Vint(bytes.AsSpan(0, end), ref p, false, out bool unknown);
        if (unknown || length > (ulong)(end - p)) throw Bad("Unsupported or truncated EBML metadata.");
        return new(id, p + (long)length);
    }
    private static ulong Vint(ReadOnlySpan<byte> bytes, ref int p, bool identifier, out bool unknown)
    {
        if (p >= bytes.Length) throw Bad("Truncated EBML integer.");
        byte first = bytes[p++]; int length = 1; byte marker = 0x80;
        while (marker != 0 && (first & marker) == 0) { marker >>= 1; length++; }
        if (marker == 0 || length > (identifier ? 4 : 8) || bytes.Length - p < length - 1) throw Bad("Invalid EBML integer.");
        ulong value = identifier ? first : (ulong)(first & (marker - 1));
        for (int i = 1; i < length; i++) value = value * 256 + bytes[p++];
        unknown = !identifier && value == ((1UL << (length * 7)) - 1);
        return value;
    }
    private static List<IsoBox> IsoBoxes(byte[] bytes, int start, int end)
    {
        var boxes = new List<IsoBox>();
        while (start < end)
        {
            if (end - start < 8 || boxes.Count >= 100_000) throw Bad("Truncated MP4 metadata.");
            uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start));
            if (size < 8 || size > end - start) throw Bad("Invalid MP4 metadata box size.");
            boxes.Add(new(Encoding.ASCII.GetString(bytes, start + 4, 4), start + 8, start + (int)size)); start += (int)size;
        }
        return boxes;
    }
    /// <summary>Lists the boxes between two file offsets. Reads each box's header and nothing else.</summary>
    private static List<FileBox> FileBoxes(PartialFile file, long start, long end, CancellationToken ct)
    {
        var boxes = new List<FileBox>();
        while (start < end)
        {
            ct.ThrowIfCancellationRequested();
            if (end - start < 8 || boxes.Count >= MaxBoxesPerLevel) throw Bad("Truncated MP4 metadata.");
            byte[] header = Read(file, start, (int)Math.Min(16, end - start));
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            int prefix = 8;
            if (size == 1)
            {
                if (header.Length < 16) throw Bad("Truncated MP4 metadata.");
                size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                prefix = 16;
            }
            else if (size == 0) size = (ulong)(end - start);
            if (size < (ulong)prefix || size > (ulong)(end - start)) throw Bad("Invalid MP4 metadata box size.");
            boxes.Add(new(Encoding.ASCII.GetString(header, 4, 4), start + prefix, start + (long)size));
            start += (long)size;
        }
        return boxes;
    }
    private static FileBox One(List<FileBox> boxes, string type)
    {
        var matches = boxes.Where(b => b.Type == type).ToArray();
        return matches.Length == 1 ? matches[0] : throw Bad($"Missing or duplicate MP4 {type} box.");
    }
    private static byte[] Read(PartialFile file, long offset, int size)
    {
        var bytes = new byte[size]; int done = 0;
        while (done < size)
        {
            int count = file.Read(bytes.AsSpan(done), offset + done);
            if (count <= 0) throw Bad("The file ended during media track validation.");
            done += count;
        }
        return bytes;
    }
    private static MediaContainerException Bad(string text) => new(text);

    private static MediaContainerException MissingAudio() =>
        Bad("The downloaded representation is video-only or has no supported audio track. No silent output was published; choose another format.");
}
