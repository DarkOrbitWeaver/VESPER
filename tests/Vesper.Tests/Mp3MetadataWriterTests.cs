using System.Text;
using Vesper.Engine;

namespace Vesper.Tests;

[Collection("Process handle checks")]
public sealed class Mp3MetadataWriterTests
{
    private static byte[] Fixture => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", "quiet-track.mp3"));
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");

    [Fact]
    public async Task Id3v24EmbedsSourceTagsAndCoverWithoutChangingMp3Bytes()
    {
        byte[] mp3 = Fixture;
        byte[] cover = TinyPng();
        var metadata = new MediaMergeMetadata
        {
            Title = "Song One", Artist = "Fixture Band", Album = "Fixture Album", AlbumArtist = "Fixture Band",
            TrackNumber = 2, DiscNumber = 1, ReleaseDate = "2024-03-12"
        };
        MediaThumbnail thumbnail = Assert.IsType<MediaThumbnail>(MediaThumbnail.TryCreate(cover));
        using var source = new MemoryStream(mp3);
        using var output = new MemoryStream();

        Mp3MetadataWriteResult result = await Mp3MetadataWriter.TryWriteAsync(source, output, metadata, thumbnail, default);

        Assert.Equal(Mp3MetadataWriteResult.Written, result);
        byte[] tagged = output.ToArray();
        Assert.Equal("ID3", Encoding.ASCII.GetString(tagged, 0, 3));
        Assert.Equal(4, tagged[3]);
        int tagLength = ReadSynchsafe(tagged.AsSpan(6, 4));
        Assert.True(tagLength > 0 && tagLength <= 1_000_000);
        Dictionary<string, byte[]> frames = ReadFrames(tagged.AsSpan(10, tagLength));

        Assert.Equal("Song One", ReadText(frames["TIT2"]));
        Assert.Equal("Fixture Band", ReadText(frames["TPE1"]));
        Assert.Equal("Fixture Album", ReadText(frames["TALB"]));
        Assert.Equal("Fixture Band", ReadText(frames["TPE2"]));
        Assert.Equal("2", ReadText(frames["TRCK"]));
        Assert.Equal("1", ReadText(frames["TPOS"]));
        Assert.Equal("2024-03-12", ReadText(frames["TDRC"]));
        Assert.Equal(cover, ReadPicture(frames["APIC"]));
        Assert.Equal(mp3, tagged.AsSpan(10 + tagLength).ToArray());

        string? qaPath = Environment.GetEnvironmentVariable("VESPER_MEDIA_QA_MP3");
        if (!string.IsNullOrWhiteSpace(qaPath))
        {
            string fullPath = Path.GetFullPath(qaPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, tagged);
        }
    }

    [Fact]
    public async Task ExistingId3HeaderIsMergedWithoutDuplicatingTheTag()
    {
        byte[] mp3 = Fixture;
        byte[] existing = [.. BuildTag(), .. mp3];
        using var source = new MemoryStream(existing);
        using var output = new MemoryStream();

        Mp3MetadataWriteResult result = await Mp3MetadataWriter.TryWriteAsync(source, output,
            new MediaMergeMetadata { Title = "Song One" }, null, default);

        Assert.Equal(Mp3MetadataWriteResult.Written, result);
        byte[] tagged = output.ToArray();
        int tagLength = ReadSynchsafe(tagged.AsSpan(6, 4));
        Dictionary<string, byte[]> frames = ReadFrames(tagged.AsSpan(10, tagLength));
        Assert.Equal("Song One", ReadText(frames["TIT2"]));
        Assert.Equal(mp3, tagged.AsSpan(10 + tagLength).ToArray());
        Assert.Equal(existing, source.ToArray());
    }

