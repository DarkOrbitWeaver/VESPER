using Vesper.Engine;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;

namespace VESPER;

[System.ComponentModel.DesignerCategory("Form")]
internal sealed class PodcastFollowForm : VesperPopupForm
{
    private sealed class FeedRow(PodcastFeedSubscription feed)
    {
        public PodcastFeedSubscription Feed { get; } = feed;
        public override string ToString() => Feed.Title + "  ·  " + new Uri(Feed.Url).Host;
    }

    private sealed class EpisodeRow(CollectionEntry entry, string identity, bool alreadyAdded)
    {
        public CollectionEntry Entry { get; } = entry;
        public string Identity { get; } = identity;
        public bool AlreadyAdded { get; set; } = alreadyAdded;
        public string Title => SafeTitle(Entry.Title, Entry.Url);

        public override string ToString() => AlreadyAdded ? Title + "  ·  Already added" : Title;
    }

    private readonly DownloadEngine _engine;
    private readonly PodcastFeedStore _store;
    private readonly DefaultMediaResolver _resolver;
    private readonly VesperPreferences _preferences;
    private readonly VesperPalette _colors;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBox _feedUrl;
    private readonly ListBox _feeds;
    private readonly CheckedListBox _episodes;
    private readonly Label _status;
    private readonly VesperButton _addFeed;
    private readonly VesperButton _refresh;
    private readonly VesperButton _removeFeed;
    private readonly VesperButton _queue;
    private readonly VesperButton _close;
    private bool _busy;

