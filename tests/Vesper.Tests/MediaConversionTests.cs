using System.Text;
using Vesper.Engine;
using Xunit;

namespace Vesper.Tests;

public sealed class MediaConversionTests
{
    private static byte[] Audio()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media");
        return [.. File.ReadAllBytes(Path.Combine(folder, "audio-init.mp4")), .. File.ReadAllBytes(Path.Combine(folder, "audio-1.m4s"))];
    }

    [WindowsFact]
    public async Task WindowsMediaFoundation_PreservesFragmentedAacInAnM4aOutput()
    {
        await using var server = new FaultServer();
        byte[] sourceBytes = server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();
        using var harness = new Harness();
        Guid id = AddAudio(harness, server);

        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal("", done.Reason);
        Assert.Equal("audio.m4a", done.FileName);
        Assert.Equal("audio/mp4", done.ContentType);
        Assert.Equal("Audio AAC", done.MediaQuality);
        Assert.NotNull(done.FinalPath);
        Assert.True(server.Hits("/audio.mp4") > 0);

        byte[] outputBytes = await File.ReadAllBytesAsync(done.FinalPath!);
        Assert.True(outputBytes.Length > 8);
        Assert.Equal("ftyp", Encoding.ASCII.GetString(outputBytes, 4, 4));
        Assert.Equal(outputBytes.LongLength, done.TotalBytes);

        using var source = new MemoryStream(sourceBytes);
        DemuxedTrack sourceAudio = Assert.Single(MediaMerger.Demux(source, source.Length, default));
        using var output = new MemoryStream(outputBytes);
        DemuxedTrack outputAudio = Assert.Single(MediaMerger.Demux(output, output.Length, default));

        Assert.Equal(TrackKind.Audio, outputAudio.Kind);
        Assert.Equal("aac", outputAudio.Codec);
        Assert.Equal("mp4", outputAudio.SourceContainer);
        Assert.Equal(sourceAudio.Channels, outputAudio.Channels);
        Assert.Equal(sourceAudio.SampleRate, outputAudio.SampleRate);
        Assert.NotEmpty(outputAudio.Samples);
        Assert.InRange(Math.Abs(outputAudio.DurationSeconds - sourceAudio.DurationSeconds), 0, 0.05);
    }

    [Fact]
    public async Task Invalid_converted_output_publishes_the_original_unchanged()
    {
        await using var server = new FaultServer();
        byte[] audio = server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();
        using var h = new Harness(convertMedia: (_, _, target, _, _) => File.WriteAllBytes(target, [1, 2, 3]));
        Guid id = AddAudio(h, server);

        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal("saved-original", done.Reason);
        Assert.Equal("audio.mp4", done.FileName);
        Assert.Equal(audio, File.ReadAllBytes(Assert.Single(h.Files())));
    }

    [Theory]
    [InlineData("finalize.beforeMove")]
    [InlineData("finalize.afterMove")]
    public async Task A_crash_while_publishing_compatible_output_recovers_one_valid_file(string point)
    {
        await using var server = new FaultServer();
        byte[] audio = server.Extra["/audio.mp4"] = Audio();
        await server.StartAsync();
        using var crashed = new ManualResetEventSlim();
        int conversions = 0;
        using var h = new Harness(failpoint: step =>
        {
            if (step != point) return;
            crashed.Set();
            throw new FailpointCrash(step);
        }, convertMedia: (source, _, target, _, _) =>
        {
            Interlocked.Increment(ref conversions);
            File.Copy(source, target);
        });
        Guid id = AddAudio(h, server);
        Assert.True(crashed.Wait(TimeSpan.FromSeconds(10)), "The conversion did not reach the publication failpoint.");
        JobSnapshot before = h.Engine.Snapshot(id)!;
        Assert.True(File.Exists(before.PartialPath));
        bool moved = point == "finalize.afterMove";
        string occupied = Path.Combine(h.Downloads, moved ? "kept.m4a" : "audio.m4a");
        File.WriteAllText(occupied, "unrelated");

        h.Restart();
        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(moved ? "audio.m4a" : "audio (1).m4a", done.FileName);
        Assert.Equal("", done.Reason);
        Assert.Equal(audio, File.ReadAllBytes(done.FinalPath!));
        Assert.Equal(audio.LongLength, done.TotalBytes);
        Assert.Equal("audio/mp4", done.ContentType);
        Assert.Equal("unrelated", File.ReadAllText(occupied));
        Assert.Equal(2, h.Files().Length);
        Assert.Equal(moved ? 1 : 2, conversions);
    }

    private static Guid AddAudio(Harness h, FaultServer server)
    {
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/audio.mp4"), FileName: "audio.mp4",
            MediaProvider: "fixture", Output: MediaOutput.M4a));
        Assert.True(added.Ok, added.Message);
        return added.JobId;
    }

    // ---- converted output keeps title, artist and cover (VES-006) ----------------------------------------------

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", name));

    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");

    private static readonly MediaMergeMetadata SongTags = new()
    {
        Title = "Song One", Artist = "Fixture Band", Album = "Fixture Album", AlbumArtist = "Fixture Band",
        TrackNumber = 2, DiscNumber = 1, ReleaseDate = "2024-03-12"
    };

    // Stands in for Media Foundation: quiet-track-aac.m4a is the real AAC encode of quiet-track.webm.
    private static void WriteConvertedAac(string source, string extension, string target, MediaOutput output, CancellationToken ct) =>
        File.WriteAllBytes(target, Fixture("quiet-track-aac.m4a"));

    private static Guid AddOpusSong(Harness h, FaultServer server, string? cover = "/m/cover.png")
    {
        byte[] opus = Fixture("quiet-track.webm");
        var plan = new MediaMergePlan
        {
            Provider = "test", MediaId = "webm-song", OutputContainer = "webm", Metadata = SongTags,
            Tracks = [new MergeTrack("audio", "opus-track", server.Url("/m/audio.webm"), opus.Length, "webm", "opus", "audio-id")]
        };
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/m/audio.webm"), FileName: "song.webm",
            Kind: JobKind.Merge, MediaProvider: "test", MediaId: "webm-song", MediaPlanJson: plan.ToJson(),
            MediaThumbnailUrl: cover is null ? null : server.Url(cover), Output: MediaOutput.M4a));
        Assert.True(added.Ok, added.Message);
        return added.JobId;
    }

    private static void AssertSongTags(MediaMergeMetadata stored)
    {
        Assert.Equal(SongTags.Title, stored.Title);
        Assert.Equal(SongTags.Artist, stored.Artist);
        Assert.Equal(SongTags.Album, stored.Album);
        Assert.Equal(SongTags.AlbumArtist, stored.AlbumArtist);
        Assert.Equal(SongTags.TrackNumber, stored.TrackNumber);
        Assert.Equal(SongTags.DiscNumber, stored.DiscNumber);
        Assert.Equal(SongTags.ReleaseDate, stored.ReleaseDate);
    }

    // The converted bytes are copied, never re-encoded: every AAC frame must equal the converter's output.
    private static void AssertSameAacFrames(Stream published)
    {
        using var converted = new MemoryStream(Fixture("quiet-track-aac.m4a"));
        DemuxedTrack expected = Assert.Single(Mp4Demuxer.Read(converted, converted.Length, default));
        DemuxedTrack actual = Assert.Single(Mp4Demuxer.Read(published, published.Length, default));
        Assert.Equal("aac", actual.Codec);
        Assert.Equal(expected.Samples.Count, actual.Samples.Count);
        Assert.Equal(expected.PayloadBytes, actual.PayloadBytes);
        for (int i = 0; i < expected.Samples.Count; i++)
        {
            var want = new byte[expected.Samples[i].Size];
            var got = new byte[actual.Samples[i].Size];
            converted.Position = expected.Samples[i].Offset;
            converted.ReadExactly(want);
            published.Position = actual.Samples[i].Offset;
            published.ReadExactly(got);
            Assert.Equal(want, got);
        }
    }

    [Fact]
    public async Task Opus_converted_to_m4a_keeps_title_artist_and_cover()
    {
        await using var server = new FaultServer();
        server.Extra["/m/audio.webm"] = Fixture("quiet-track.webm");
        server.Extra["/m/cover.png"] = TinyPng();
        await server.StartAsync();
        using var h = new Harness(convertMedia: WriteConvertedAac);
        Guid id = AddOpusSong(h, server);

        JobSnapshot done = await h.WaitAsync(id, job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);

        Assert.Equal(JobState.Completed, done.State);
        Assert.Equal("", done.Reason);
        Assert.Equal("song.m4a", done.FileName);
        Assert.Equal("audio/mp4", done.ContentType);
        using FileStream output = File.OpenRead(done.FinalPath!);
        Assert.Equal(output.Length, done.TotalBytes);
        AssertSongTags(Mp4Demuxer.ReadMetadata(output, output.Length));
        MediaThumbnail cover = Assert.IsType<MediaThumbnail>(Mp4Demuxer.ReadThumbnail(output, output.Length));
        Assert.Equal(Payloads.Sha256(TinyPng()), cover.Sha256);
        AssertSameAacFrames(output);
        Assert.Single(h.Files());                                       // no .convert or .convert-tagged leftovers
        Assert.Equal(1, server.Hits("/m/cover.png"));                    // the cover the merge stored is reused, not fetched twice

        // Lets a developer hand the exact published bytes to ffprobe, mutagen or Windows Media Player.
        string? qaPath = Environment.GetEnvironmentVariable("VESPER_MEDIA_QA_CONVERTED_M4A");
        if (!string.IsNullOrWhiteSpace(qaPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(qaPath))!);
            File.Copy(done.FinalPath!, Path.GetFullPath(qaPath), overwrite: true);
        }
    }

    [Fact]
    public async Task A_directly_downloaded_file_converted_to_m4a_is_tagged_and_renamed()
    {
        await using var server = new FaultServer();
        server.Extra["/m/audio.webm"] = Fixture("quiet-track.webm");
        server.Extra["/m/cover.png"] = TinyPng();
        await server.StartAsync();
        using var h = new Harness(convertMedia: WriteConvertedAac);
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/m/audio.webm"), FileName: "song.webm",
            MediaProvider: "test", Output: MediaOutput.M4a, MediaThumbnailUrl: server.Url("/m/cover.png"),
            MediaMetadataJson: System.Text.Json.JsonSerializer.Serialize(SongTags)));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);

        // Before the fix this name stayed "song.webm" as soon as a converted job carried tags.
        Assert.Equal("song.m4a", done.FileName);
        Assert.Equal("", done.Reason);
        using FileStream output = File.OpenRead(done.FinalPath!);
        AssertSongTags(Mp4Demuxer.ReadMetadata(output, output.Length));
        Assert.NotNull(Mp4Demuxer.ReadThumbnail(output, output.Length));
        AssertSameAacFrames(output);
        Assert.Single(h.Files());
    }

    [Theory]
    [InlineData(null)]                 // no artwork was found for this media
    [InlineData("/m/broken.png")]      // the server sent something that is not an image
    [InlineData("/m/missing.png")]     // the artwork link is dead
    public async Task Missing_or_broken_artwork_never_blocks_the_converted_file(string? cover)
    {
        await using var server = new FaultServer();
        server.Extra["/m/audio.webm"] = Fixture("quiet-track.webm");
        server.Extra["/m/broken.png"] = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        await server.StartAsync();
        using var h = new Harness(convertMedia: WriteConvertedAac);
        Guid id = AddOpusSong(h, server, cover);

        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal("song.m4a", done.FileName);
        using FileStream output = File.OpenRead(done.FinalPath!);
        AssertSongTags(Mp4Demuxer.ReadMetadata(output, output.Length));
        Assert.Null(Mp4Demuxer.ReadThumbnail(output, output.Length));
        AssertSameAacFrames(output);
        if (cover is null) Assert.Equal("", done.Reason);
        else
        {
            Assert.Equal("media-metadata", done.Reason);
            Assert.Contains("artwork was unavailable", done.Message);
        }
    }

    [Fact]
    public async Task A_converted_file_without_any_tags_is_published_exactly_as_converted()
    {
        await using var server = new FaultServer();
        server.Extra["/audio.webm"] = Fixture("quiet-track.webm");
        await server.StartAsync();
        using var h = new Harness(convertMedia: WriteConvertedAac);
        AddResult added = h.Engine.Add(new AddRequest(server.Url("/audio.webm"), FileName: "audio.webm",
            MediaProvider: "fixture", Output: MediaOutput.M4a));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await h.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.Equal("audio.m4a", done.FileName);
        Assert.Equal(Fixture("quiet-track-aac.m4a"), File.ReadAllBytes(done.FinalPath!));   // nothing invented, nothing rewritten
    }

    [Theory]
    [InlineData("finalize.afterMediaPrepare")]
    [InlineData("finalize.beforeMove")]
    [InlineData("finalize.afterMove")]
    public async Task A_crash_around_publishing_a_tagged_conversion_recovers_one_tagged_file(string point)
    {
        await using var server = new FaultServer();
        server.Extra["/m/audio.webm"] = Fixture("quiet-track.webm");
        server.Extra["/m/cover.png"] = TinyPng();
        await server.StartAsync();
        using var crashed = new ManualResetEventSlim();
        using var h = new Harness(failpoint: step =>
        {
            if (step != point) return;
            crashed.Set();
            throw new FailpointCrash(step);
        }, convertMedia: WriteConvertedAac);
        Guid id = AddOpusSong(h, server);
        Assert.True(crashed.Wait(TimeSpan.FromSeconds(10)), "The conversion did not reach the failpoint.");

        h.Restart();
        JobSnapshot done = await h.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal("song.m4a", done.FileName);
        using (FileStream output = File.OpenRead(done.FinalPath!))
        {
            Assert.Equal(output.Length, done.TotalBytes);
            AssertSongTags(Mp4Demuxer.ReadMetadata(output, output.Length));
            Assert.NotNull(Mp4Demuxer.ReadThumbnail(output, output.Length));
            AssertSameAacFrames(output);
        }
        Assert.Single(h.Files());
        Assert.Empty(Directory.GetFiles(h.Downloads, "*.convert*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Tags_are_refused_for_an_original_file_that_cannot_hold_them()
    {
        using var h = new Harness();
        AddResult added = h.Engine.Add(new AddRequest("https://example.com/audio.webm", FileName: "audio.webm",
            MediaProvider: "fixture", MediaMetadataJson: System.Text.Json.JsonSerializer.Serialize(SongTags), StartNow: false));
        Assert.Equal(AddStatus.Invalid, added.Status);
    }
}