    [Fact]
    public async Task ExistingId3v24FieldsAndArtworkAreMergedWithoutLosingOtherFrames()
    {
        byte[] mp3 = Fixture;
        byte[] oldFrontCover = [1, 2, 3, 4];
        byte[] oldBackCover = [5, 6, 7, 8];
        byte[] newCover = TinyPng();
        byte[] unknownFrame = BuildFrame("TXXX", [3, .. Encoding.UTF8.GetBytes("private value")]);
        byte[] existingTag = BuildTag(
            BuildTextFrame("TIT2", "Old Title"),
            BuildTextFrame("TALB", "Existing Album"),
            unknownFrame,
            BuildPictureFrame(oldFrontCover, 3),
            BuildPictureFrame(oldBackCover, 4));
        byte[] original = [.. existingTag, .. mp3];
        using var source = new MemoryStream(original);
        using var output = new MemoryStream();
        MediaThumbnail thumbnail = Assert.IsType<MediaThumbnail>(MediaThumbnail.TryCreate(newCover));

        Mp3MetadataWriteResult result = await Mp3MetadataWriter.TryWriteAsync(source, output,
            new MediaMergeMetadata { Title = "New Title", Artist = "New Artist" }, thumbnail, default);

        Assert.Equal(Mp3MetadataWriteResult.Written, result);
        byte[] tagged = output.ToArray();
        int tagLength = ReadSynchsafe(tagged.AsSpan(6, 4));
        List<(string Id, byte[] Payload, byte[] Encoded)> frames = ReadFrameList(tagged.AsSpan(10, tagLength));
        Assert.Equal("New Title", ReadText(Assert.Single(frames, frame => frame.Id == "TIT2").Payload));
        Assert.Equal("New Artist", ReadText(Assert.Single(frames, frame => frame.Id == "TPE1").Payload));
        Assert.Equal("Existing Album", ReadText(Assert.Single(frames, frame => frame.Id == "TALB").Payload));
        Assert.Equal(unknownFrame, Assert.Single(frames, frame => frame.Id == "TXXX").Encoded);
        var pictures = frames.Where(frame => frame.Id == "APIC").ToArray();
        Assert.Equal(2, pictures.Length);
        Assert.Equal(newCover, ReadPicture(pictures.Single(frame => PictureType(frame.Payload) == 3).Payload));
        Assert.Equal(oldBackCover, ReadPicture(pictures.Single(frame => PictureType(frame.Payload) == 4).Payload, 4));
        Assert.Equal(mp3, tagged.AsSpan(10 + tagLength).ToArray());
        Assert.Equal(original, source.ToArray());
    }

    [Fact]
    public async Task ExistingArtworkStaysWhenTheSourceHasNoValidCover()
    {
        byte[] mp3 = Fixture;
        byte[] oldFrontCover = [1, 2, 3, 4];
        byte[] existingFrontFrame = BuildPictureFrame(oldFrontCover, 3);
        byte[] existingTag = BuildTag(existingFrontFrame);
        using var source = new MemoryStream([.. existingTag, .. mp3]);
        using var output = new MemoryStream();

        Mp3MetadataWriteResult result = await Mp3MetadataWriter.TryWriteAsync(source, output,
            new MediaMergeMetadata { Title = "Updated Title" }, null, default);

        Assert.Equal(Mp3MetadataWriteResult.Written, result);
        byte[] tagged = output.ToArray();
        int tagLength = ReadSynchsafe(tagged.AsSpan(6, 4));
        List<(string Id, byte[] Payload, byte[] Encoded)> frames = ReadFrameList(tagged.AsSpan(10, tagLength));
        Assert.Equal("Updated Title", ReadText(Assert.Single(frames, frame => frame.Id == "TIT2").Payload));
        Assert.Equal(existingFrontFrame, Assert.Single(frames, frame => frame.Id == "APIC").Encoded);
        Assert.Equal(mp3, tagged.AsSpan(10 + tagLength).ToArray());
    }

