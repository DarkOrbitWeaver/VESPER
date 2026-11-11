using Microsoft.Win32;
using Vesper.Engine;

namespace VESPER;

public partial class Form1 : Form
{
    private readonly Dictionary<Guid, DownloadItem> _items = [];
    private readonly VesperPreferences _prefs;
    private readonly DownloadEngine? _engine;
    private readonly DownloadActions? _actions;
    private readonly ContextMenuStrip _rowMenu = new() { ShowImageMargin = true };
    private readonly Dictionary<string, ToolStripMenuItem> _menuCommands = [];
    private readonly Dictionary<ToolStripMenuItem, VesperIcon> _menuSymbols = [];
    private readonly List<ToolStripMenuItem> _rateOptions = [];
    private readonly List<(ToolStripMenuItem Item, JobPriority Priority)> _priorityOptions = [];
    private ToolStripMenuItem _bandwidthMenu = null!;
    private ToolStripMenuItem _priorityMenu = null!;
    private readonly ToolTip _tooltips = new() { InitialDelay = 450, ReshowDelay = 100 };

    // Engine events arrive on worker threads. They are coalesced into one posted UI callback at a time.
    private readonly Dictionary<Guid, JobSnapshot> _inbox = [];
    private readonly object _inboxLock = new();
    private bool _inboxQueued;

    private NotifyIcon? _tray;
    private BridgeServer? _bridge;
    private DownloadItem? _menuTarget;
    private VesperPalette _colors = VesperPalette.Light;
    private Image? _moth;
    private Image? _branchLight;
    private Image? _branchDark;
    private readonly bool _startHidden;
    private bool _allowVisible;
    private bool _exiting;
    private bool _trayHintShown;
    private string? _lastClipboard;
    private string? _clipboardCandidate;
    private DateTime _lastAvailabilityCheck = DateTime.MinValue;

    /// <summary>Visual Studio designer entry point. Never starts an engine.</summary>
    public Form1() : this(new VesperPreferences(), null, false) { }

