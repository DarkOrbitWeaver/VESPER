using System.Text;
using System.Text.Json;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class AudioMetadataIntegrationTests
{
    private static string Fixture(string extension) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", "quiet-track." + extension);

    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");

    [Theory]
    [InlineData("flac", "fLaC")]
    [InlineData("ogg", "OggS")]
    [InlineData("opus", "OggS")]
    public async Task DirectAudioMetadataIsWrittenBeforeTheOriginalFormatIsPublished(string extension, string signature)
    {
        await using var server = new FaultServer();
        byte[] original = await File.ReadAllBytesAsync(Fixture(extension));
        server.Extra["/music/song." + extension] = original;
        server.Extra["/music/cover.png"] = TinyPng();
        await server.StartAsync();

        using var harness = new Harness();
        var metadata = new MediaMergeMetadata
        {
            Title = "Song One", Artist = "Fixture Band", Album = "Fixture Album", AlbumArtist = "Fixture Band",
            TrackNumber = 2, DiscNumber = 1, ReleaseDate = "2024-03-12"
        };
        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/music/song." + extension), FileName: "song." + extension,
            Kind: JobKind.Http, MediaProvider: "bandcamp", MediaId: "fixture-song",
            MediaThumbnailUrl: server.Url("/music/cover.png"), MediaMetadataJson: JsonSerializer.Serialize(metadata)));
        Assert.True(added.Ok, added.Message);

        JobSnapshot done = await harness.WaitAsync(added.JobId,
            job => job.State is JobState.Completed or JobState.NeedsAttention or JobState.Failed);

        Assert.Equal(JobState.Completed, done.State);
        Assert.Equal("." + extension, Path.GetExtension(done.FinalPath));
        byte[] tagged = await File.ReadAllBytesAsync(done.FinalPath!);
        Assert.Equal(signature, Encoding.ASCII.GetString(tagged, 0, signature.Length));
        Assert.NotEqual(original, tagged);
        Assert.Single(harness.Files());

        string? qaPath = Environment.GetEnvironmentVariable("VESPER_MEDIA_QA_FINAL_" + extension.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(qaPath))
        {
            string fullPath = Path.GetFullPath(qaPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, tagged);
        }
    }
}