    [Fact]
    public async Task AmbiguousExistingCoverKeepsTheOriginalFileUnchanged()
    {
        byte[] mp3 = Fixture;
        byte[] existingTag = BuildTag(BuildPictureFrame([], 3));
        byte[] sourceBytes = [.. existingTag, .. mp3];
        using var source = new MemoryStream(sourceBytes);
        using var output = new MemoryStream();
        MediaThumbnail thumbnail = Assert.IsType<MediaThumbnail>(MediaThumbnail.TryCreate(TinyPng()));

        Mp3MetadataWriteResult result = await Mp3MetadataWriter.TryWriteAsync(source, output,
            new MediaMergeMetadata { Title = "New Title" }, thumbnail, default);

        Assert.Equal(Mp3MetadataWriteResult.ExistingTags, result);
        Assert.Empty(output.ToArray());
        Assert.Equal(sourceBytes, source.ToArray());
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(4, 0x80)]
    public async Task UnsupportedExistingTagVersionsAndGlobalFlagsStayUnchanged(byte version, byte flags)
    {
        byte[] mp3 = Fixture;
        byte[] existing = BuildTagWithHeader(version, flags, BuildTextFrame("TIT2", "Old Title"));
        byte[] sourceBytes = [.. existing, .. mp3];
        using var source = new MemoryStream(sourceBytes);
        using var output = new MemoryStream();

        Mp3MetadataWriteResult result = await Mp3MetadataWriter.TryWriteAsync(source, output,
            new MediaMergeMetadata { Title = "New Title" }, null, default);

        Assert.Equal(Mp3MetadataWriteResult.ExistingTags, result);
        Assert.Empty(output.ToArray());
        Assert.Equal(sourceBytes, source.ToArray());
    }

    [Fact]
    public async Task MalformedExistingFrameKeepsTheOriginalFileUnchanged()
    {
        byte[] mp3 = Fixture;
        byte[] malformedFrame = [.. "TIT2"u8, 0, 0, 0, 5, 0, 0];
        byte[] sourceBytes = [.. BuildTag(malformedFrame), .. mp3];
        using var source = new MemoryStream(sourceBytes);
        using var output = new MemoryStream();

        Mp3MetadataWriteResult result = await Mp3MetadataWriter.TryWriteAsync(source, output,
            new MediaMergeMetadata { Title = "New Title" }, null, default);

        Assert.Equal(Mp3MetadataWriteResult.ExistingTags, result);
        Assert.Empty(output.ToArray());
        Assert.Equal(sourceBytes, source.ToArray());
    }

    [Fact]
    public async Task MissingCoverStillEmbedsTextTagsAndKeepsAudioBytes()
    {
        byte[] mp3 = Fixture;
        using var source = new MemoryStream(mp3);
        using var output = new MemoryStream();

        Assert.Null(MediaThumbnail.TryCreate([1, 2, 3, 4]));
        Mp3MetadataWriteResult result = await Mp3MetadataWriter.TryWriteAsync(source, output,
            new MediaMergeMetadata { Title = "Song One", Artist = "Fixture Band" }, null, default);

        Assert.Equal(Mp3MetadataWriteResult.Written, result);
        byte[] tagged = output.ToArray();
        int tagLength = ReadSynchsafe(tagged.AsSpan(6, 4));
        Dictionary<string, byte[]> frames = ReadFrames(tagged.AsSpan(10, tagLength));
        Assert.Equal("Song One", ReadText(frames["TIT2"]));
        Assert.False(frames.ContainsKey("APIC"));
        Assert.Equal(mp3, tagged.AsSpan(10 + tagLength).ToArray());
    }

    [Fact]
    public async Task CoverCanBeEmbeddedWhenNoTextTagsAreAvailable()
    {
        byte[] mp3 = Fixture;
        byte[] cover = TinyPng();
        MediaThumbnail thumbnail = Assert.IsType<MediaThumbnail>(MediaThumbnail.TryCreate(cover));
        using var source = new MemoryStream(mp3);
        using var output = new MemoryStream();

        Mp3MetadataWriteResult result = await Mp3MetadataWriter.TryWriteAsync(source, output,
            new MediaMergeMetadata(), thumbnail, default);

        Assert.Equal(Mp3MetadataWriteResult.Written, result);
        byte[] tagged = output.ToArray();
        int tagLength = ReadSynchsafe(tagged.AsSpan(6, 4));
        Dictionary<string, byte[]> frames = ReadFrames(tagged.AsSpan(10, tagLength));
        Assert.Single(frames);
        Assert.Equal(cover, ReadPicture(frames["APIC"]));
        Assert.Equal(mp3, tagged.AsSpan(10 + tagLength).ToArray());
    }