    internal Form1(VesperPreferences preferences, DownloadEngine? engine, bool startHidden)
    {
        _prefs = preferences;
        _engine = engine;
        _startHidden = startHidden;
        _allowVisible = !startHidden;
        InitializeComponent();
        HandleCreated += (_, _) => VesperWindowChrome.Apply(this, UiKit.IsDark(_prefs));

        VesperWindowChrome.Brand(this);
        _moth = UiKit.LoadImage("moth-mark-transparent-96.png");
        _branchLight = UiKit.LoadImage("header-branch-light-720x288.png");
        _branchDark = UiKit.LoadImage("header-branch-dark-720x288.png");
        _brandIcon.Image = _moth;

        if (engine is not null) _actions = new DownloadActions(this, engine, () => _prefs);
        WireEvents();
        ApplyAppearance();
        if (engine is null) return;

        AttachEngine(engine);
        RefreshList();
        if (startHidden) _ = Handle;   // the bridge and tray need a window handle even while hidden
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(_allowVisible && value);

    // ---- engine wiring --------------------------------------------------------------------------------------------

    private void AttachEngine(DownloadEngine engine)
    {
        foreach (JobSnapshot snapshot in engine.Snapshots()) _items[snapshot.Id] = DownloadItem.From(snapshot);

        engine.JobChanged += Enqueue;
        engine.Progress += batch => { foreach (JobSnapshot s in batch) Enqueue(s); };
        engine.JobRemoved += id => PostToUi(() => { _items.Remove(id); _grid.PruneChecks(_items.Keys); RefreshList(); });
        engine.ActivityChanged += active => PostToUi(() => EngineHost.HoldAwake(active && _prefs.PreventSleep));

        BuildTray();
        var handler = new BrowserBridgeHandler(this, engine, () => _prefs);
        try
        {
            _bridge = new BridgeServer(BridgePipe.NameForCurrentUser(), handler.HandleAsync);
            _bridge.Start();
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, "The browser connection could not be started: " + ex.Message + "\n\nDownloads from the VESPER window still work.",
                "Browser integration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Activated += (_, _) => OnActivatedWindow();
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { /* the window is closing */ }
    }

    private void Enqueue(JobSnapshot snapshot)
    {
        bool post;
        lock (_inboxLock)
        {
            _inbox[snapshot.Id] = snapshot;
            post = !_inboxQueued;
            _inboxQueued = true;
        }
        if (post) PostToUi(DrainInbox);
    }

    private void DrainInbox()
    {
        List<JobSnapshot> batch;
        lock (_inboxLock)
        {
            batch = [.. _inbox.Values];
            _inbox.Clear();
            _inboxQueued = false;
        }

        bool structural = false;
        foreach (JobSnapshot snapshot in batch)
        {
            if (_items.TryGetValue(snapshot.Id, out DownloadItem? item))
            {
                JobStatus before = item.Status;
                string name = item.Name;
                item.UpdateFrom(snapshot);
                structural |= before != item.Status || name != item.Name;
                Announce(before, item);
            }
            else
            {
                _items[snapshot.Id] = DownloadItem.From(snapshot);
                structural = true;
            }
        }
        if (structural) RefreshList();
        else { _grid.RefreshRows(); UpdateFooter(); }
    }

    /// <summary>Tray notifications only for real transitions the user would want to know about.</summary>
    private void Announce(JobStatus before, DownloadItem item)
    {
        if (!_prefs.NotifyOnFinish || _tray is null || before == item.Status) return;
        bool wasActive = before is JobStatus.Downloading or JobStatus.Resolving or JobStatus.Processing or JobStatus.Verifying
            or JobStatus.Waiting or JobStatus.WaitingRetry;
        if (!wasActive) return;
        switch (item.Status)
        {
            case JobStatus.Completed: _tray.ShowBalloonTip(4000, "Download complete", item.Name, ToolTipIcon.Info); break;
            case JobStatus.NeedsLink: _tray.ShowBalloonTip(5000, "Link expired", item.Name + " needs a new link. Saved data is kept.", ToolTipIcon.Warning); break;
            case JobStatus.NeedsAttention: case JobStatus.Failed: case JobStatus.Suspect:
                _tray.ShowBalloonTip(5000, "Download needs attention", item.Name, ToolTipIcon.Warning); break;
        }
    }

    // ---- window, tray and lifecycle -------------------------------------------------------------------------------

    private void BuildTray()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Open VESPER", null, (_, _) => ShowFromTray());
        menu.Items.Add("Add download…", null, (_, _) => { ShowFromTray(); AddDownload(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Pause all", null, (_, _) => _engine!.PauseAll());
        menu.Items.Add("Resume all", null, (_, _) => _engine!.ResumeAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit VESPER", null, (_, _) => ExitApplication());
        VesperMenuTheme.Apply(menu, _colors);

        _tray = new NotifyIcon { Icon = Icon, Text = "VESPER", Visible = true, ContextMenuStrip = menu };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_clipboardCandidate is { } link) { _clipboardCandidate = null; ShowFromTray(); AddDownload(link); }
            else ShowFromTray();
        };
    }

    internal void RequestShow() => PostToUi(ShowFromTray);

    private void ShowFromTray()
    {
        _allowVisible = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private bool HasActiveDownloads() => _engine is not null && _engine.Snapshots().Any(s => s.IsActive);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_engine is not null && !_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            if (_prefs.CloseToTray)
            {
                Hide();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    _tray?.ShowBalloonTip(3500, "VESPER is still running", "Downloads continue in the tray. Use the tray menu to exit.", ToolTipIcon.Info);
                }
            }
            else ExitApplication();
            return;
        }
        base.OnFormClosing(e);
    }

    /// <summary>Exits explicitly. Active downloads are never abandoned silently: the user decides.</summary>
    private void ExitApplication()
    {
        if (HasActiveDownloads())
        {
            var choice = MessageBox.Show(this,
                "Downloads are still running.\n\nExit now? Running downloads will be paused safely and resume next time VESPER starts.",
                "Exit VESPER", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (choice != DialogResult.Yes) return;
        }
        _exiting = true;
        _allowVisible = true;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _bridge?.Dispose();
        _tray?.Dispose();
        EngineHost.HoldAwake(false);
        _engine?.Dispose();                       // stops transfers and makes their progress durable
        _moth?.Dispose();
        _branchLight?.Dispose();
        _branchDark?.Dispose();
        _rowMenu.Dispose();
        _tooltips.Dispose();
        base.OnFormClosed(e);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _engine?.WakeWaiting();   // connections made before sleep are dead
    }

    private void OnActivatedWindow()
    {
        if (_engine is null) return;
        if (DateTime.UtcNow - _lastAvailabilityCheck > TimeSpan.FromSeconds(10))
        {
            _lastAvailabilityCheck = DateTime.UtcNow;
            _ = _engine.RefreshAvailabilityAsync();
        }
        if (_prefs.WatchClipboard) SuggestClipboardLink();
    }

    /// <summary>Opt-in: offers (never starts) a download when a plausible file link was copied.</summary>
    private void SuggestClipboardLink()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            string text = Clipboard.GetText().Trim();
            if (text == _lastClipboard || text.Contains('\n') || !UrlPolicy.TryParse(text, out Uri uri, out _)) return;
            _lastClipboard = text;
            string ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (ext is not (".zip" or ".rar" or ".7z" or ".iso" or ".exe" or ".msi" or ".mp4" or ".mkv" or ".webm" or ".mp3" or ".flac" or ".pdf" or ".m3u8" or ".epub")) return;
            _clipboardCandidate = text;
            _tray?.ShowBalloonTip(5000, "Link copied", "Click to download " + FileNames.FromUrl(uri) + " with VESPER.", ToolTipIcon.Info);
        }
        catch (System.Runtime.InteropServices.ExternalException) { /* another program holds the clipboard; try again next time */ }
    }

