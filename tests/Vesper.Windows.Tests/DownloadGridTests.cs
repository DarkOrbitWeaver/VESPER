using VESPER;

namespace Vesper.Windows.Tests;

public sealed class DownloadGridTests
{
    [Fact]
    public void SearchFindsMediaDetailsAndPublicSourceAddressWithoutIndexingQueryTokens()
    {
        DownloadItem item = DownloadItem.From(new Vesper.Engine.JobSnapshot
        {
            FileName = "quiet-evening.m4a",
            SourceUrl = "https://cdn.example.test/audio/quiet-evening.m4a?token=private-value",
            OriginalPageUrl = "https://bandcamp.com/album/quiet-evening?signature=private-value",
            MediaProvider = "Bandcamp",
            MediaId = "album-481",
            MediaQuality = "audio",
            MediaFormatId = "mp4a.40.2",
            Category = "Music",
            ContentType = "audio/mp4",
            State = Vesper.Engine.JobState.Completed
        });

        Assert.True(item.MatchesSearch("bandcamp"));
        Assert.True(item.MatchesSearch("quiet-evening"));
        Assert.True(item.MatchesSearch("album-481"));
        Assert.True(item.MatchesSearch("music"));
        Assert.True(item.MatchesSearch("completed"));
        Assert.False(item.MatchesSearch("private-value"));
        Assert.False(item.MatchesSearch("not-in-this-download"));
    }

    [Fact]
    public void RevealScrollsToAnExistingRow()
    {
        using var grid = new DownloadGrid { Size = new Size(640, 300) };
        grid.CreateControl();
        DownloadItem[] items = Enumerable.Range(0, 50)
            .Select(_ => new DownloadItem { Id = Guid.NewGuid(), Name = "download.bin" })
            .ToArray();
        grid.SetItems(items);

        Assert.True(grid.Reveal(items[30].Id));
        Assert.True(grid.AutoScrollPosition.Y < 0);
        Assert.False(grid.Reveal(Guid.NewGuid()));
    }
}