    [Theory]
    [InlineData("finalize.beforeMove")]
    [InlineData("finalize.afterMove")]
    public async Task DirectMp3TagsRecoverAcrossPublicationCrashes(string failpoint)
    {
        await using var server = new FaultServer();
        byte[] mp3 = Fixture;
        byte[] cover = TinyPng();
        server.Extra["/music/song.mp3"] = mp3;
        server.Extra["/music/cover.png"] = cover;
        await server.StartAsync();
        int hit = 0;
        using var harness = new Harness(name =>
        {
            if (name == failpoint && Interlocked.Increment(ref hit) == 1) throw new FailpointCrash(name);
        });
        var metadata = new MediaMergeMetadata
        {
            Title = "Song One", Artist = "Fixture Band", Album = "Fixture Album", AlbumArtist = "Fixture Band",
            TrackNumber = 2, DiscNumber = 1, ReleaseDate = "2024-03-12"
        };
        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/music/song.mp3"), FileName: "song.mp3",
            Kind: JobKind.Http, MediaProvider: "bandcamp", MediaId: "track-1",
            MediaThumbnailUrl: server.Url("/music/cover.png"), MediaMetadataJson: System.Text.Json.JsonSerializer.Serialize(metadata)));
        Assert.True(added.Ok, added.Message);
        harness.Engine.Rename(added.JobId, "renamed.wav");
        JobSnapshot renamed = harness.Engine.Snapshot(added.JobId)!;
        Assert.Equal("renamed.mp3", renamed.FileName);
        Assert.Contains("extension was kept", renamed.Message, StringComparison.OrdinalIgnoreCase);

        await harness.WaitAsync(added.JobId, job => job.State == JobState.Finalizing, 15);
        await Task.Delay(300);
        harness.Restart();

        JobSnapshot done = await harness.WaitAsync(added.JobId,
            job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed, 20);
        Assert.Equal(JobState.Completed, done.State);
        Assert.Equal(1, hit);
        using var output = File.OpenRead(done.FinalPath!);
        byte[] tagged = new byte[output.Length];
        await output.ReadExactlyAsync(tagged);
        Assert.Equal("ID3", Encoding.ASCII.GetString(tagged, 0, 3));
        int tagLength = ReadSynchsafe(tagged.AsSpan(6, 4));
        Dictionary<string, byte[]> frames = ReadFrames(tagged.AsSpan(10, tagLength));
        Assert.Equal("Song One", ReadText(frames["TIT2"]));
        Assert.Equal("Fixture Album", ReadText(frames["TALB"]));
        Assert.Equal(cover, ReadPicture(frames["APIC"]));
        Assert.Equal(mp3, tagged.AsSpan(10 + tagLength).ToArray());
        Assert.Equal("renamed.mp3", Path.GetFileName(done.FinalPath));
        Assert.Single(harness.Files(), path => path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(harness.Files(), path => path.EndsWith(".vpart", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".tagged.mp3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CrashAfterMp3StageFlushKeepsPartialAndRebuildsOrphanStage()
    {
        await using var server = new FaultServer();
        byte[] mp3 = Fixture;
        server.Extra["/music/song.mp3"] = mp3;
        server.Extra["/music/cover.png"] = TinyPng();
        await server.StartAsync();
        int hit = 0;
        using var harness = new Harness(name =>
        {
            if (name == "finalize.afterMediaPrepare" && Interlocked.Increment(ref hit) == 1)
                throw new FailpointCrash(name);
        });
        var metadata = new MediaMergeMetadata { Title = "Song One", Artist = "Fixture Band", Album = "Fixture Album" };
        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/music/song.mp3"), FileName: "song.mp3",
            Kind: JobKind.Http, MediaProvider: "bandcamp", MediaId: "track-stage",
            MediaThumbnailUrl: server.Url("/music/cover.png"), MediaMetadataJson: System.Text.Json.JsonSerializer.Serialize(metadata)));
        Assert.True(added.Ok, added.Message);

        JobSnapshot finalizing = await harness.WaitAsync(added.JobId, job => job.State == JobState.Finalizing, 15);
        await Task.Delay(300);
        Assert.Equal(1, hit);
        Assert.Equal(mp3, await File.ReadAllBytesAsync(finalizing.PartialPath));
        string orphanStage = finalizing.PartialPath + ".tagged.mp3";
        Assert.True(File.Exists(orphanStage));
        Assert.StartsWith("ID3", Encoding.ASCII.GetString(await File.ReadAllBytesAsync(orphanStage), 0, 3));

        harness.Restart();
        JobSnapshot done = await harness.WaitAsync(added.JobId,
            job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed, 20);

        Assert.Equal(JobState.Completed, done.State);
        byte[] tagged = await File.ReadAllBytesAsync(done.FinalPath!);
        int tagLength = ReadSynchsafe(tagged.AsSpan(6, 4));
        Assert.Equal(mp3, tagged.AsSpan(10 + tagLength).ToArray());
        Assert.False(File.Exists(orphanStage));
    }

    [Fact]
    public async Task InvalidCoverDoesNotBlockDirectMp3DownloadOrTextTags()
    {
        await using var server = new FaultServer();
        byte[] mp3 = Fixture;
        server.Extra["/music/song.mp3"] = mp3;
        server.Extra["/music/bad-cover.png"] = [1, 2, 3, 4, 5];
        await server.StartAsync();
        using var harness = new Harness();
        var metadata = new MediaMergeMetadata { Title = "Song One", Artist = "Fixture Band", Album = "Fixture Album" };
        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/music/song.mp3"), FileName: "song.mp3",
            Kind: JobKind.Http, MediaProvider: "bandcamp", MediaId: "track-2",
            MediaThumbnailUrl: server.Url("/music/bad-cover.png"), MediaMetadataJson: System.Text.Json.JsonSerializer.Serialize(metadata)));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await harness.WaitAsync(added.JobId,
            job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);

        Assert.Equal(JobState.Completed, done.State);
        Assert.Equal("media-metadata", done.Reason);
        Assert.Contains("artwork was unavailable", done.Message, StringComparison.OrdinalIgnoreCase);
        byte[] tagged = await File.ReadAllBytesAsync(done.FinalPath!);
        int tagLength = ReadSynchsafe(tagged.AsSpan(6, 4));
        Dictionary<string, byte[]> frames = ReadFrames(tagged.AsSpan(10, tagLength));
        Assert.Equal("Song One", ReadText(frames["TIT2"]));
        Assert.False(frames.ContainsKey("APIC"));
        Assert.Equal(mp3, tagged.AsSpan(10 + tagLength).ToArray());
    }

    [Fact]
    public async Task ExistingSupportedMp3TagsReceiveSourceMetadataAndArtwork()
    {
        await using var server = new FaultServer();
        byte[] mp3 = Fixture;
        byte[] existing = [.. BuildTag(), .. mp3];
        server.Extra["/music/existing.mp3"] = existing;
        server.Extra["/music/cover.png"] = TinyPng();
        await server.StartAsync();
        using var harness = new Harness();
        var metadata = new MediaMergeMetadata { Title = "Song One", Artist = "Fixture Band", Album = "Fixture Album" };
        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/music/existing.mp3"), FileName: "existing.mp3",
            Kind: JobKind.Http, MediaProvider: "bandcamp", MediaId: "track-existing",
            MediaThumbnailUrl: server.Url("/music/cover.png"), MediaMetadataJson: System.Text.Json.JsonSerializer.Serialize(metadata)));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await harness.WaitAsync(added.JobId,
            job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);

        Assert.Equal(JobState.Completed, done.State);
        Assert.NotEqual("media-metadata", done.Reason);
        byte[] tagged = await File.ReadAllBytesAsync(done.FinalPath!);
        int tagLength = ReadSynchsafe(tagged.AsSpan(6, 4));
        Dictionary<string, byte[]> frames = ReadFrames(tagged.AsSpan(10, tagLength));
        Assert.Equal("Song One", ReadText(frames["TIT2"]));
        Assert.Equal("Fixture Band", ReadText(frames["TPE1"]));
        Assert.Equal("Fixture Album", ReadText(frames["TALB"]));
        Assert.Equal(TinyPng(), ReadPicture(frames["APIC"]));
        Assert.Equal(mp3, tagged.AsSpan(10 + tagLength).ToArray());
        Assert.NotEqual(existing, tagged);
    }

    private static Dictionary<string, byte[]> ReadFrames(ReadOnlySpan<byte> body)
    {
        return ReadFrameList(body).ToDictionary(frame => frame.Id, frame => frame.Payload, StringComparer.Ordinal);
    }

    private static List<(string Id, byte[] Payload, byte[] Encoded)> ReadFrameList(ReadOnlySpan<byte> body)
    {
        var frames = new List<(string Id, byte[] Payload, byte[] Encoded)>();
        int offset = 0;
        while (offset + 10 <= body.Length)
        {
            string id = Encoding.ASCII.GetString(body.Slice(offset, 4));
            int length = ReadSynchsafe(body.Slice(offset + 4, 4));
            if (id == "\0\0\0\0") break;
            Assert.True(length <= body.Length - offset - 10);
            byte[] encoded = body.Slice(offset, length + 10).ToArray();
            frames.Add((id, body.Slice(offset + 10, length).ToArray(), encoded));
            offset += length + 10;
        }
        Assert.True(body[offset..].IndexOfAnyExcept((byte)0) < 0);
        return frames;
    }

    private static byte[] BuildTag(params byte[][] frames) => BuildTagWithHeader(4, 0, [.. frames.SelectMany(frame => frame)]);

    private static byte[] BuildTagWithHeader(byte version, byte flags, byte[] body)
    {
        byte[] tag = new byte[10 + body.Length];
        "ID3"u8.CopyTo(tag);
        tag[3] = version;
        tag[4] = 0;
        tag[5] = flags;
        WriteSynchsafe(tag.AsSpan(6, 4), body.Length);
        body.CopyTo(tag, 10);
        return tag;
    }

    private static byte[] BuildTextFrame(string id, string value) =>
        BuildFrame(id, [3, .. Encoding.UTF8.GetBytes(value)]);

    private static byte[] BuildPictureFrame(byte[] image, byte pictureType)
    {
        byte[] mime = Encoding.ASCII.GetBytes("image/png");
        byte[] payload = [3, .. mime, 0, pictureType, 0, .. image];
        return BuildFrame("APIC", payload);
    }

    private static byte[] BuildFrame(string id, byte[] payload)
    {
        byte[] frame = new byte[10 + payload.Length];
        Encoding.ASCII.GetBytes(id).CopyTo(frame, 0);
        WriteSynchsafe(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, 10);
        return frame;
    }

    private static void WriteSynchsafe(Span<byte> bytes, int value)
    {
        bytes[0] = (byte)((value >> 21) & 0x7F);
        bytes[1] = (byte)((value >> 14) & 0x7F);
        bytes[2] = (byte)((value >> 7) & 0x7F);
        bytes[3] = (byte)(value & 0x7F);
    }

    private static int ReadSynchsafe(ReadOnlySpan<byte> bytes) =>
        (bytes[0] << 21) | (bytes[1] << 14) | (bytes[2] << 7) | bytes[3];

    private static string ReadText(byte[] frame)
    {
        Assert.Equal(3, frame[0]);
        return Encoding.UTF8.GetString(frame, 1, frame.Length - 1);
    }

    private static byte[] ReadPicture(byte[] frame, byte expectedType = 3)
    {
        Assert.Equal(3, frame[0]);
        int mimeEnd = Array.IndexOf(frame, (byte)0, 1);
        Assert.True(mimeEnd > 1);
        Assert.Equal("image/png", Encoding.ASCII.GetString(frame, 1, mimeEnd - 1));
        Assert.Equal(expectedType, frame[mimeEnd + 1]);
        Assert.Equal(0, frame[mimeEnd + 2]);
        return frame[(mimeEnd + 3)..];
    }

    private static byte PictureType(byte[] frame)
    {
        int mimeEnd = Array.IndexOf(frame, (byte)0, 1);
        Assert.True(mimeEnd > 1);
        return frame[mimeEnd + 1];
    }
}