    // ---- drag and drop --------------------------------------------------------------------------------------------

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        drgevent.Effect = drgevent.Data?.GetDataPresent(DataFormats.Text) == true || drgevent.Data?.GetDataPresent(DataFormats.UnicodeText) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        base.OnDragEnter(drgevent);
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);
        string? text = drgevent.Data?.GetData(DataFormats.UnicodeText) as string ?? drgevent.Data?.GetData(DataFormats.Text) as string;
        if (!string.IsNullOrWhiteSpace(text)) BeginInvoke(() => AddDownload(text));
    }

    // ---- layout (unchanged structure) -------------------------------------------------------------------------------

    private void WireEvents()
    {
        AllowDrop = true;
        _filter.SetItems("Everything", "Downloading", "Paused", "Queued", "Completed", "Needs attention");
        _sort.SetItems("Newest first", "Name", "Size", "Status");

        _grid.CheckStateChanged += UpdateSelectedTools;
        _grid.DetailsRequested += ShowDetails;
        _grid.OpenRequested += item => { if (item.Snapshot is { State: JobState.Completed, FileMissing: false } s) _actions?.Open(s); };
        _grid.MenuRequested += (record, position) =>
        {
            _menuTarget = record;
#if DEBUG
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            _rowMenu.Show(position);
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"VESPER row menu Show(): {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
#endif
        };
        BuildMenuOnce();
        _rowMenu.Opening += ConfigureRowMenu;
        // Create the native menu handles right after the window exists, without a timer, so the first right-click is instant.
        HandleCreated += (_, _) => BeginInvoke((Action)(() =>
        {
            if (!IsDisposed) { _ = _rowMenu.Handle; _ = _bandwidthMenu.DropDown.Handle; _ = _priorityMenu.DropDown.Handle; }
        }));
        _tooltips.SetToolTip(_settings, "Settings");
        _tooltips.SetToolTip(_add, "Add download (Ctrl+N)");
        _tooltips.SetToolTip(_searchFrame, "Search downloads");
        _add.Click += (_, _) => AddDownload();
        _settings.Click += (_, _) => OpenSettings();
        _search.TextChanged += (_, _) => RefreshList();
        _filter.SelectedIndexChanged += (_, _) => RefreshList();
        _sort.SelectedIndexChanged += (_, _) => RefreshList();
        _pause.Click += (_, _) => { foreach (var s in CheckedSnapshots()) _engine?.Pause(s.Id); };
        _resume.Click += (_, _) => { foreach (var s in CheckedSnapshots()) _engine?.Resume(s.Id); };
        _remove.Click += (_, _) => _actions?.Remove(CheckedSnapshots());
        _clear.Click += (_, _) => _grid.ClearChecks();

        _mast.Paint += (_, e) => PaintMast(e.Graphics);
        _mast.Resize += (_, _) => LayoutMast();
        _normalTools.Resize += (_, _) => LayoutToolbar();
        _selectedTools.Resize += (_, _) => LayoutSelectionToolbar();
        _toolbar.Resize += (_, _) =>
        {
            LayoutToolbar();
            LayoutSelectionToolbar();
        };
        _searchFrame.Resize += (_, _) =>
        {
            _search.Width = Math.Max(40, _searchFrame.ClientSize.Width - UiKit.Scale(this, 50));
            _search.Top = Math.Max(2, (_searchFrame.Height - _search.Height) / 2);
        };
        _footer.Paint += (_, e) =>
        {
            using var pen = new Pen(_colors.Border);
            e.Graphics.DrawLine(pen, 0, 0, _footer.Width, 0);
        };

        LayoutMast();
        LayoutToolbar();
        LayoutSelectionToolbar();
    }

    private List<JobSnapshot> CheckedSnapshots() =>
        [.. _grid.CheckedItems.Where(i => i.Snapshot is not null).Select(i => i.Snapshot!)];

    private void LayoutMast()
    {
        int edge = UiKit.Scale(this, 20);
        int y = UiKit.Scale(this, 21);
        _settings.SetBounds(_mast.ClientSize.Width - edge - _settings.Width, y, _settings.Width, _settings.Height);
        _add.Location = new Point(_settings.Left - UiKit.Scale(this, 12) - _add.Width, y);
    }

    private void LayoutToolbar()
    {
        int width = _normalTools.ClientSize.Width;
        int margin = UiKit.Scale(this, width < UiKit.Scale(this, 940) ? 14 : 24);
        int top = UiKit.Scale(this, 12);
        int height = UiKit.Scale(this, 32);
        int gap = UiKit.Scale(this, 12);
        int filterWidth = UiKit.Scale(this, width < UiKit.Scale(this, 940) ? 135 : 148);
        int sortWidth = UiKit.Scale(this, width < UiKit.Scale(this, 940) ? 115 : 124);
        bool showCaptions = width >= UiKit.Scale(this, 975);

        _showLabel.Visible = _sortLabel.Visible = showCaptions;
        int sortX = width - margin - sortWidth;
        _sort.SetBounds(sortX, top, sortWidth, height);
        _sortLabel.Location = new Point(sortX - UiKit.Scale(this, 8) - _sortLabel.Width,
            top + (height - _sortLabel.Height) / 2);

        int filterX = (showCaptions ? _sortLabel.Left : sortX) - gap - filterWidth;
        _filter.SetBounds(filterX, top, filterWidth, height);
        _showLabel.Location = new Point(filterX - UiKit.Scale(this, 8) - _showLabel.Width,
            top + (height - _showLabel.Height) / 2);

        int end = showCaptions ? _showLabel.Left : filterX;
        int available = Math.Max(UiKit.Scale(this, 110), end - margin - UiKit.Scale(this, 18));
        int preferredMax = UiKit.Scale(this, width < UiKit.Scale(this, 900) ? 350 : 413);
        _searchFrame.SetBounds(margin, top, Math.Min(available, preferredMax), height);
    }

    private void LayoutSelectionToolbar()
    {
        int top = UiKit.Scale(this, 12);
        int height = UiKit.Scale(this, 32);
        int gap = UiKit.Scale(this, 9);
        int x = UiKit.Scale(this, 24);
        _selectedLabel.Location = new Point(x, top + (height - _selectedLabel.Height) / 2);
        x += TextRenderer.MeasureText(_selectedLabel.Text, _selectedLabel.Font).Width + UiKit.Scale(this, 22);

        // Text and an icon need room; fixed 85px buttons truncated Resume and Remove.
        foreach (var button in new[] { _pause, _resume, _remove })
        {
            int width = Math.Max(UiKit.Scale(this, 100),
                TextRenderer.MeasureText(button.Text, button.Font).Width + UiKit.Scale(this, 53));
            button.SetBounds(x, top, width, height);
            x += width + gap;
        }
        int clearWidth = Math.Max(UiKit.Scale(this, 142),
            TextRenderer.MeasureText(_clear.Text, _clear.Font).Width + UiKit.Scale(this, 48));
        _clear.SetBounds(_selectedTools.Width - UiKit.Scale(this, 24) - clearWidth,
            top, clearWidth, height);
    }

    private void PaintMast(Graphics graphics)
    {
        graphics.Clear(_colors.Surface);
        if (_prefs.Botanical && !SystemInformation.HighContrast)
        {
            Image? branch = UiKit.IsDark(_prefs) ? _branchDark : _branchLight;
            if (branch is not null)
            {
                var destination = new Rectangle(Math.Max(245, _mast.Width - 405), -28, 265, 125);
                using var attributes = new System.Drawing.Imaging.ImageAttributes();
                var tint = new System.Drawing.Imaging.ColorMatrix { Matrix33 = UiKit.IsDark(_prefs) ? 0.42f : 0.48f };
                attributes.SetColorMatrix(tint);
                graphics.DrawImage(branch, destination, 0, 0, branch.Width, branch.Height, GraphicsUnit.Pixel, attributes);
            }
        }
        using var border = new Pen(_colors.Border);
        graphics.DrawLine(border, 0, _mast.Height - 1, _mast.Width, _mast.Height - 1);
    }

    private void ApplyAppearance()
    {
        _colors = UiKit.Palette(_prefs);
        VesperWindowChrome.Apply(this, UiKit.IsDark(_prefs));
        BackColor = _colors.Back;
        ForeColor = _colors.Text;
        _mast.BackColor = _colors.Surface;
        _toolbar.BackColor = _colors.Back;
        _normalTools.BackColor = _colors.Back;
        _selectedTools.BackColor = UiKit.IsDark(_prefs) ? Color.FromArgb(37, 63, 45) : Color.FromArgb(233, 241, 232);
        _footer.BackColor = _colors.Secondary;
        _footerLabel.ForeColor = _colors.Muted;
        _brandText.ForeColor = _colors.Text;
        _brandCaption.ForeColor = _colors.Muted;
        _brandIcon.BackColor = _colors.Surface;
        _selectedLabel.ForeColor = _colors.Text;

        foreach (var button in new[] { _add, _settings, _pause, _resume, _remove, _clear })
            button.ApplyPalette(_colors);
        _searchFrame.BackColor = _colors.Surface;
        _searchFrame.OutlineColor = _colors.Border;
        _searchFrame.SearchIconColor = _colors.Muted;
        _search.BackColor = _colors.Surface;
        _search.ForeColor = _colors.Text;
        _showLabel.ForeColor = _sortLabel.ForeColor = _colors.Muted;
        _filter.ApplyPalette(_colors);
        _sort.ApplyPalette(_colors);
        _grid.ApplyAppearance(_colors, _prefs.Density);
        VesperMenuTheme.Apply(_rowMenu, _colors, withIcons: true);
        RefreshMenuStyle();
        _mast.Invalidate();
        _searchFrame.Invalidate();
        _footer.Invalidate();
    }

    // ---- list -----------------------------------------------------------------------------------------------------

    private void RefreshList()
    {
        IEnumerable<DownloadItem> records = _items.Values;
        if (_prefs.HideMissingCompleted) records = records.Where(r => r.Status != JobStatus.Missing);

        string term = _search.Text.Trim();
        if (term.Length > 0)
            records = records.Where(r => r.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.Extension.Contains(term, StringComparison.OrdinalIgnoreCase));

        records = _filter.SelectedText switch
        {
            "Downloading" => records.Where(r => r.Status is JobStatus.Downloading or JobStatus.Resolving or JobStatus.Processing),
            "Paused" => records.Where(r => r.Status == JobStatus.Paused),
            "Queued" => records.Where(r => r.Status is JobStatus.Queued or JobStatus.Waiting or JobStatus.WaitingRetry),
            "Completed" => records.Where(r => r.Status is JobStatus.Completed or JobStatus.Missing),
            "Needs attention" => records.Where(r => r.Status is JobStatus.NeedsLink or JobStatus.Missing or JobStatus.Suspect
                or JobStatus.NeedsAttention or JobStatus.Failed),
            _ => records
        };
        records = _sort.SelectedText switch
        {
            "Name" => records.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            "Size" => records.OrderByDescending(r => r.SizeBytes),
            "Status" => records.OrderBy(r => r.Status).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            _ => records.OrderByDescending(r => r.CreatedUtc)
        };

        _grid.EmptyTitle = _items.Count == 0 ? "No downloads yet" : "No matching downloads";
        _grid.EmptyHint = _items.Count == 0 ? "Add a link to start downloading, or send one from your browser with the VESPER extension." : "";
        _grid.SetItems(records.ToList());
        UpdateFooter();
        UpdateSelectedTools();
    }

    private void UpdateFooter()
    {
        int active = _items.Values.Count(r => r.Status is JobStatus.Downloading or JobStatus.Resolving);
        int queued = _items.Values.Count(r => r.Status is JobStatus.Queued or JobStatus.Waiting or JobStatus.WaitingRetry);
        int attention = _items.Values.Count(r => r.Status is JobStatus.NeedsLink or JobStatus.NeedsAttention or JobStatus.Failed or JobStatus.Suspect);
        double speed = _items.Values.Where(r => r.Snapshot is { State: JobState.Downloading }).Sum(r => r.Snapshot!.SpeedBytesPerSecond);

        var parts = new List<string>();
        if (active > 0) parts.Add($"{active} downloading");
        if (queued > 0) parts.Add($"{queued} queued");
        if (attention > 0) parts.Add($"{attention} need attention");
        if (parts.Count == 0 && _items.Count > 0) parts.Add($"{_items.Count} download{(_items.Count == 1 ? "" : "s")}");
        if (speed > 0) parts.Add("↓ " + Formatting.Rate(speed));
        string text = string.Join("    |    ", parts);
        if (_footerLabel.Text != text) _footerLabel.Text = text;
    }

    private void UpdateSelectedTools()
    {
        int count = _grid.CheckedCount;
        _selectedTools.Visible = count > 0;
        _normalTools.Visible = count == 0;
        if (count > 0) _selectedTools.BringToFront();
        else _normalTools.BringToFront();

        _selectedLabel.Text = $"{count} selected";
        var selected = _grid.CheckedItems;
        _pause.Enabled = selected.Any(r => r.Status is JobStatus.Downloading or JobStatus.Queued or JobStatus.Resolving or JobStatus.WaitingRetry or JobStatus.Waiting);
        _resume.Enabled = selected.Any(r => r.Status is JobStatus.Paused or JobStatus.Failed or JobStatus.NeedsAttention);
        _remove.Enabled = count > 0;
        LayoutSelectionToolbar();
    }

    // ---- commands -------------------------------------------------------------------------------------------------

    private void ShowDetails(DownloadItem record)
    {
        if (_engine is null || _actions is null || record.Snapshot is null) return;
        JobSnapshot current = _engine.Snapshot(record.Id) ?? record.Snapshot;
        using var dialog = new DownloadDetailsForm(_engine, _actions, current, _prefs);
        dialog.ShowDialog(this);
    }

    private void AddDownload(string? initialText = null)
    {
        if (_engine is null) return;
        using var dialog = new AddDownloadForm(_prefs, initialText);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // The dialog only passes a folder when it differs from the default, so category subfolders still apply.
        string? folder = string.Equals(dialog.Destination, _prefs.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? null : dialog.Destination;
        var problems = new List<string>();
        foreach (MediaDownloadChoice media in dialog.MediaJobs)
        {
            AddResult result = _engine.Add(new AddRequest(media.Url, folder, media.FileName,
                StartNow: !dialog.QueueOnly, Referer: media.Referer, UserAgent: media.UserAgent,
                Kind: media.Kind, OriginalPageUrl: media.OriginalPageUrl,
                MediaProvider: media.Provider, MediaId: media.MediaId,
                MediaQuality: media.Quality, MediaThumbnailUrl: media.ThumbnailUrl,
                MediaExpiresAt: media.ExpiresAt, MediaPlanJson: media.MediaPlanJson, MediaFormatId: media.FormatId));
            if (!result.Ok) problems.Add($"{UrlPolicy.Redact(media.Url)}\n   {result.Message}");
        }
        foreach (string url in dialog.DownloadUrls)
        {
            bool hls = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
            AddResult result = _engine.Add(new AddRequest(url, folder, StartNow: !dialog.QueueOnly, Kind: hls ? JobKind.Hls : JobKind.Http));
            if (!result.Ok) problems.Add($"{UrlPolicy.Redact(url)}\n   {result.Message}");
        }
        if (problems.Count > 0)
            MessageBox.Show(this, "Some links were not added:\n\n" + string.Join("\n\n", problems), "Add download", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenSettings()
    {
        if (_engine is null) return;
        using var dialog = new SettingsForm(_prefs);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        VesperPreferences updated = dialog.Result;
        bool restartNeeded = updated.AllowPrivateNetworks != _prefs.AllowPrivateNetworks
            || updated.ProxyMode != _prefs.ProxyMode || updated.ProxyAddress != _prefs.ProxyAddress;
        CopyPreferences(updated, _prefs);

        BuildRateOptions();
        if (SettingsStore.Save(_prefs) is { } saveError)
            MessageBox.Show(this, "Settings could not be saved and will be lost when VESPER closes:\n" + saveError, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        EngineHost.Apply(_engine, _prefs);        // running downloads keep running; limits and queue rules update
        ApplyAppearance();
        RefreshList();
        if (restartNeeded)
            MessageBox.Show(this, "Network access and proxy changes apply the next time VESPER starts.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void CopyPreferences(VesperPreferences from, VesperPreferences to)
    {
        to.Theme = from.Theme; to.Density = from.Density; to.Motion = from.Motion; to.Botanical = from.Botanical;
        to.HideMissingCompleted = from.HideMissingCompleted; to.CategoryFolders = from.CategoryFolders; to.DefaultFolder = from.DefaultFolder;
        to.ConcurrentTransfers = from.ConcurrentTransfers; to.GlobalRateLimitKbs = from.GlobalRateLimitKbs;
        to.PerTransferRateLimitKbs = from.PerTransferRateLimitKbs; to.BandwidthPresetsKbs = [.. from.BandwidthPresetsKbs];
        to.MaxConnectionsPerJob = from.MaxConnectionsPerJob; to.NotifyOnFinish = from.NotifyOnFinish; to.WatchClipboard = from.WatchClipboard;
        to.PreventSleep = from.PreventSleep; to.CloseToTray = from.CloseToTray; to.AllowPrivateNetworks = from.AllowPrivateNetworks;
        to.ProxyMode = from.ProxyMode; to.ProxyAddress = from.ProxyAddress; to.ScheduleEnabled = from.ScheduleEnabled;
        to.ScheduleStart = from.ScheduleStart; to.ScheduleEnd = from.ScheduleEnd; to.BrowserAction = from.BrowserAction;
    }

    // ---- row context menu: built once, reused, only availability changes per right-click -------------------------------------

    private void BuildMenuOnce()
    {
        void Add(string text, VesperIcon icon, Action<JobSnapshot> action)
        {
            var command = new ToolStripMenuItem(text);
            command.Click += (_, _) =>
            {
                if (_menuTarget?.Snapshot is { } snapshot) action(snapshot);
            };
            _menuCommands.Add(text, command);
            _menuSymbols.Add(command, icon);
            _rowMenu.Items.Add(command);
        }

        Add("Details", VesperIcon.Info, s => { if (_menuTarget is { } t) ShowDetails(t); });
        Add("Open file", VesperIcon.File, s => _actions?.Open(s));
        Add("Open containing folder", VesperIcon.Folder, s => _actions?.ShowFolder(s));
        Add("Locate file…", VesperIcon.Folder, s => _actions?.Locate(s));
        Add("Pause", VesperIcon.Pause, s => _engine?.Pause(s.Id));
        Add("Resume", VesperIcon.Play, s => _engine?.Resume(s.Id));
        Add("Start now", VesperIcon.Play, s => _engine?.StartNow(s.Id));
        Add("Replace link…", VesperIcon.Link, s => _actions?.ReplaceLink(s));
        Add("Resume anyway…", VesperIcon.Play, s => _actions?.ApproveUnverified(s));
        Add("Start over…", VesperIcon.Refresh, s => _actions?.StartOver(s));

        _bandwidthMenu = new ToolStripMenuItem("Bandwidth · Unlimited");
        _menuSymbols.Add(_bandwidthMenu, VesperIcon.Gauge);
        _rowMenu.Items.Add(_bandwidthMenu);

        _priorityMenu = new ToolStripMenuItem("Priority · Normal");
        _menuSymbols.Add(_priorityMenu, VesperIcon.Queue);
        _rowMenu.Items.Add(_priorityMenu);
        foreach ((string label, JobPriority priority) in new[] { ("High", JobPriority.High), ("Normal", JobPriority.Normal), ("Low", JobPriority.Low) })
        {
            var option = new ToolStripMenuItem(label);
            option.Click += (_, _) => { if (_menuTarget?.Snapshot is { } s) _engine?.SetPriority(s.Id, priority); };
            _priorityOptions.Add((option, priority));
            _priorityMenu.DropDownItems.Add(option);
        }
        _priorityMenu.DropDownItems.Add(new ToolStripSeparator());
        var top = new ToolStripMenuItem("Move to top of queue");
        top.Click += (_, _) => { if (_menuTarget?.Snapshot is { } s) _engine?.MoveInQueue(s.Id, QueueMove.Top); };
        _priorityMenu.DropDownItems.Add(top);

        Add("Verify integrity…", VesperIcon.Check, s => _actions?.Verify(s));
        Add("Copy source URL", VesperIcon.Clipboard, s => _actions?.CopyUrl(s));
        Add("Rename entry…", VesperIcon.Pencil, s => _actions?.Rename(s));
        _rowMenu.Items.Add(new ToolStripSeparator());
        Add("Remove from list…", VesperIcon.Trash, s => _actions?.Remove([s]));
        Add("Delete downloaded file…", VesperIcon.Trash, s => _actions?.DeleteFile(s));
        BuildRateOptions();
        VesperMenuTheme.Apply(_rowMenu, _colors, withIcons: true);
        RefreshMenuStyle();
    }

    private void BuildRateOptions()
    {
        ToolStripItem[] previous = _bandwidthMenu.DropDownItems.Cast<ToolStripItem>().ToArray();
        foreach (ToolStripItem item in previous)
            if (item is ToolStripMenuItem command) _menuSymbols.Remove(command);
        _bandwidthMenu.DropDownItems.Clear();
        foreach (ToolStripItem item in previous) item.Dispose();
        _rateOptions.Clear();

        void AddRate(string label, long bytesPerSecond)
        {
            var option = new ToolStripMenuItem(label);
            option.Click += (_, _) => { if (_menuTarget?.Snapshot is { } s) _engine?.SetSpeedLimit(s.Id, bytesPerSecond); };
            _rateOptions.Add(option);
            _bandwidthMenu.DropDownItems.Add(option);
        }
        AddRate("Unlimited", 0);
        foreach (int kb in _prefs.BandwidthPresetsKbs.Distinct().Order())
            AddRate(kb % 1000 == 0 ? $"{kb / 1000} MB/s" : $"{kb} KB/s", kb * 1000L);
        _bandwidthMenu.DropDownItems.Add(new ToolStripSeparator());
        var custom = new ToolStripMenuItem("Custom limit…");
        custom.Click += (_, _) => { if (_menuTarget?.Snapshot is { } s) _actions?.CustomLimit(s); };
        _menuSymbols[custom] = VesperIcon.Pencil;
        _bandwidthMenu.DropDownItems.Add(custom);
        RefreshMenuStyle();
    }

    private void RefreshMenuStyle()
    {
        foreach (var (item, symbol) in _menuSymbols)
        {
            if (item.IsDisposed) continue;
            item.Image = VesperMenuTheme.IconImage(symbol, _colors, _rowMenu.DeviceDpi);
            item.ImageScaling = ToolStripItemImageScaling.None;
        }
        VesperMenuTheme.FormatItems(_rowMenu.Items, _colors, itemWidth: 226, dpi: _rowMenu.DeviceDpi);
    }

    /// <summary>Only toggles items whose availability actually changed; toggling all of them forced repeated menu layout.</summary>
    private void ConfigureRowMenu(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_menuTarget?.Snapshot is not { } s) { e.Cancel = true; return; }

        bool fileAvailable = s.State == JobState.Completed && !s.FileMissing;
        bool unfinished = s.State is not (JobState.Completed or JobState.Cancelled);
        foreach (var (name, command) in _menuCommands)
        {
            bool available = name switch
            {
                "Details" or "Copy source URL" or "Rename entry…" or "Remove from list…" => true,
                "Open file" or "Open containing folder" or "Verify integrity…" or "Delete downloaded file…" =>
                    fileAvailable || (name == "Open containing folder" && s.State == JobState.Suspect),
                "Locate file…" => s.State == JobState.Completed && s.FileMissing,
                "Pause" => s.State is JobState.Resolving or JobState.Downloading or JobState.Queued or JobState.WaitingForNetwork or JobState.WaitingForRetry,
                "Resume" => s.State is JobState.Paused or JobState.Failed or JobState.NeedsAttention or JobState.Cancelled,
                "Start now" => s.State is JobState.Queued or JobState.Paused or JobState.WaitingForRetry or JobState.WaitingForNetwork,
                "Replace link…" => s.State is JobState.Paused or JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed or JobState.Queued or JobState.WaitingForRetry,
                "Resume anyway…" => s.State == JobState.NeedsAttention && s.Reason == "no-validator",
                "Start over…" => s.CommittedBytes > 0 && s.State is JobState.NeedsAttention or JobState.Failed or JobState.Suspect or JobState.Paused or JobState.NeedsLink,
                _ => false
            };
            if (command.Available != available) command.Available = available;
        }

        bool tunable = unfinished && s.State is not (JobState.Suspect or JobState.Finalizing or JobState.Verifying);
        if (_bandwidthMenu.Available != tunable) _bandwidthMenu.Available = tunable;
        if (_priorityMenu.Available != tunable) _priorityMenu.Available = tunable;

        string rateLabel = $"Bandwidth · {_menuTarget.RateCap}";
        if (_bandwidthMenu.Text != rateLabel) _bandwidthMenu.Text = rateLabel;
        foreach (var option in _rateOptions)
        {
            bool selected = option.Text == _menuTarget.RateCap;
            if (option.Checked != selected) option.Checked = selected;
        }
        string priorityLabel = $"Priority · {s.Priority}";
        if (_priorityMenu.Text != priorityLabel) _priorityMenu.Text = priorityLabel;
        foreach (var (item, priority) in _priorityOptions)
        {
            bool selected = priority == s.Priority;
            if (item.Checked != selected) item.Checked = selected;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keys)
    {
        if (keys == (Keys.Control | Keys.N)) { AddDownload(); return true; }
        if (keys == (Keys.Control | Keys.F)) { _grid.ClearChecks(); _search.Focus(); return true; }
        if (_grid.ContainsFocus && keys == (Keys.Control | Keys.A)) { _grid.CheckAllVisible(); return true; }
        if (_grid.ContainsFocus && keys == Keys.Escape) { _grid.ClearChecks(); return true; }
        return base.ProcessCmdKey(ref msg, keys);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _grid.ApplyAppearance(_colors, _prefs.Density);
        VesperMenuTheme.Apply(_rowMenu, _colors, withIcons: true);
        RefreshMenuStyle();
        LayoutMast();
        LayoutToolbar();
        LayoutSelectionToolbar();
    }
}