    public PodcastFollowForm(DownloadEngine engine, VesperPreferences preferences)
    {
        _engine = engine;
        _preferences = preferences.Clone();
        _colors = UiKit.Palette(_preferences);
        _store = PodcastFeedStore.Load(Path.Combine(EngineHost.DataDirectory, "podcast-feeds.json"));
        _resolver = new DefaultMediaResolver(EngineHost.MediaNetwork());

        VesperWindowChrome.Brand(this);
        Text = "Podcast feeds — VESPER";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(790, 640);
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        BackColor = _colors.Surface;
        ForeColor = _colors.Text;

        var body = new Panel { Dock = DockStyle.Fill, BackColor = _colors.Surface };
        var intro = Label("Follow a podcast", 12, bold: true);
        intro.SetBounds(23, 13, 720, 27);
        var explanation = Label("Add a public RSS or Atom feed, then refresh it when you want to check for episodes. VESPER does not poll feeds in the background.", 8.5F, muted: true);
        explanation.SetBounds(23, 43, 740, 32);
        explanation.AutoEllipsis = false;

        var urlFrame = new VesperInputFrame
        {
            Location = new Point(23, 82), Size = new Size(584, 38),
            BackColor = _colors.Surface, BorderColor = _colors.Border, FocusColor = _colors.MutedGreen
        };
        _feedUrl = new TextBox
        {
            Name = "podcastFeedUrl", AccessibleName = "Podcast feed URL",
            PlaceholderText = "Paste a public RSS or Atom feed link", BorderStyle = BorderStyle.None,
            Location = new Point(10, 10), Width = 560, BackColor = _colors.Surface, ForeColor = _colors.Text
        };
        _feedUrl.GotFocus += (_, _) => { urlFrame.Active = true; urlFrame.Invalidate(); };
        _feedUrl.LostFocus += (_, _) => { urlFrame.Active = false; urlFrame.Invalidate(); };
        urlFrame.Controls.Add(_feedUrl);

        _addFeed = Button("Add feed", 98, primary: true, symbol: VesperIcon.Plus);
        _addFeed.SetBounds(618, 82, 145, 38);
        _addFeed.Click += (_, _) => AddFeed();

        var feedsLabel = Label("Your feeds", 9, bold: true);
        feedsLabel.SetBounds(23, 135, 254, 22);
        var episodesLabel = Label("Episodes", 9, bold: true);
        episodesLabel.SetBounds(298, 135, 465, 22);

        _feeds = new ListBox
        {
            Name = "podcastFeeds", AccessibleName = "Followed podcast feeds",
            Location = new Point(23, 160), Size = new Size(255, 274),
            IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle,
            BackColor = _colors.Surface, ForeColor = _colors.Text, Font = new Font("Segoe UI", 9F)
        };
        _feeds.SelectedIndexChanged += (_, _) => FeedSelectionChanged();

        _episodes = new CheckedListBox
        {
            Name = "podcastEpisodes", AccessibleName = "Podcast episodes",
            AccessibleDescription = "Select new episodes to add to the download queue.",
            Location = new Point(298, 160), Size = new Size(465, 274),
            CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle,
            BackColor = _colors.Surface, ForeColor = _colors.Text, Font = new Font("Segoe UI", 9F),
            HorizontalScrollbar = true
        };
        _episodes.ItemCheck += (_, e) =>
        {
            if (e.NewValue != CheckState.Unchecked && e.Index < _episodes.Items.Count
                && !CanSelectEpisode(_episodes.Items[e.Index]))
                e.NewValue = CheckState.Unchecked;
            if (IsHandleCreated) BeginInvoke(new Action(UpdateButtons));
        };

        _refresh = Button("Refresh episodes", 154, symbol: VesperIcon.Refresh);
        _refresh.SetBounds(23, 445, 154, 34);
        _refresh.Click += async (_, _) => await RefreshEpisodesAsync();
        _removeFeed = Button("Remove feed", 128, symbol: VesperIcon.RemoveList);
        _removeFeed.SetBounds(185, 445, 128, 34);
        _removeFeed.Click += (_, _) => RemoveFeed();

        _status = Label(_store.LoadProblem is { Length: > 0 } issue
            ? "Saved feeds could not be read and will not be overwritten. " + issue
            : "Choose a feed, then select Refresh episodes. No feed is checked until you ask.", 8.5F, muted: true);
        _status.SetBounds(23, 486, 740, 36);
        _status.AutoEllipsis = false;

        body.Controls.AddRange([intro, explanation, urlFrame, _addFeed, feedsLabel, episodesLabel, _feeds, _episodes, _refresh, _removeFeed, _status]);

        var footer = new VesperPanel { Dock = DockStyle.Bottom, Height = 62, BackColor = _colors.Surface };
        footer.Paint += (_, e) => { using var pen = new Pen(_colors.Divider); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        _close = Button("Close", 84);
        _close.DialogResult = DialogResult.Cancel;
        _queue = Button("Add selected to queue", 190, primary: true, symbol: VesperIcon.Queue);
        _queue.Click += (_, _) => QueueSelected();
        footer.Controls.AddRange([_close, _queue]);
        footer.Resize += (_, _) => LayoutFooter(footer);

        Controls.Add(body);
        Controls.Add(footer);
        VesperDialogChrome.Attach(this, "VESPER  /  Podcast feeds", _colors, VesperIcon.Audio);
        CancelButton = _close;
        FormClosing += (_, _) => _lifetime.Cancel();
        FormClosed += (_, _) => { _lifetime.Dispose(); _resolver.Dispose(); };

        foreach (PodcastFeedSubscription feed in _store.Feeds)
            _feeds.Items.Add(new FeedRow(feed));
        if (_feeds.Items.Count > 0) _feeds.SelectedIndex = 0;
        LayoutFooter(footer);
        UpdateButtons();
    }

    private Label Label(string text, float size, bool bold = false, bool muted = false) => new()
    {
        Text = text,
        AutoSize = false,
        AutoEllipsis = true,
        ForeColor = muted ? _colors.Muted : _colors.Text,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };

    private VesperButton Button(string text, int width, bool primary = false, VesperIcon symbol = VesperIcon.None)
    {
        var button = new VesperButton { Text = text, Width = width, Height = 34, Primary = primary, Symbol = symbol, Cursor = Cursors.Hand };
        button.ApplyPalette(_colors);
        return button;
    }

    private void AddFeed()
    {
        if (_busy) return;
        if (!_store.TryAddFeed(_feedUrl.Text, out PodcastFeedSubscription feed, out string problem))
        {
            SetStatus(problem);
            return;
        }

        bool alreadyThere = problem.Length > 0;
        ReloadFeeds(feed.Url);
        _feedUrl.Clear();
        SetStatus(alreadyThere && problem.Length > 0
            ? problem + " Select Refresh episodes when you want to check it."
            : "Feed added. Select Refresh episodes when you want to check for new items.");
    }

    private async Task RefreshEpisodesAsync()
    {
        if (_busy || _feeds.SelectedItem is not FeedRow selected) return;
        PodcastFeedSubscription feed = selected.Feed;
        SetBusy(true, "Checking this feed now…");
        _episodes.Items.Clear();
        try
        {
            _resolver.ClearCache();
            MediaDiscoveryResult result = await _resolver.ResolveAsync(new MediaDiscoveryRequest
            {
                Url = new Uri(feed.Url),
                Options = new DiscoveryOptions { CollectionIntent = CollectionIntent.CollectionOnly, MaxCollectionItems = 200 }
            }, _lifetime.Token);

            if (IsDisposed || _lifetime.IsCancellationRequested) return;
            if (result.Collection is not { Kind: CollectionKind.Feed } collection)
            {
                SetStatus(result.Error?.UserMessage ?? "That link did not return a supported podcast feed.");
                return;
            }

            _store.TryUpdateTitle(feed.Url, collection.Title, out string saveProblem);
            ReloadFeeds(feed.Url);

            int newCount = PopulateEpisodeList(_episodes, feed.Url, collection.Entries.Take(200),
                identity => _store.IsQueued(feed.Url, identity));
            string completeness = collection.IsComplete ? "" : " Showing the latest 200 items in this feed.";
            string saveNote = saveProblem.Length > 0 ? " The feed title could not be saved: " + saveProblem : "";
            string warningNote = result.Warnings.Count > 0 ? " Some feed details were unavailable." : "";
            SetStatus($"{newCount} new episode(s) found. Select the episodes you want to add; previously added episodes are marked.{completeness}{warningNote}{saveNote}");
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!IsDisposed) SetStatus("The feed could not be refreshed. Check the link or try again.");
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void QueueSelected()
    {
        if (_busy || _feeds.SelectedItem is not FeedRow selected) return;
        EpisodeRow[] rows = _episodes.CheckedItems.Cast<object>().OfType<EpisodeRow>()
            .Where(row => !row.AlreadyAdded).ToArray();
        if (rows.Length == 0)
        {
            SetStatus("Select one or more new episodes first.");
            return;
        }

        if (VesperMessageBox.Show(this,
                $"Add {rows.Length} selected episode(s) from {selected.Feed.Title} to the normal VESPER queue? They will start when a download slot is available.",
                "Add podcast episodes", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2, "Add to queue", VesperIcon.Queue) != DialogResult.Yes)
            return;

        SetBusy(true, "Adding selected episodes…");
        int added = 0, alreadyQueued = 0;
        string? destinationFolder = SiteRule.ForPage(_preferences.SiteRules, selected.Feed.Url)?.Folder;
        var savedIds = new List<string>();
        var errors = new List<string>();
        try
        {
            foreach (EpisodeRow row in rows)
            {
                try
                {
                    JobKind kind = row.Entry.Url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                        ? JobKind.Hls : JobKind.Http;
                    AddResult result = _engine.Add(new AddRequest(row.Entry.Url.AbsoluteUri,
                        DestinationFolder: destinationFolder, FileName: EpisodeFileName(row), StartNow: false,
                        RequestId: PodcastFeedStore.RequestId(selected.Feed.Url, row.Identity),
                        Kind: kind, OriginalPageUrl: selected.Feed.Url, MediaProvider: "podcast-feed",
                        MediaId: row.Identity, MediaQuality: "Original"));
                    if (result.Status is AddStatus.Added)
                    {
                        added++;
                        savedIds.Add(row.Identity);
                    }
                    else if (result.Status is AddStatus.Duplicate or AddStatus.AlreadySubmitted)
                    {
                        alreadyQueued++;
                        savedIds.Add(row.Identity);
                    }
                    else errors.Add($"{row.Title}: {result.Message}");
                }
                catch (Exception)
                {
                    errors.Add($"{row.Title}: could not be added. Check the queue and local storage.");
                }
            }

            string saveProblem = "";
            bool historySaved = savedIds.Count == 0 || _store.TryMarkQueued(selected.Feed.Url, savedIds, out saveProblem);
            MarkRowsAdded(savedIds);
            string summary = $"Added {added} episode(s) to the queue; {alreadyQueued} were already there.";
            if (!historySaved) summary += " The feed history could not be saved: " + saveProblem;
            if (errors.Count > 0)
                summary += " " + errors.Count + " episode(s) could not be added: " + string.Join("; ", errors.Take(3));
            SetStatus(summary);
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void RemoveFeed()
    {
        if (_busy || _feeds.SelectedItem is not FeedRow selected) return;
        if (VesperMessageBox.Show(this,
                $"Remove {selected.Feed.Title} from your followed feeds? Downloads already in VESPER stay in the queue.",
                "Remove podcast feed", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2, "Remove feed", VesperIcon.RemoveList) != DialogResult.Yes)
            return;

        if (!_store.TryRemoveFeed(selected.Feed.Url, out string problem))
        {
            SetStatus(problem);
            return;
        }
        ReloadFeeds(null);
        _episodes.Items.Clear();
        SetStatus("Feed removed. Existing downloads were not changed.");
    }

    private void MarkRowsAdded(IEnumerable<string> episodeIds)
    {
        var ids = episodeIds.ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < _episodes.Items.Count; i++)
        {
            if (_episodes.Items[i] is not EpisodeRow row || !ids.Contains(row.Identity)) continue;
            row.AlreadyAdded = true;
            _episodes.SetItemChecked(i, false);
        }
        _episodes.Refresh();
    }

    internal static int PopulateEpisodeList(CheckedListBox episodes, string feedUrl,
        IEnumerable<CollectionEntry> entries, Func<string, bool> isAlreadyAdded)
    {
        episodes.Items.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int newCount = 0;
        foreach (CollectionEntry entry in entries)
        {
            string identity = PodcastFeedStore.EpisodeIdentity(feedUrl, entry);
            if (!seen.Add(identity)) continue;
            bool alreadyAdded = isAlreadyAdded(identity);
            episodes.Items.Add(new EpisodeRow(entry, identity, alreadyAdded), false);
            if (!alreadyAdded) newCount++;
        }
        return newCount;
    }

    internal static bool CanSelectEpisode(object? item) => item is EpisodeRow { AlreadyAdded: false };

    private void ReloadFeeds(string? selectUrl)
    {
        _feeds.BeginUpdate();
        try
        {
            _feeds.Items.Clear();
            int select = -1;
            foreach (PodcastFeedSubscription feed in _store.Feeds)
            {
                int index = _feeds.Items.Add(new FeedRow(feed));
                if (string.Equals(feed.Url, selectUrl, StringComparison.Ordinal)) select = index;
            }
            if (select >= 0) _feeds.SelectedIndex = select;
            else if (_feeds.Items.Count > 0) _feeds.SelectedIndex = 0;
        }
        finally { _feeds.EndUpdate(); }
    }

    private void FeedSelectionChanged()
    {
        _episodes.Items.Clear();
        UpdateButtons();
        if (_feeds.SelectedItem is FeedRow feed)
            SetStatus("Choose Refresh episodes to check " + feed.Feed.Title + ". No automatic polling is used.");
        else SetStatus("Add a public RSS or Atom feed to get started.");
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        _feedUrl.Enabled = !busy;
        _feeds.Enabled = !busy;
        _episodes.Enabled = !busy;
        _addFeed.Enabled = !busy;
        _refresh.Enabled = !busy && _feeds.SelectedItem is FeedRow;
        _removeFeed.Enabled = !busy && _feeds.SelectedItem is FeedRow;
        if (status is not null) SetStatus(status);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (IsDisposed) return;
        _refresh.Enabled = !_busy && _feeds.SelectedItem is FeedRow;
        _removeFeed.Enabled = !_busy && _feeds.SelectedItem is FeedRow;
        _queue.Enabled = !_busy && _episodes.CheckedItems.Cast<object>().OfType<EpisodeRow>().Any(row => !row.AlreadyAdded);
    }

    private void SetStatus(string text)
    {
        if (IsDisposed) return;
        _status.Text = Clip(text, 260);
    }

    private static string EpisodeFileName(EpisodeRow row)
    {
        string extension = Path.GetExtension(row.Entry.Url.AbsolutePath);
        if (extension.Length > 12 || extension.Length > 0 && !extension.Skip(1).All(char.IsAsciiLetterOrDigit))
            extension = "";
        string title = row.Entry.Title?.Trim() ?? "";
        if (title.Length == 0) title = FileNames.FromUrl(row.Entry.Url, "episode");
        if (extension.Length > 0 && !title.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) title += extension;
        return FileNames.Sanitize(title, "episode");
    }

    private static string SafeTitle(string? title, Uri url)
    {
        string value = FileNames.Sanitize(title, FileNames.FromUrl(url, "Untitled episode"));
        return Clip(value, 110);
    }

    private static string Clip(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        int end = maxLength - 1;
        if (end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
            end--;
        return text[..end] + "…";
    }

    private void LayoutFooter(VesperPanel footer)
    {
        int scale = IsHandleCreated ? UiKit.Scale(this, 1) : 1;
        _queue.SetBounds(footer.Width - 23 * scale - 190 * scale, 14 * scale, 190 * scale, 34 * scale);
        _close.SetBounds(23 * scale, 14 * scale, 84 * scale, 34 * scale);
    }
}
