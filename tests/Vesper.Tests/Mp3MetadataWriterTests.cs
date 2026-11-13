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
    public async Task ExistingId3HeaderIsPreservedRatherThanDuplicated()
    {
        byte[] mp3 = Fixture;
        byte[] existing = [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 0, 0, .. mp3];
        using var source = new MemoryStream(existing);
        using var output = new MemoryStream();

        Mp3MetadataWriteResult result = await Mp3MetadataWriter.TryWriteAsync(source, output,
            new MediaMergeMetadata { Title = "Song One" }, null, default);

        Assert.Equal(Mp3MetadataWriteResult.ExistingTags, result);
        Assert.Empty(output.ToArray());
        Assert.Equal(existing, source.ToArray());
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
    public async Task ExistingMp3TagsAreLeftUnchangedWithAnAccurateCompletionNotice()
    {
        await using var server = new FaultServer();
        byte[] mp3 = Fixture;
        byte[] existing = [0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0, .. mp3];
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
        Assert.Equal("media-metadata", done.Reason);
        Assert.Contains("existing tags unchanged", done.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not applied", done.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existing, await File.ReadAllBytesAsync(done.FinalPath!));
    }

    private static Dictionary<string, byte[]> ReadFrames(ReadOnlySpan<byte> body)
    {
        var frames = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int offset = 0;
        while (offset + 10 <= body.Length)
        {
            string id = Encoding.ASCII.GetString(body.Slice(offset, 4));
            int length = ReadSynchsafe(body.Slice(offset + 4, 4));
            offset += 10;
            if (id == "\0\0\0\0" || length <= 0) break;
            Assert.True(length <= body.Length - offset);
            frames.Add(id, body.Slice(offset, length).ToArray());
            offset += length;
        }
        Assert.Equal(body.Length, offset);
        return frames;
    }

    private static int ReadSynchsafe(ReadOnlySpan<byte> bytes) =>
        (bytes[0] << 21) | (bytes[1] << 14) | (bytes[2] << 7) | bytes[3];

    private static string ReadText(byte[] frame)
    {
        Assert.Equal(3, frame[0]);
        return Encoding.UTF8.GetString(frame, 1, frame.Length - 1);
    }

    private static byte[] ReadPicture(byte[] frame)
    {
        Assert.Equal(3, frame[0]);
        int mimeEnd = Array.IndexOf(frame, (byte)0, 1);
        Assert.True(mimeEnd > 1);
        Assert.Equal("image/png", Encoding.ASCII.GetString(frame, 1, mimeEnd - 1));
        Assert.Equal(3, frame[mimeEnd + 1]);
        Assert.Equal(0, frame[mimeEnd + 2]);
        return frame[(mimeEnd + 3)..];
    }
}
