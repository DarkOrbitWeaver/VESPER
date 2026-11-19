using System.Reflection;
using System.Runtime.ExceptionServices;
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
    public void SearchIndexTracksMetadataAndSourceChangesWithoutIndexingQueryTokens()
    {
        DownloadItem item = DownloadItem.From(new Vesper.Engine.JobSnapshot
        {
            FileName = "episode.mp3",
            SourceUrl = "https://old.example.test/file.mp3?token=old-secret",
            MediaProvider = "Old Provider",
            MediaId = "old-id",
            Category = "Music",
            State = Vesper.Engine.JobState.Queued
        });

        const string metadataTerm = "new provider";
        JobStatus previousStatus = item.Status;
        string previousName = item.Name;
        long previousSize = item.SizeBytes;
        bool previouslyMatched = item.MatchesSearch(metadataTerm);

        item.UpdateFrom(new Vesper.Engine.JobSnapshot
        {
            FileName = "episode.mp3",
            SourceUrl = "https://old.example.test/file.mp3?token=new-secret",
            MediaProvider = "New Provider",
            MediaId = "new-id",
            Category = "Podcasts",
            State = Vesper.Engine.JobState.Queued
        });

        Assert.False(item.MatchesSearch("old provider"));
        Assert.False(item.MatchesSearch("old-id"));
        Assert.True(item.MatchesSearch("new provider"));
        Assert.True(Form1.NeedsListRefresh(item, previousStatus, previousName, previousSize,
            previouslyMatched, metadataTerm, sortBySize: false));
        Assert.True(item.MatchesSearch("new-id"));
        Assert.True(item.MatchesSearch("podcasts"));
        Assert.True(item.MatchesSearch("old.example.test"));
        Assert.False(item.MatchesSearch("old-secret"));
        Assert.False(item.MatchesSearch("new-secret"));

        item.UpdateFrom(new Vesper.Engine.JobSnapshot
        {
            FileName = "episode.mp3",
            SourceUrl = "https://new.example.test/file.mp3?token=latest-secret",
            MediaProvider = "New Provider",
            MediaId = "new-id",
            Category = "Podcasts",
            State = Vesper.Engine.JobState.Queued
        });

        Assert.False(item.MatchesSearch("old.example.test"));
        Assert.True(item.MatchesSearch("new.example.test"));
        Assert.False(item.MatchesSearch("latest-secret"));
    }

    [Fact]
    public void Ten_thousand_downloads_keep_search_order_and_selection_by_download_id()
    {
        RunOnSta(() =>
        {
            using var form = new Form1(new VesperPreferences(), null, startHidden: false);
            _ = form.Handle;
            DownloadItem[] rows = Enumerable.Range(0, 10_000).Select(index => DownloadItem.From(new Vesper.Engine.JobSnapshot
            {
                Id = Guid.NewGuid(),
                FileName = $"download-{index:D5}.bin",
                SourceUrl = $"https://cdn.example.test/files/{index:D5}.bin?token=private-{index}",
                TotalBytes = 10_000 - index,
                State = index % 2 == 0 ? Vesper.Engine.JobState.Completed : Vesper.Engine.JobState.Paused,
                CreatedUtc = DateTime.UnixEpoch.AddMinutes(index)
            })).ToArray();
            form.LoadPreviewRows(rows);

            DownloadGrid grid = (DownloadGrid)typeof(Form1).GetField("_grid", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(form)!;
            VesperChoice sort = (VesperChoice)typeof(Form1).GetField("_sort", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(form)!;
            VesperChoice filter = (VesperChoice)typeof(Form1).GetField("_filter", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(form)!;
            DownloadItem target = rows[7_500];

            Assert.Equal(10_000, grid.VisibleItems.Count);
            Assert.Equal(rows[^1].Id, grid.VisibleItems[0].Id);
            grid.CheckAllVisible();
            sort.SelectedIndex = 1;

            Assert.Equal(rows[0].Id, grid.VisibleItems[0].Id);
            Assert.Equal(10_000, grid.CheckedCount);
            Assert.Equal(target.Id, Assert.Single(rows, item => item.MatchesSearch("download-07500")).Id);
            Assert.DoesNotContain(rows, item => item.MatchesSearch("private-7500"));

            filter.SelectedIndex = 4;
            Assert.Equal(5_000, grid.VisibleItems.Count);
            Assert.Contains(grid.VisibleItems, item => item.Id == target.Id);
            Assert.True(grid.IsChecked(target));

            filter.SelectedIndex = 0;
            Assert.Equal(10_000, grid.VisibleItems.Count);
            Assert.Contains(grid.VisibleItems, item => item.Id == target.Id);
            Assert.True(grid.IsChecked(target));
        });
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

    [Fact]
    public void Keyboard_focus_stays_with_the_same_download_when_rows_reorder()
    {
        var first = new DownloadItem { Id = Guid.NewGuid(), Name = "first.bin" };
        var focused = new DownloadItem { Id = Guid.NewGuid(), Name = "focused.bin" };
        var last = new DownloadItem { Id = Guid.NewGuid(), Name = "last.bin" };
        using var grid = new DownloadGrid { Size = new Size(640, 300) };
        grid.CreateControl();
        grid.SetItems([first, focused, last]);
        Assert.True(grid.Reveal(focused.Id));
        var refreshedFocused = new DownloadItem { Id = focused.Id, Name = "focused-refresh.bin" };
        grid.SetItems(
        [
            new DownloadItem { Id = last.Id, Name = "last-refresh.bin" },
            new DownloadItem { Id = first.Id, Name = "first-refresh.bin" },
            refreshedFocused
        ]);

        DownloadItem? opened = null;
        grid.DetailsRequested += item => opened = item;
        var onKeyDown = typeof(DownloadGrid).GetMethod("OnKeyDown",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        onKeyDown.Invoke(grid, [new KeyEventArgs(Keys.Enter)]);

        Assert.Equal(focused.Id, opened?.Id);
        Assert.Equal("focused-refresh.bin", opened?.Name);
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The search test did not complete.");
        failure?.Throw();
    }
}
