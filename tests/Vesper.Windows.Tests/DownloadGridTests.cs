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
    public void SearchAddressChangesWhenAJobSourceIsRefreshed()
    {
        DownloadItem item = DownloadItem.From(new Vesper.Engine.JobSnapshot
        {
            FileName = "installer.exe",
            SourceUrl = "https://old.example.test/file?token=old-secret",
            State = Vesper.Engine.JobState.Paused
        });

        item.UpdateFrom(new Vesper.Engine.JobSnapshot
        {
            FileName = "installer.exe",
            SourceUrl = "https://new.example.test/files/installer.exe?token=new-secret",
            State = Vesper.Engine.JobState.Paused
        });

        Assert.False(item.MatchesSearch("old.example.test"));
        Assert.True(item.MatchesSearch("new.example.test"));
        Assert.False(item.MatchesSearch("new-secret"));
    }
    [Fact]
    public void SearchRebuildsWhenTheSourceStartsMatching()
    {
        DownloadItem item = DownloadItem.From(new Vesper.Engine.JobSnapshot
        {
            FileName = "sample.bin",
            SourceUrl = "https://old.example.test/file",
            State = Vesper.Engine.JobState.Paused
        });

        const string searchTerm = "new.example.test";
        JobStatus previousStatus = item.Status;
        string previousName = item.Name;
        long previousSize = item.SizeBytes;
        bool previouslyMatched = item.MatchesSearch(searchTerm);

        item.UpdateFrom(new Vesper.Engine.JobSnapshot
        {
            FileName = "sample.bin",
            SourceUrl = "https://new.example.test/file?token=secret",
            State = Vesper.Engine.JobState.Paused
        });

        Assert.False(previouslyMatched);
        Assert.True(item.MatchesSearch(searchTerm));
        Assert.True(Form1.NeedsListRefresh(item, previousStatus, previousName,
            previousSize, previouslyMatched, searchTerm, sortBySize: false));
        Assert.False(item.MatchesSearch("secret"));
    }

    [Fact]
    public void SearchRebuildsWhenAReasonStopsMatching()
    {
        DownloadItem item = DownloadItem.From(new Vesper.Engine.JobSnapshot
        {
            FileName = "sample.bin",
            Reason = "authentication needed",
            State = Vesper.Engine.JobState.Paused
        });

        const string searchTerm = "authentication";
        JobStatus previousStatus = item.Status;
        string previousName = item.Name;
        long previousSize = item.SizeBytes;
        bool previouslyMatched = item.MatchesSearch(searchTerm);

        item.UpdateFrom(new Vesper.Engine.JobSnapshot
        {
            FileName = "sample.bin",
            Reason = "ready",
            State = Vesper.Engine.JobState.Paused
        });

        Assert.True(previouslyMatched);
        Assert.False(item.MatchesSearch(searchTerm));
        Assert.True(Form1.NeedsListRefresh(item, previousStatus, previousName,
            previousSize, previouslyMatched, searchTerm, sortBySize: false));
    }

    [Fact]
    public void SizeSortRefreshesUnknownSizeWithoutRebuildingOtherViews()
    {
        DownloadItem item = DownloadItem.From(new Vesper.Engine.JobSnapshot
        {
            FileName = "sample.bin",
            State = Vesper.Engine.JobState.Downloading,
            CommittedBytes = 10
        });

        JobStatus previousStatus = item.Status;
        string previousName = item.Name;
        long previousSize = item.SizeBytes;
        const string searchTerm = "sample";
        bool previouslyMatched = item.MatchesSearch(searchTerm);

        item.UpdateFrom(new Vesper.Engine.JobSnapshot
        {
            FileName = "sample.bin",
            State = Vesper.Engine.JobState.Downloading,
            CommittedBytes = 40
        });

        Assert.False(Form1.NeedsListRefresh(item, previousStatus, previousName,
            previousSize, previouslyMatched, searchTerm, sortBySize: false));
        Assert.True(Form1.NeedsListRefresh(item, previousStatus, previousName,
            previousSize, previouslyMatched, searchTerm, sortBySize: true));
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
