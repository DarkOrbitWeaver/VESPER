using Microsoft.Win32;
using Vesper.Engine;

namespace VESPER;

public partial class Form1 : Form
{
    internal enum SleepPromptResult { Skipped, Slept, Failed }

    internal sealed class QueueSleepPrompt : VesperPopupForm
    {
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
        private readonly Label _countdown;
        private readonly VesperButton _sleep;
        private readonly VesperButton _cancel;
        private readonly Action<QueueSleepPrompt, bool> _completed;
        private int _secondsRemaining;
        private bool _finished;
        private bool _approved;

        internal QueueSleepPrompt(VesperPalette colors, int countdownSeconds, Action<QueueSleepPrompt, bool> completed)
        {
            if (countdownSeconds < 1) throw new ArgumentOutOfRangeException(nameof(countdownSeconds));
            _secondsRemaining = countdownSeconds;
            _completed = completed;
            VesperWindowChrome.Brand(this);
            Text = "Sleep when the queue finishes — VESPER";
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(500, 300 + VesperDialogChrome.HeaderHeight);
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            BackColor = colors.Surface;
            ForeColor = colors.Text;
            AccessibleRole = AccessibleRole.Dialog;

            var body = new Panel { Dock = DockStyle.Fill, BackColor = colors.Surface };
            var heading = SleepLabel("The download queue is finished", 12, colors, bold: true);
            heading.SetBounds(24, 20, 450, 30);
            var explanation = SleepLabel($"VESPER will put this PC to sleep only if you choose Sleep now. The prompt closes after {countdownSeconds} seconds without sleeping.", 9, colors);
            explanation.SetBounds(24, 62, 450, 54);
            explanation.AutoEllipsis = false;
            _countdown = SleepLabel($"This prompt cancels in {countdownSeconds} seconds.", 9, colors, bold: true);
            _countdown.Name = "queueSleepCountdown";
            _countdown.AccessibleName = "Sleep prompt countdown";
            _countdown.SetBounds(24, 128, 450, 25);
            AccessibleName = "Sleep prompt. " + _countdown.Text;
            var cancellationNote = SleepLabel("New downloads or exiting VESPER cancels this prompt.", 8.5F, colors, muted: true);
            cancellationNote.SetBounds(24, 160, 450, 25);
            body.Controls.AddRange([heading, explanation, _countdown, cancellationNote]);

            var footer = new VesperPanel { Dock = DockStyle.Bottom, Height = 62, BackColor = colors.Surface };
            footer.Paint += (_, e) =>
            {
                using var pen = new Pen(colors.Divider);
                e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            _sleep = SleepButton("Sleep now", 116, colors, primary: true, symbol: VesperIcon.Clock);
            _sleep.Click += (_, _) => Finish(approved: true);
            _cancel = SleepButton("Cancel", 88, colors);
            _cancel.Click += (_, _) => Finish(approved: false);
            footer.Controls.AddRange([_sleep, _cancel]);
            footer.Resize += (_, _) => LayoutFooter(footer);

            Controls.Add(body);
            Controls.Add(footer);
            VesperDialogChrome.Attach(this, "VESPER  /  Queue finished", colors, VesperIcon.Clock);
            StartPosition = FormStartPosition.CenterScreen;
            CancelButton = _cancel;
            FormClosing += (_, _) =>
            {
                if (_finished) return;
                _finished = true;
                _approved = false;
                _timer.Stop();
            };
            FormClosed += (_, _) =>
            {
                _timer.Stop();
                _timer.Dispose();
                _completed(this, _approved);
            };
            _timer.Tick += (_, _) =>
            {
                _secondsRemaining--;
                if (_secondsRemaining <= 0)
                {
                    Finish(approved: false);
                    return;
                }
                _countdown.Text = $"This prompt cancels in {_secondsRemaining} seconds.";
                AccessibleName = "Sleep prompt. " + _countdown.Text;
            };
            Shown += (_, _) => _timer.Start();
            LayoutFooter(footer);
        }

        internal void Cancel() => Finish(approved: false);

        private void Finish(bool approved)
        {
            if (_finished) return;
            _finished = true;
            _approved = approved;
            _timer.Stop();
            Close();
        }

        private static Label SleepLabel(string text, float size, VesperPalette colors, bool bold = false, bool muted = false) => new()
        {
            Text = text, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = muted ? colors.Muted : colors.Text,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
        };

        private static VesperButton SleepButton(string text, int width, VesperPalette colors,
            bool primary = false, VesperIcon symbol = VesperIcon.None)
        {
            var button = new VesperButton { Text = text, Width = width, Height = 34, Primary = primary, Symbol = symbol };
            button.ApplyPalette(colors);
            return button;
        }

        private void LayoutFooter(VesperPanel footer)
        {
            int scale = IsHandleCreated ? UiKit.Scale(this, 1) : 1;
            _sleep.SetBounds(footer.Width - 23 * scale - 116 * scale, 14 * scale, 116 * scale, 34 * scale);
            _cancel.SetBounds(_sleep.Left - 8 * scale - 88 * scale, 14 * scale, 88 * scale, 34 * scale);
        }
    }

    internal VesperPalette CurrentPalette => _colors;
    private readonly Dictionary<Guid, DownloadItem> _items = [];
    private readonly Dictionary<string, DownloadItem> _browserItems = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recentBrowserCompletions = new(StringComparer.Ordinal);
    private readonly VesperPreferences _prefs;
    private readonly DownloadEngine? _engine;
    private readonly DownloadActions? _actions;
    private readonly Func<bool> _requestSleep;
    private readonly ContextMenuStrip _rowMenu = new() { ShowImageMargin = true };
    private readonly ContextMenuStrip _listMenu = new() { ShowImageMargin = true };
    private readonly VesperButton _listButton = new()
    {
        Name = "downloadListActionsButton",
        Text = "List",
        Symbol = VesperIcon.Queue,
        TrailingChevron = true,
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
        Size = new Size(86, 34)
    };
    private readonly Dictionary<string, ToolStripMenuItem> _menuCommands = [];
    private readonly Dictionary<ToolStripMenuItem, VesperIcon> _menuSymbols = [];
    private readonly List<ToolStripMenuItem> _rateOptions = [];
    private readonly List<(ToolStripMenuItem Item, JobPriority Priority)> _priorityOptions = [];
    private ToolStripMenuItem _bandwidthMenu = null!;
    private ToolStripMenuItem _priorityMenu = null!;
    private readonly ToolTip _tooltips = new() { InitialDelay = 450, ReshowDelay = 100 };
    private readonly System.Windows.Forms.Timer _diskForecastTimer = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer _announcementTimer = new() { Interval = 1_000 };
    private Dictionary<string, long?> _availableDiskSpace = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastDiskProbeUtc = DateTime.MinValue;
    private bool _diskProbeRunning;

    // Engine events arrive on worker threads. They are coalesced into one posted UI callback at a time.
    private readonly Dictionary<Guid, JobSnapshot> _inbox = [];
    private readonly object _inboxLock = new();
    private bool _inboxQueued;

    private NotifyIcon? _tray;
    private VesperNotice? _notice;
    private Rectangle _noticeWorkArea;
    private BridgeServer? _bridge;
    private BrowserBridgeHandler? _browserHandler;
    private DownloadItem? _menuTarget;
    private VesperPalette _colors = VesperPalette.Light;

    private readonly bool _startHidden;
    private BotanicalArt? _botanicalArt;
    private bool _allowVisible;
    private bool _exiting;
    private QueueSleepPrompt? _sleepPrompt;
    private bool _unfinishedWorkArrived;
    private string? _lastClipboard;
    private DateTime _lastAvailabilityCheck = DateTime.MinValue;

    /// <summary>Visual Studio designer entry point. Never starts an engine.</summary>
    public Form1() : this(new VesperPreferences(), null, false) { }

    internal Form1(VesperPreferences preferences, DownloadEngine? engine, bool startHidden, bool botanicalPreview = false,
        Func<bool>? requestSleep = null)
    {
        _prefs = preferences;
        _requestSleep = requestSleep ?? EngineHost.SleepComputer;
        Disposed += (_, _) =>
        {
            CancelQueueSleepPrompt();
            _browserHandler?.CloseBrowserActivity();
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _botanicalArt?.Dispose();
            _rowMenu.Dispose();
            _listMenu.Dispose();
            _tooltips.Dispose();
            _diskForecastTimer.Dispose();
            _announcementTimer.Dispose();
        };
        _engine = engine;
        _startHidden = startHidden;
        _allowVisible = !startHidden;
        InitializeComponent();
        _listButton.AccessibleName = "Download lists and podcast feeds";
        _listButton.TabIndex = 1;
        _settings.TabIndex = 2;
        _listButton.Visible = engine is not null;
        _mast.Controls.Add(_listButton);
        _listButton.BringToFront();
        _footerLabel.AutoEllipsis = true;
        _footerLabel.AccessibleName = "Download and storage status";
        _diskForecastTimer.Tick += (_, _) => _ = RefreshDiskSpaceAsync();
        _announcementTimer.Tick += (_, _) => FlushScreenReaderAnnouncement();
        Move += (_, _) => RememberNoticeMonitor();
        FormWindowState previousState = WindowState;
        Resize += (_, _) =>
        {
            RememberNoticeMonitor();
            if (previousState != WindowState) _browserHandler?.WakeBrowserActivity();
            previousState = WindowState;
        };
        VisibleChanged += (_, _) => { RememberNoticeMonitor(); _browserHandler?.WakeBrowserActivity(); };
        HandleCreated += (_, _) =>
        {
            VesperWindowChrome.Apply(this, _colors);
            // Engine updates can arrive before the handle exists. PostToUi drops them then;
            // drain the coalesced snapshot once the UI can actually accept a callback.
            bool pending;
            lock (_inboxLock) pending = _inboxQueued;
            if (pending) DrainInbox();
        };

        VesperWindowChrome.Brand(this);
        if (botanicalPreview)
        {
            _settings.Text = "Settings";
            _settings.Width = 104;
        }
        if (engine is not null) _actions = new DownloadActions(this, engine, () => _prefs);
        WireEvents();
        ApplyAppearance();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        if (engine is null) return;

        AttachEngine(engine);
        RefreshList();
        if (startHidden) _ = Handle;   // the bridge and tray need a window handle even while hidden
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(_allowVisible && value);

    // ---- engine wiring --------------------------------------------------------------------------------------------

    private void AttachEngine(DownloadEngine engine)
    {
        foreach (JobSnapshot snapshot in engine.Snapshots())
            if (!snapshot.IsUnacceptedBrowserCapture) _items[snapshot.Id] = DownloadItem.From(snapshot);

        CodecCapabilities.Prime();
        engine.JobChanged += Enqueue;
        engine.JobChanged += snapshot => { if (MediaSourceRescue.ShouldTryAutomatically(snapshot)) _ = AutoRefreshAsync(engine, snapshot); };
        engine.Progress += batch => { foreach (JobSnapshot s in batch) Enqueue(s); };
        engine.JobRemoved += id =>
        {
            MediaSourceRescue.ForgetAutomaticAttempt(id);
            PostToUi(() => { _items.Remove(id); _grid.PruneChecks(_items.Keys); RefreshList(); });
        };
        engine.ActivityChanged += active => PostToUi(() => EngineHost.HoldAwake(active && _prefs.PreventSleep));

        BuildTray();
        _browserHandler = new BrowserBridgeHandler(this, engine, () => _prefs);
        try
        {
            _bridge = new BridgeServer(BridgePipe.NameForCurrentUser(), _browserHandler.HandleAsync);
            _bridge.Start();
        }
        catch (IOException ex)
        {
            VesperMessageBox.Show(this, "The browser connection could not be started: " + ex.Message + "\n\nDownloads from the VESPER window still work.",
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

    internal async Task<bool> ApplyBrowserActivityAsync(BrowserActivity activity, CancellationToken ct)
    {
        if (IsDisposed || !IsHandleCreated) return false;
        var applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostToUi(() =>
        {
            var current = activity.Files.Select(file => file.Id).ToHashSet(StringComparer.Ordinal);
            bool structure = RemoveBrowserRows(item => item.BrowserProfile == activity.Profile && !current.Contains(item.BrowserFile!.Id));
            var changed = new List<Guid>();
            foreach (BrowserActiveFile file in activity.Files)
            {
                if (_recentBrowserCompletions.Contains(file.Id)) continue;
                if (!_browserItems.TryGetValue(file.Id, out var row))
                {
                    row = new DownloadItem { Id = Guid.NewGuid() };
                    _items.Add(row.Id, row);
                    _browserItems.Add(file.Id, row);
                    structure = true;
                }
                else if (row.BrowserFile == file) continue;
                var previous = row.BrowserFile;
                row.UpdateFromBrowser(activity.Profile, file);
                structure |= previous is null || previous.Name != file.Name || previous.Paused != file.Paused
                    || previous.CanResume != file.CanResume || _sort.SelectedText == "Size" && previous.Length != file.Length;
                changed.Add(row.Id);
            }
            if (structure) RefreshList();
            else if (Visible && changed.Count > 0) _grid.RefreshRows(changed, static id => id);
            applied.TrySetResult(Visible && WindowState != FormWindowState.Minimized);
        });
        try { return await applied.Task.WaitAsync(TimeSpan.FromSeconds(3), ct); }
        catch (TimeoutException) { return false; }
    }

    internal void ClearBrowserActivity(string profile) => PostToUi(() =>
    {
        if (RemoveBrowserRows(item => item.BrowserProfile == profile)) RefreshList();
    });

    internal void FinishBrowserActivity(string id) => PostToUi(() =>
    {
        if (_recentBrowserCompletions.Count >= 256) _recentBrowserCompletions.Remove(_recentBrowserCompletions.First());
        _recentBrowserCompletions.Add(id);
        if (RemoveBrowserRows(item => item.BrowserFile!.Id == id)) RefreshList();
    });

    private bool RemoveBrowserRows(Func<DownloadItem, bool> predicate)
    {
        var removed = _browserItems.Values.Where(predicate).ToArray();
        foreach (var row in removed)
        {
            _browserItems.Remove(row.BrowserFile!.Id);
            _items.Remove(row.Id);
        }
        if (removed.Length > 0) _grid.PruneChecks(_items.Keys);
        return removed.Length > 0;
    }

    private void ControlBrowserDownload(DownloadItem row, string action)
    {
        if (row.BrowserFile is { } file && row.BrowserProfile is { } profile)
            _browserHandler?.SendBrowserCommand(profile, file.Id, action);
    }

    private void Enqueue(JobSnapshot snapshot)
    {
        bool post;
        lock (_inboxLock)
        {
            if (IsUnfinishedQueueWork(snapshot)) _unfinishedWorkArrived = true;
            _inbox[snapshot.Id] = snapshot;
            post = !_inboxQueued;
            _inboxQueued = true;
        }
        if (post) PostToUi(DrainInbox);
    }

    private void DrainInbox()
    {
        List<JobSnapshot> batch;
        bool unfinishedWorkArrived;
        lock (_inboxLock)
        {
            batch = [.. _inbox.Values];
            _inbox.Clear();
            _inboxQueued = false;
            unfinishedWorkArrived = _unfinishedWorkArrived;
            _unfinishedWorkArrived = false;
        }

        bool cancelledSleepPrompt = _sleepPrompt is not null && unfinishedWorkArrived;
        if (cancelledSleepPrompt) CancelQueueSleepPrompt();

        string searchTerm = _search.Text.Trim();
        bool sortBySize = _sort.SelectedText == "Size";
        bool structural = false;
        var queueTerminations = new List<JobSnapshot>();
        foreach (JobSnapshot snapshot in batch)
        {
            if (snapshot.IsUnacceptedBrowserCapture)
            {
                if (_items.Remove(snapshot.Id))
                {
                    _grid.PruneChecks(_items.Keys);
                    structural = true;
                }
                continue;
            }
            if (_items.TryGetValue(snapshot.Id, out DownloadItem? item))
            {
                JobStatus before = item.Status;
                JobState? previousState = item.Snapshot?.State;
                string name = item.Name;
                long size = item.SizeBytes;
                bool matched = searchTerm.Length > 0 && item.MatchesSearch(searchTerm);
                item.UpdateFrom(snapshot);
                if (IsQueueTermination(snapshot) && previousState != snapshot.State)
                    queueTerminations.Add(snapshot);
                structural |= NeedsListRefresh(item, before, name, size, matched, searchTerm, sortBySize);
                Announce(before, item);
            }
            else
            {
                var added = DownloadItem.From(snapshot);
                _items[snapshot.Id] = added;
                if (added.Status == JobStatus.Completed) Announce(JobStatus.Queued, added);
                if (IsQueueTermination(snapshot))
                    queueTerminations.Add(snapshot);
                structural = true;
            }
        }
        if (structural) RefreshList();
        else { _grid.RefreshRows(batch, static row => row.Id); UpdateFooter(); }
        if (!cancelledSleepPrompt && queueTerminations.Count > 0) HandleQueueFinished(queueTerminations);
    }

    private static bool IsUnfinishedQueueWork(JobSnapshot snapshot) =>
        snapshot.Kind != JobKind.BrowserFile && !snapshot.IsUnacceptedBrowserCapture
        && snapshot.State is not (JobState.Completed or JobState.Cancelled);

    internal static bool HasUnfinishedQueueWork(IEnumerable<JobSnapshot> snapshots) =>
        snapshots.Any(IsUnfinishedQueueWork);

    private static bool IsQueueTermination(JobSnapshot snapshot) =>
        snapshot.Kind != JobKind.BrowserFile && !snapshot.IsUnacceptedBrowserCapture
        && snapshot.State is (JobState.Completed or JobState.Cancelled);

    internal static bool QueueFinishedAfterTransitions(IEnumerable<JobSnapshot> transitions,
        IEnumerable<JobSnapshot> currentSnapshots) =>
        transitions.Any(IsQueueTermination) && !HasUnfinishedQueueWork(currentSnapshots);

    private bool HasUnprocessedUnfinishedWork()
    {
        lock (_inboxLock) return _unfinishedWorkArrived;
    }

    private void HandleQueueFinished(IReadOnlyList<JobSnapshot> queueTerminations)
    {
        if (_engine is null) return;
        JobSnapshot[] currentSnapshots = _engine.Snapshots().ToArray();
        if (!QueueFinishedAfterTransitions(queueTerminations, currentSnapshots) || HasUnprocessedUnfinishedWork()) return;

        JobSnapshot? latest = LatestCompletedQueueJob(currentSnapshots);

        if (latest is not null && _prefs.OpenFolderWhenQueueFinishes)
            _actions?.ShowFolder(latest);
        if (_prefs.SleepWhenQueueFinishes) StartQueueSleepPrompt();
    }

    internal static JobSnapshot? LatestCompletedQueueJob(IEnumerable<JobSnapshot> snapshots) =>
        snapshots
            .Where(snapshot => snapshot.State == JobState.Completed && !snapshot.IsUnacceptedBrowserCapture
                && snapshot.Kind != JobKind.BrowserFile)
            .OrderByDescending(snapshot => snapshot.CompletedUtc ?? DateTime.MinValue)
            .FirstOrDefault();

    private void StartQueueSleepPrompt()
    {
        if (_sleepPrompt is not null || _engine is null || _exiting || IsDisposed
            || !_prefs.SleepWhenQueueFinishes || HasUnfinishedQueueWork(_engine.Snapshots())
            || HasUnprocessedUnfinishedWork())
            return;

        var prompt = new QueueSleepPrompt(_colors, 60, OnQueueSleepPromptClosed);
        _sleepPrompt = prompt;
        try { prompt.Show(); }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            _sleepPrompt = null;
            prompt.Dispose();
            System.Diagnostics.Trace.WriteLine(ex.Message, "Queue completion action");
        }
    }

    private void CancelQueueSleepPrompt()
    {
        QueueSleepPrompt? prompt = _sleepPrompt;
        _sleepPrompt = null;
        prompt?.Cancel();
    }

    private void OnQueueSleepPromptClosed(QueueSleepPrompt prompt, bool approved)
    {
        if (!ReferenceEquals(_sleepPrompt, prompt)) return;
        _sleepPrompt = null;
        if (!approved || _engine is null || _exiting || IsDisposed) return;

        SleepPromptResult result;
        try
        {
            result = RequestSleepAfterPrompt(approved, shuttingDown: _exiting || IsDisposed,
                hasUnfinishedWork: () => _engine is null
                    || HasUnfinishedQueueWork(_engine.Snapshots()) || HasUnprocessedUnfinishedWork(),
                releaseSleepLock: () => EngineHost.HoldAwake(false),
                requestSleep: _requestSleep);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine(ex.Message, "Queue completion sleep");
            result = SleepPromptResult.Failed;
        }

        if (result == SleepPromptResult.Failed)
            PresentNotice("Sleep did not start", "Windows could not put the PC to sleep", VesperIcon.Warning, ShowFromTray);
    }

    internal static SleepPromptResult RequestSleepAfterPrompt(bool approved, bool shuttingDown,
        Func<bool> hasUnfinishedWork, Action releaseSleepLock, Func<bool> requestSleep)
    {
        if (!approved || shuttingDown || hasUnfinishedWork()) return SleepPromptResult.Skipped;
        releaseSleepLock();
        if (shuttingDown || hasUnfinishedWork()) return SleepPromptResult.Skipped;
        return requestSleep() ? SleepPromptResult.Slept : SleepPromptResult.Failed;
    }

    internal static bool NeedsListRefresh(DownloadItem item, JobStatus previousStatus, string previousName,
        long previousSize, bool previouslyMatched, string searchTerm, bool sortBySize)
    {
        if (previousStatus != item.Status || previousName != item.Name) return true;
        if (sortBySize && previousSize != item.SizeBytes) return true;
        return searchTerm.Length > 0 && previouslyMatched != item.MatchesSearch(searchTerm);
    }

    /// <summary>Tray notifications only for real transitions the user would want to know about.</summary>
    private void Announce(JobStatus before, DownloadItem item)
    {
        if (before != item.Status) AnnounceToScreenReader(before, item);
        if (!_prefs.NotifyOnFinish || _tray is null || before == item.Status || item.Snapshot?.Kind == JobKind.BrowserFile) return;
        if (item.Status == JobStatus.Completed && before != JobStatus.Missing)
        {
            _notice ??= new VesperNotice();
            if (_notice.PresentCompletion(item.Name, _colors, NoticeArea, ShowFromTray) && _prefs.PlayCompletionSound)
                VesperSounds.PlayCompletion();
            return;
        }
        bool wasActive = before is JobStatus.Downloading or JobStatus.Resolving or JobStatus.Processing or JobStatus.Verifying or JobStatus.Converting
            or JobStatus.Waiting or JobStatus.WaitingRetry;
        if (!wasActive) return;
        switch (item.Status)
        {
            case JobStatus.NeedsLink:
                PresentNotice("Link expired", item.Name, VesperIcon.Clock, ShowFromTray);
                break;
            case JobStatus.NeedsAttention: case JobStatus.Failed: case JobStatus.Suspect:
                PresentNotice("Download needs attention", item.Name, VesperIcon.Warning, ShowFromTray);
                break;
        }
    }

    private Rectangle NoticeArea => _noticeWorkArea.IsEmpty ? Screen.FromPoint(Cursor.Position).WorkingArea : _noticeWorkArea;

    // "Pause tonight, resume tomorrow": an expired YouTube merge job gets fresh links by itself. The engine only
    // accepts a plan that continues the saved bytes byte-for-byte, so this is safe to do without asking.
    private static async Task AutoRefreshAsync(DownloadEngine engine, JobSnapshot job)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            RescueResult result = await MediaSourceRescue.RefreshAsync(engine, job,
                EngineHost.MediaNetwork(options: engine.Options), timeout.Token, automatic: true);
            if (!result.Accepted) System.Diagnostics.Trace.WriteLine(result.Message, "Automatic link refresh");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine(ex.Message, "Automatic link refresh");
        }
    }

    // Narrator and other screen readers hear outcomes (finished, failed, needs attention) without the
    // user having to tab into the list. Throttled: one announcement per second, most recent wins.
    private DateTime _lastAnnouncement;
    private string? _pendingAnnouncement;
    private void AnnounceToScreenReader(JobStatus before, DownloadItem item)
    {
        if (!IsHandleCreated) return;
        string? text = item.Status switch
        {
            JobStatus.Completed when before != JobStatus.Missing => $"{item.Name} finished",
            JobStatus.Failed => $"{item.Name} failed",
            JobStatus.NeedsAttention or JobStatus.Suspect => $"{item.Name} needs attention",
            JobStatus.NeedsLink => $"{item.Name} needs a new link",
            _ => null
        };
        if (text is null) return;
        DateTime now = DateTime.UtcNow;
        TimeSpan elapsed = now - _lastAnnouncement;
        if (_lastAnnouncement != DateTime.MinValue && elapsed < TimeSpan.FromSeconds(1))
        {
            _pendingAnnouncement = text;
            _announcementTimer.Stop();
            _announcementTimer.Interval = Math.Max(1, (int)Math.Ceiling(1_000 - elapsed.TotalMilliseconds));
            _announcementTimer.Start();
            return;
        }

        _announcementTimer.Stop();
        _pendingAnnouncement = null;
        RaiseScreenReaderAnnouncement(text);
    }

    private void FlushScreenReaderAnnouncement()
    {
        _announcementTimer.Stop();
        if (_pendingAnnouncement is not { } text) return;
        _pendingAnnouncement = null;
        RaiseScreenReaderAnnouncement(text);
    }

    private void RaiseScreenReaderAnnouncement(string text)
    {
        _lastAnnouncement = DateTime.UtcNow;
        try
        {
            AccessibilityObject.RaiseAutomationNotification(System.Windows.Forms.Automation.AutomationNotificationKind.ActionCompleted,
                System.Windows.Forms.Automation.AutomationNotificationProcessing.MostRecent, text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            // No UI Automation client is listening; nothing to do.
        }
    }

    private void PresentNotice(string title, string detail, VesperIcon symbol, Action activate)
    {
        _notice ??= new VesperNotice();
        _notice.Present(title, detail, symbol, _colors, NoticeArea, activate);
    }

    private void RememberNoticeMonitor()
    {
        if (Visible && WindowState != FormWindowState.Minimized) _noticeWorkArea = Screen.FromControl(this).WorkingArea;
    }

    // ---- window, tray and lifecycle -------------------------------------------------------------------------------

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        void Add(string text, VesperIcon icon, EventHandler action)
        {
            var item = new ToolStripMenuItem(text, null, action) { Tag = icon };
            menu.Items.Add(item);
        }
        Add("Open VESPER", VesperIcon.Quality, (_, _) => ShowFromTray());
        Add("Add download…", VesperIcon.Plus, (_, _) => { ShowFromTray(); AddDownload(); });
        menu.Items.Add(new ToolStripSeparator());
        Add("Pause all", VesperIcon.Pause, (_, _) => _engine!.PauseAll());
        Add("Resume all", VesperIcon.Play, (_, _) => _engine!.ResumeAll());
        menu.Items.Add(new ToolStripSeparator());
        Add("Exit VESPER", VesperIcon.Close, (_, _) => ExitApplication());

        _tray = new NotifyIcon { Icon = Icon, Text = "VESPER " + Vesper.Shared.BuildInfo.Version(typeof(Form1).Assembly), Visible = true, ContextMenuStrip = menu };
        ApplyTrayAppearance();
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
    }

    private void ApplyTrayAppearance()
    {
        if (_tray?.ContextMenuStrip is not { } menu) return;
        foreach (ToolStripMenuItem item in menu.Items.OfType<ToolStripMenuItem>())
            if (item.Tag is VesperIcon icon)
                item.Image = VesperMenuTheme.IconImage(icon, _colors, DeviceDpi);
        VesperMenuTheme.Apply(menu, _colors, withIcons: true);
        VesperMenuTheme.FormatItems(menu.Items, _colors, itemWidth: 180, dpi: DeviceDpi);
    }

    private void BuildListMenu()
    {
        _listMenu.Items.Add(new ToolStripMenuItem("Import download list…", null, (_, _) => ImportDownloadList())
        {
            Tag = VesperIcon.Folder
        });
        _listMenu.Items.Add(new ToolStripMenuItem("Export list as JSON…", null, (_, _) => ExportDownloadList(".json"))
        {
            Tag = VesperIcon.Clipboard
        });
        _listMenu.Items.Add(new ToolStripMenuItem("Export list as CSV…", null, (_, _) => ExportDownloadList(".csv"))
        {
            Tag = VesperIcon.Clipboard
        });
        _listMenu.Items.Add(new ToolStripSeparator());
        _listMenu.Items.Add(new ToolStripMenuItem("Podcast feeds…", null, (_, _) => OpenPodcastFeeds())
        {
            Tag = VesperIcon.Audio
        });
        ApplyListMenuAppearance();
    }

    private void ApplyListMenuAppearance()
    {
        foreach (ToolStripMenuItem item in _listMenu.Items.OfType<ToolStripMenuItem>())
            if (item.Tag is VesperIcon icon)
                item.Image = VesperMenuTheme.IconImage(icon, _colors, DeviceDpi);
        VesperMenuTheme.Apply(_listMenu, _colors, withIcons: true);
        VesperMenuTheme.FormatItems(_listMenu.Items, _colors, itemWidth: 220, dpi: DeviceDpi);
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
        if (_exiting || e.CloseReason != CloseReason.UserClosing) CancelQueueSleepPrompt();
        if (_engine is not null && !_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            if (_prefs.CloseToTray) Hide();
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
            var choice = VesperMessageBox.Show(this,
                "Downloads are still running.\n\nExit now? Running downloads will be paused safely and resume next time VESPER starts.",
                "Exit VESPER", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (choice != DialogResult.Yes) return;
        }
        _exiting = true;
        CancelQueueSleepPrompt();
        _allowVisible = true;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        CancelQueueSleepPrompt();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _bridge?.Dispose();
        _notice?.Dispose();
        VesperSounds.Stop();
        _tray?.ContextMenuStrip?.Dispose();
        _tray?.Dispose();
        EngineHost.HoldAwake(false);
        _engine?.Dispose();                       // stops transfers and makes their progress durable
        base.OnFormClosed(e);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _engine?.WakeWaiting();   // connections made before sleep are dead
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color
            or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)) return;
        PostToUi(() =>
        {
            if (UiKit.Palette(_prefs) != _colors) ApplyAppearance();
        });
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
            if (_tray is not null)
                PresentNotice("Link copied", "Add " + FileNames.FromUrl(uri) + " to VESPER", VesperIcon.Clipboard,
                    () => { ShowFromTray(); AddDownload(text); });
        }
        catch (System.Runtime.InteropServices.ExternalException) { /* another program holds the clipboard; try again next time */ }
    }

    // ---- drag and drop --------------------------------------------------------------------------------------------

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        drgevent.Effect = drgevent.Data?.GetDataPresent(DataFormats.Text) == true
            || drgevent.Data?.GetDataPresent(DataFormats.UnicodeText) == true
            || UrlShortcutPaths(drgevent.Data).Length > 0
            ? DragDropEffects.Copy : DragDropEffects.None;
        base.OnDragEnter(drgevent);
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);
        string[] shortcuts = UrlShortcutPaths(drgevent.Data);
        if (shortcuts.Length > 0)
        {
            BeginInvoke((Action)(() => _ = AddUrlShortcutsAsync(shortcuts)));
            return;
        }
        string? text = drgevent.Data?.GetData(DataFormats.UnicodeText) as string ?? drgevent.Data?.GetData(DataFormats.Text) as string;
        if (!string.IsNullOrWhiteSpace(text)) BeginInvoke(() => AddDownload(text));
    }

    private async Task AddUrlShortcutsAsync(string[] paths)
    {
        try
        {
            string[] urls = await Task.Run(() => paths.Select(ReadInternetShortcut).OfType<string>().ToArray());
            if (IsDisposed) return;
            if (urls.Length > 0)
                AddDownload(string.Join(Environment.NewLine, urls));
            else
                VesperMessageBox.Show(this, "The dropped .url file did not contain a valid web link.",
                    "Can't add link", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            if (!IsDisposed)
                VesperMessageBox.Show(this, "The dropped .url file couldn't be read.",
                    "Can't add link", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static string[] UrlShortcutPaths(IDataObject? data)
    {
        if (data?.GetDataPresent(DataFormats.FileDrop) != true) return [];
        try
        {
            return data.GetData(DataFormats.FileDrop) is string[] files
                ? files.Where(file => !string.IsNullOrWhiteSpace(file)
                    && Path.GetExtension(file).Equals(".url", StringComparison.OrdinalIgnoreCase)).Take(50).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ArgumentException or InvalidCastException)
        {
            return [];
        }
    }

    internal static string? ReadInternetShortcut(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase)) return null;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > 32 * 1024) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            for (int lineNumber = 0; lineNumber < 64 && reader.ReadLine() is { } line; lineNumber++)
            {
                if (!line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) continue;
                string candidate = line[4..].Trim();
                return Vesper.Engine.UrlPolicy.TryParse(candidate, out Uri url, out _) ? url.AbsoluteUri : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
        }
        return null;
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
        BuildListMenu();
        _rowMenu.Opening += ConfigureRowMenu;
        _listButton.Click += (_, _) =>
        {
            ApplyListMenuAppearance();
            _listMenu.Show(_listButton, new Point(0, _listButton.Height));
        };
        // Create the native menu handles right after the window exists, without a timer, so the first right-click is instant.
        HandleCreated += (_, _) => BeginInvoke((Action)(() =>
        {
            if (!IsDisposed) { _ = _rowMenu.Handle; _ = _bandwidthMenu.DropDown.Handle; _ = _priorityMenu.DropDown.Handle; }
        }));
        _tooltips.SetToolTip(_settings, "Settings");
        _tooltips.SetToolTip(_add, "Add download (Ctrl+N)");
        _tooltips.SetToolTip(_listButton, "Import or export download lists and manage podcast feeds");
        _tooltips.SetToolTip(_searchFrame, "Search downloads");
        _add.Click += (_, _) => AddDownload();
        _settings.Click += (_, _) => OpenSettings();
        // Typing refilters once the keystrokes pause, not on every character (VES-019).
        var searchDebounce = new System.Windows.Forms.Timer { Interval = 120 };
        searchDebounce.Tick += (_, _) => { searchDebounce.Stop(); RefreshList(); };
        _search.TextChanged += (_, _) => { searchDebounce.Stop(); searchDebounce.Start(); };
        _filter.SelectedIndexChanged += (_, _) => RefreshList();
        _sort.SelectedIndexChanged += (_, _) => RefreshList();
        _pause.Click += (_, _) =>
        {
            foreach (var row in _grid.CheckedItems)
                if (row.BrowserFile is { Paused: false }) ControlBrowserDownload(row, "pause");
                else if (row.Snapshot is { } s) _engine?.Pause(s.Id);
        };
        _resume.Click += (_, _) =>
        {
            foreach (var row in _grid.CheckedItems)
                if (row.BrowserFile is { Paused: true, CanResume: true }) ControlBrowserDownload(row, "resume");
                else if (row.Snapshot is { } s) _engine?.Resume(s.Id);
        };
        _remove.Click += async (_, _) => await RemoveOrCancelSelectedAsync();
        _clear.Click += (_, _) => _grid.ClearChecks();

        _mast.Decoration = PaintMast;
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
        _footer.Decoration = (graphics, _) =>
        {
            if (_botanicalArt is { } art)
                art.PaintLandscape(graphics, _footer.ClientRectangle, _colors.IsDark);
            else
            {
                using var pen = new Pen(_colors.Divider);
                graphics.DrawLine(pen, 0, 0, _footer.Width, 0);
            }
        };

        LayoutMast();
        LayoutToolbar();
        LayoutSelectionToolbar();
    }

    private List<JobSnapshot> CheckedSnapshots() =>
        [.. _grid.CheckedItems.Where(i => i.Snapshot is not null).Select(i => i.Snapshot!)];

    internal static bool CanCancel(JobSnapshot snapshot) =>
        snapshot.Kind != JobKind.BrowserFile && snapshot.State is not
            (JobState.Completed or JobState.Verifying or JobState.Finalizing or JobState.Converting);

    private async Task RemoveOrCancelSelectedAsync()
    {
        if (_actions is null) return;
        List<JobSnapshot> selected = CheckedSnapshots();
        if (selected.Count == 0) return;

        if (selected.All(CanCancel))
        {
            foreach (JobSnapshot snapshot in selected) await _actions.CancelAsync(snapshot);
        }
        else if (selected.All(s => s.State == JobState.Completed))
        {
            _actions.Remove(selected);
        }
        // Mixed selections never combine data deletion with history-only removal.
    }

    private void LayoutMast()
    {
        int edge = UiKit.Scale(this, 20);
        int y = Math.Max(0, (_mast.Height - _add.Height) / 2);
        _settings.SetBounds(_mast.ClientSize.Width - edge - _settings.Width, y, _settings.Width, _settings.Height);
        int gap = UiKit.Scale(this, 12);
        if (_listButton.Visible)
        {
            _listButton.SetBounds(_settings.Left - gap - _listButton.Width, y, _listButton.Width, _add.Height);
            _add.Location = new Point(_listButton.Left - gap - _add.Width, y);
        }
        else
        {
            _add.Location = new Point(_settings.Left - gap - _add.Width, y);
        }
        _brandIcon.SetBounds(edge, UiKit.Scale(this, 10), UiKit.Scale(this, 62), UiKit.Scale(this, 55));
        int brandX = _botanicalArt is null ? edge : _brandIcon.Right + UiKit.Scale(this, 9);
        _brandText.Location = new Point(brandX, UiKit.Scale(this, 12));
        _brandCaption.Location = new Point(brandX + UiKit.Scale(this, 1), UiKit.Scale(this, 49));
        _mast.Invalidate(true);
    }

    private void LayoutToolbar()
    {
        int width = _normalTools.ClientSize.Width;
        int margin = UiKit.Scale(this, width < UiKit.Scale(this, 940) ? 14 : 24);
        int top = UiKit.Scale(this, 12);
        int height = UiKit.Scale(this, 32);
        int gap = UiKit.Scale(this, 12);
        int filterWidth = UiKit.Scale(this, 154);
        int sortWidth = UiKit.Scale(this, 149);
        int sortX = width - margin - sortWidth;
        _sort.SetBounds(sortX, top, sortWidth, height);
        int filterX = sortX - gap - filterWidth;
        _filter.SetBounds(filterX, top, filterWidth, height);
        int available = Math.Max(UiKit.Scale(this, 110), filterX - margin - UiKit.Scale(this, 18));
        int preferredMax = Math.Max(UiKit.Scale(this, 340), width / 2);
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

    private void PaintMast(Graphics graphics, Rectangle clip)
    {
        if (_botanicalArt is { } art)
        {
            int left = Math.Max(_brandText.Right, _brandCaption.Right) + UiKit.Scale(this, 16);
            int right = _add.Left - UiKit.Scale(this, 20);
            int width = Math.Clamp(right - left, 0, UiKit.Scale(this, 280));
            int inset = UiKit.Scale(this, 3);
            art.PaintHeader(graphics, _mast.ClientRectangle, clip,
                new Rectangle(right - width, inset, width, Math.Max(0, _mast.Height - 2 * inset)), dialog: false);
            return;
        }
        using var border = new Pen(_colors.Divider);
        graphics.DrawLine(border, 0, _mast.Height - 1, _mast.Width, _mast.Height - 1);
    }

    private void ApplyAppearance()
    {
        _colors = UiKit.Palette(_prefs);
        VesperWindowChrome.ApplyNativeColors(_colors);
        _notice?.ApplyPalette(_colors);
        if (_colors.Botanical) _botanicalArt ??= BotanicalArt.Acquire(_colors);
        else
        {
            _grid.EmptyIllustration = _brandIcon.Image = null;
            _botanicalArt?.Dispose();
            _botanicalArt = null;
        }
        _grid.EmptyIllustration = _botanicalArt?.EmptyHero;
        _brandIcon.Image = _botanicalArt?.Butterfly;
        _brandIcon.Visible = _brandIcon.Image is not null;
        VesperWindowChrome.Apply(this, _colors);
        BackColor = _colors.Back;
        ForeColor = _colors.Text;
        _mast.BackColor = _colors.Surface;
        _toolbar.BackColor = _normalTools.BackColor = _colors.Botanical ? _colors.Surface : _colors.Back;
        _selectedTools.BackColor = _colors.HighContrast ? _colors.Surface : _colors.Selection;
        _footer.BackColor = _colors.Botanical ? _colors.Surface : _colors.Secondary;
        _footer.Height = UiKit.Scale(this, _botanicalArt is null ? 30 : 48);
        _footerLabel.ForeColor = _colors.Muted;
        _brandText.ForeColor = _colors.Text;
        _brandCaption.ForeColor = _colors.Muted;
        _selectedLabel.ForeColor = _colors.Text;

        foreach (var button in new[] { _add, _settings, _listButton, _pause, _resume, _remove, _clear })
            button.ApplyPalette(_colors);
        _searchFrame.FillColor = _colors.Surface;
        _searchFrame.OutlineColor = _colors.Border;
        _searchFrame.FocusColor = _colors.MutedGreen;
        _searchFrame.SearchIconColor = VesperDrawing.Ink(VesperIcon.Search, _colors);
        _search.BackColor = _colors.Surface;
        _search.ForeColor = _colors.Text;
        _filter.ApplyPalette(_colors);
        _sort.ApplyPalette(_colors);
        _grid.ApplyAppearance(_colors, _prefs.Density);
        VesperMenuTheme.Apply(_rowMenu, _colors, withIcons: true);
        RefreshMenuStyle();
        ApplyListMenuAppearance();
        ApplyTrayAppearance();
        LayoutMast();
        _searchFrame.Invalidate();
        _footer.Invalidate(true);
        UpdateFooter();
    }

    // ---- list -----------------------------------------------------------------------------------------------------

    internal event Action? PreviewAddRequested;
    internal event Action? PreviewSettingsRequested;
    internal void RefreshPreviewAppearance() => ApplyAppearance();

    internal void LoadPreviewRows(IEnumerable<DownloadItem> rows)
    {
        if (_engine is not null) throw new InvalidOperationException("Sample data requires an offline window.");
        _items.Clear();
        foreach (var row in rows) _items.Add(row.Id, row);
        RefreshList();
    }

    private void RefreshList()
    {
        IEnumerable<DownloadItem> records = _items.Values;
        if (_prefs.HideMissingCompleted) records = records.Where(r => r.Status != JobStatus.Missing);

        string term = _search.Text.Trim();
        if (term.Length > 0)
            records = records.Where(r => r.MatchesSearch(term));

        records = _filter.SelectedText switch
        {
            "Downloading" => records.Where(r => r.Status is JobStatus.Downloading or JobStatus.Resolving or JobStatus.Processing or JobStatus.Converting),
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
        _grid.EmptyHint = _items.Count == 0 ? "Add a link or send one from your browser." : "";
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

        JobSnapshot[] pending = _items.Values.Select(item => item.Snapshot).OfType<JobSnapshot>()
            .Where(QueueDiskForecast.IsPending).ToArray();
        if (_engine is not null && pending.Length > 0)
        {
            _diskForecastTimer.Start();
            if (!_diskProbeRunning && DateTime.UtcNow - _lastDiskProbeUtc >= TimeSpan.FromMinutes(1))
                _ = RefreshDiskSpaceAsync(pending);
        }
        else
        {
            _diskForecastTimer.Stop();
            _availableDiskSpace.Clear();
            _lastDiskProbeUtc = DateTime.MinValue;
        }

        IReadOnlyList<DiskForecastVolume> forecast = QueueDiskForecast.Calculate(pending, _availableDiskSpace,
            _engine?.Options.FreeSpaceReserve ?? 0);
        if (forecast.Count > 0) parts.Add(QueueDiskForecast.Summary(forecast));
        string details = QueueDiskForecast.Details(forecast);
        _footerLabel.AccessibleDescription = details;
        _tooltips.SetToolTip(_footerLabel, details);
        _footerLabel.ForeColor = forecast.Any(volume => volume.IsShort) ? _colors.ProgressRed
            : forecast.Any(volume => volume.UnknownJobs > 0 || volume.AvailableBytes is null) ? _colors.ProgressAmber : _colors.Muted;

        string text = string.Join("    |    ", parts);
        if (_footerLabel.Text != text) _footerLabel.Text = text;
    }

    private async Task RefreshDiskSpaceAsync(JobSnapshot[]? pending = null)
    {
        if (_engine is null || _diskProbeRunning || IsDisposed) return;
        pending ??= _items.Values.Select(item => item.Snapshot).OfType<JobSnapshot>()
            .Where(QueueDiskForecast.IsPending).ToArray();
        string[] roots = QueueDiskForecast.VolumeRoots(pending);
        if (roots.Length == 0)
        {
            _lastDiskProbeUtc = DateTime.UtcNow;
            return;
        }

        _diskProbeRunning = true;
        try
        {
            IReadOnlyDictionary<string, long?> free = await Task.Run(() => QueueDiskForecast.ReadAvailable(roots));
            if (!IsDisposed) _availableDiskSpace = new Dictionary<string, long?>(free, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Security.SecurityException)
        {
            _availableDiskSpace = roots.ToDictionary(root => root, _ => (long?)null, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _diskProbeRunning = false;
            _lastDiskProbeUtc = DateTime.UtcNow;
        }

        if (!IsDisposed) UpdateFooter();
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
        _resume.Enabled = selected.Any(r => r.BrowserFile is { Paused: true, CanResume: true }
            || r.Snapshot is { IsCleanupPending: false, State: JobState.Paused or JobState.Failed or JobState.NeedsAttention });
        JobSnapshot[] native = [.. selected.Where(r => r.Snapshot is not null).Select(r => r.Snapshot!)];
        bool allNative = native.Length == count && count > 0;
        bool onlyUnfinished = allNative && native.All(CanCancel);
        bool onlyCompleted = allNative && native.All(s => s.State == JobState.Completed);
        _remove.Text = onlyUnfinished ? "Cancel" : "Remove";
        _remove.Symbol = onlyUnfinished ? VesperIcon.Close : VesperIcon.RemoveList;
        _remove.Enabled = onlyUnfinished || onlyCompleted;
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
        if (_engine is null) { PreviewAddRequested?.Invoke(); return; }
        using var dialog = new AddDownloadForm(_prefs, initialText);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // The dialog only passes a folder when it differs from the default, so category subfolders still apply.
        string? folder = string.Equals(dialog.Destination, _prefs.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? null : dialog.Destination;
        var problems = new List<string>();
        Guid? duplicateToShow = null;
        void Record(AddResult result, string url)
        {
            if (result.Status == AddStatus.Duplicate)
                duplicateToShow ??= result.JobId;
            else if (!result.Ok)
                problems.Add($"{UrlPolicy.Redact(url)}\n   {result.Message}");
        }

        foreach (MediaDownloadChoice media in dialog.MediaJobs)
        {
            AddResult result = _engine.Add(new AddRequest(media.Url, folder, media.FileName,
                StartNow: !dialog.QueueOnly, Referer: media.Referer, UserAgent: media.UserAgent,
                Kind: media.Kind, OriginalPageUrl: media.OriginalPageUrl,
                MediaProvider: media.Provider, MediaId: media.MediaId,
                MediaQuality: media.Quality, MediaThumbnailUrl: media.ThumbnailUrl,
                MediaExpiresAt: media.ExpiresAt, MediaPlanJson: media.MediaPlanJson, MediaFormatId: media.FormatId,
                Output: media.Output, MediaMetadataJson: media.MediaMetadataJson, Secrets: media.Secrets));
            Record(result, media.Url);
        }
        foreach (string url in dialog.DownloadUrls)
        {
            bool hls = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
            AddResult result = _engine.Add(new AddRequest(url, folder, StartNow: !dialog.QueueOnly, Kind: hls ? JobKind.Hls : JobKind.Http));
            Record(result, url);
        }
        if (duplicateToShow is Guid duplicateId && _engine.Snapshot(duplicateId) is { } existing)
        {
            string nextStep = existing.State switch
            {
                JobState.Failed => "Use Resume or Start over from its row.",
                JobState.NeedsLink => "Use Replace link from its row.",
                JobState.NeedsAttention => "Open Details from its row to see the recovery actions.",
                JobState.Suspect => "Open Details from its row to inspect the file before choosing Rescue.",
                _ => "Review or manage it from its row."
            };
            DialogResult show = VesperMessageBox.Show(this,
                $"{existing.FileName} is already in your list. {nextStep}\n\nShow the existing row now?",
                "Download already exists", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (show == DialogResult.Yes) RevealDownload(existing);
        }
        if (problems.Count > 0)
            VesperMessageBox.Show(this, "Some links were not added:\n\n" + string.Join("\n\n", problems), "Add download", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ImportDownloadList()
    {
        if (_engine is null) return;
        using var picker = new OpenFileDialog
        {
            Title = "Import download list",
            Filter = "VESPER lists (*.json;*.csv)|*.json;*.csv",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        if (!TryReadDownloadListFile(picker.FileName, out string contents, out string readError))
        {
            VesperMessageBox.Show(this, readError, "Import download list", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        DownloadListReadResult parsed = DownloadListTransfer.Read(contents, Path.GetExtension(picker.FileName));
        if (!parsed.Success)
        {
            VesperMessageBox.Show(this, parsed.Error ?? "The list could not be imported.", "Import download list",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (parsed.Entries.Count == 0)
        {
            VesperMessageBox.Show(this, "The list contains no downloads.", "Import download list",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int queued = parsed.Entries.Count(entry => entry.State == DownloadListState.Queued);
        int paused = parsed.Entries.Count - queued;
        string prompt = $"Import {parsed.Entries.Count} downloads? Queued items may start after import. {paused} paused item{(paused == 1 ? " stays" : "s stay")} paused.";
        if (VesperMessageBox.Show(this, prompt, "Import download list", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        int added = 0;
        int duplicates = 0;
        int errorCount = 0;
        var problems = new List<string>();
        var importedQueuedJobs = new List<Guid>();
        for (int i = 0; i < parsed.Entries.Count; i++)
        {
            DownloadListEntry entry = parsed.Entries[i];
            AddResult result;
            try
            {
                result = _engine.Add(new AddRequest(entry.Url, DestinationFolder: ImportSiteFolder(_prefs, entry),
                    StartNow: false, Kind: entry.Kind, InitiallyPaused: true));
            }
            catch (IOException ex)
            {
                errorCount++;
                problems.Add($"Import stopped at entry {i + 1}: {ex.Message}");
                break;
            }
            if (result.Status == AddStatus.Added)
            {
                added++;
                if (entry.State == DownloadListState.Queued) importedQueuedJobs.Add(result.JobId);
            }
            else if (result.Status == AddStatus.Duplicate)
                duplicates++;
            else
            {
                errorCount++;
                if (problems.Count < 8) problems.Add($"Entry {i + 1}: {result.Message}");
            }
        }

        if (errorCount == 0)
        {
            foreach (Guid id in importedQueuedJobs)
            {
                try { _engine.Resume(id); }
                catch (IOException ex)
                {
                    errorCount++;
                    problems.Add($"An imported item remains paused because it could not be saved: {ex.Message}");
                    break;
                }
            }
        }

        string summary = $"{added} added; {duplicates} already in the list.";
        if (errorCount > 0)
        {
            summary += "\n\nSome entries were not imported or started. Check the list before retrying:\n"
                + string.Join("\n", problems);
            if (errorCount > problems.Count) summary += $"\nAnd {errorCount - problems.Count} more.";
        }
        VesperMessageBox.Show(this, summary, "Import download list", MessageBoxButtons.OK,
            errorCount > 0 ? MessageBoxIcon.Information : MessageBoxIcon.None);
    }

    private void OpenPodcastFeeds()
    {
        if (_engine is null) return;
        using var dialog = new PodcastFollowForm(_engine, _prefs);
        dialog.ShowDialog(this);
    }

    internal static string? ImportSiteFolder(VesperPreferences preferences, DownloadListEntry entry) =>
        SiteRule.ForPage(preferences.SiteRules, entry.Url)?.Folder;

    private void ExportDownloadList(string extension)
    {
        if (_engine is null) return;
        if (extension is not (".json" or ".csv")) return;
        DownloadListExportResult export = DownloadListTransfer.CreateExport(_engine.Snapshots());
        if (export.Entries.Count == 0)
        {
            VesperMessageBox.Show(this, "There are no transferable downloads in the list.", "Export download list",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var picker = new SaveFileDialog
        {
            Title = "Export download list",
            Filter = extension == ".csv" ? "CSV list (*.csv)|*.csv" : "JSON list (*.json)|*.json",
            DefaultExt = extension[1..],
            AddExtension = true,
            OverwritePrompt = true,
            FileName = "vesper-download-list" + extension
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        if (!Path.GetExtension(picker.FileName).Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            VesperMessageBox.Show(this, $"Choose a {extension} filename.", "Export download list",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            string contents = extension == ".csv"
                ? DownloadListTransfer.WriteCsv(export.Entries)
                : DownloadListTransfer.WriteJson(export.Entries);
            File.WriteAllText(picker.FileName, contents, new System.Text.UTF8Encoding(false));
            string summary = $"Exported {export.Entries.Count} downloads. Waiting or running items are marked Queued; items needing your action are marked Paused.";
            if (export.Skipped > 0)
            {
                var reasons = new List<string>();
                if (export.CompletedSkipped > 0) reasons.Add($"{export.CompletedSkipped} completed");
                if (export.BrowserOwnedSkipped > 0) reasons.Add($"{export.BrowserOwnedSkipped} Chrome-owned");
                if (export.UnsupportedSkipped > 0) reasons.Add($"{export.UnsupportedSkipped} unsupported");
                if (export.UnsafeUrlSkipped > 0) reasons.Add($"{export.UnsafeUrlSkipped} with a URL that may contain access credentials");
                if (export.DuplicateSkipped > 0) reasons.Add($"{export.DuplicateSkipped} duplicate links");
                if (export.LimitSkipped > 0) reasons.Add($"{export.LimitSkipped} over the export limit");
                summary += $" {export.Skipped} items were omitted: {string.Join(", ", reasons)}.";
            }
            VesperMessageBox.Show(this, summary, "Export download list", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or System.Security.SecurityException or NotSupportedException)
        {
            VesperMessageBox.Show(this, "The list could not be written: " + ex.Message, "Export download list",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static bool TryReadDownloadListFile(string path, out string contents, out string error)
    {
        contents = "";
        error = "";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0)
            {
                error = "The list file is empty.";
                return false;
            }
            if (stream.Length > DownloadListTransfer.MaxInputBytes)
            {
                error = "The list is too large to import.";
                return false;
            }

            byte[] bytes = new byte[DownloadListTransfer.MaxInputBytes + 1];
            int total = 0;
            while (total < bytes.Length)
            {
                int read = stream.Read(bytes, total, bytes.Length - total);
                if (read == 0) break;
                total += read;
            }
            if (total > DownloadListTransfer.MaxInputBytes)
            {
                error = "The list is too large to import.";
                return false;
            }

            contents = new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, total);
            return true;
        }
        catch (System.Text.DecoderFallbackException)
        {
            error = "The list is not valid UTF-8 text.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or System.Security.SecurityException or NotSupportedException)
        {
            error = "The list could not be read: " + ex.Message;
            return false;
        }
    }

    private void RevealDownload(JobSnapshot snapshot)
    {
        if (snapshot.IsUnacceptedBrowserCapture) return;
        if (_items.TryGetValue(snapshot.Id, out DownloadItem? item)) item.UpdateFrom(snapshot);
        else _items[snapshot.Id] = item = DownloadItem.From(snapshot);
        _search.Clear();
        _filter.SelectedIndex = 0;
        RefreshList();
        _grid.Reveal(item.Id);
    }

    private void OpenSettings()
    {
        if (_engine is null) { PreviewSettingsRequested?.Invoke(); return; }
        using var dialog = new SettingsForm(_prefs, _engine);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        VesperPreferences updated = dialog.Result;
        bool restartNeeded = updated.AllowPrivateNetworks != _prefs.AllowPrivateNetworks
            || updated.ProxyMode != _prefs.ProxyMode || updated.ProxyAddress != _prefs.ProxyAddress;
        CopyPreferences(updated, _prefs);
        if (!_prefs.SleepWhenQueueFinishes) CancelQueueSleepPrompt();

        BuildRateOptions();
        if (SettingsStore.Save(_prefs) is { } saveError)
            VesperMessageBox.Show(this, "Settings could not be saved and will be lost when VESPER closes:\n" + saveError, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        EngineHost.Apply(_engine, _prefs);        // network policy, limits and queue rules update
        ApplyAppearance();
        RefreshList();
        if (restartNeeded)
            VesperMessageBox.Show(this, "Network access and proxy changes apply the next time VESPER starts.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    internal static void CopyPreferences(VesperPreferences from, VesperPreferences to)
    {
        to.Theme = from.Theme; to.Density = from.Density; to.Motion = from.Motion; to.Botanical = from.Botanical;
        to.HideMissingCompleted = from.HideMissingCompleted; to.CategoryFolders = from.CategoryFolders; to.DefaultFolder = from.DefaultFolder;
        to.FileNameTemplate = from.FileNameTemplate;
        to.SiteRules = SiteRule.Normalize(from.SiteRules);
        to.ConcurrentTransfers = from.ConcurrentTransfers; to.GlobalRateLimitKbs = from.GlobalRateLimitKbs;
        to.PerTransferRateLimitKbs = from.PerTransferRateLimitKbs; to.BandwidthPresetsKbs = [.. from.BandwidthPresetsKbs];
        to.MaxConnectionsPerJob = from.MaxConnectionsPerJob; to.NotifyOnFinish = from.NotifyOnFinish; to.WatchClipboard = from.WatchClipboard;
        to.PlayCompletionSound = from.PlayCompletionSound;
        to.OpenFolderWhenQueueFinishes = from.OpenFolderWhenQueueFinishes;
        to.SleepWhenQueueFinishes = from.SleepWhenQueueFinishes;
        to.PreventSleep = from.PreventSleep; to.PauseOnMeteredNetwork = from.PauseOnMeteredNetwork;
        to.CloseToTray = from.CloseToTray; to.AllowPrivateNetworks = from.AllowPrivateNetworks;
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
                if (_menuTarget is not { } target) return;
                if (target.BrowserFile is not null)
                {
                    if (text == "Pause") ControlBrowserDownload(target, "pause");
                    else if (text == "Resume") ControlBrowserDownload(target, "resume");
                    else if (text == "Cancel download") ControlBrowserDownload(target, "cancel");
                }
                else if (target.Snapshot is { } snapshot) action(snapshot);
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
        Add("Stop and keep", VesperIcon.Check, s => { _engine?.StopAndKeepLive(s.Id); });
        Add("Cancel download", VesperIcon.Close, s => { if (_actions is { } actions) _ = actions.CancelAsync(s); });
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
        Add("Remove from list…", VesperIcon.RemoveList, s => _actions?.Remove([s]));
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
        if (_menuTarget?.BrowserFile is { } browser)
        {
            foreach (var (name, command) in _menuCommands)
                command.Available = name == "Cancel download" || name == "Pause" && !browser.Paused
                    || name == "Resume" && browser.Paused && browser.CanResume;
            _bandwidthMenu.Available = _priorityMenu.Available = false;
            return;
        }
        if (_menuTarget?.Snapshot is not { } s) { e.Cancel = true; return; }

        bool fileAvailable = s.State == JobState.Completed && !s.FileMissing;
        bool unfinished = s.State is not (JobState.Completed or JobState.Cancelled);
        foreach (var (name, command) in _menuCommands)
        {
            bool available = name switch
            {
                "Details" or "Rename entry…" => true,
                "Remove from list…" => s.State == JobState.Completed,
                "Cancel download" => CanCancel(s),
                "Copy source URL" => s.Kind != JobKind.BrowserFile,
                "Delete downloaded file…" => fileAvailable && s.Kind != JobKind.BrowserFile,
                "Open file" or "Open containing folder" or "Verify integrity…" =>
                    fileAvailable || (name == "Open containing folder" && s.State == JobState.Suspect),
                "Locate file…" => s.State == JobState.Completed && s.FileMissing,
                "Pause" => s.State is JobState.Resolving or JobState.Downloading or JobState.Queued or JobState.WaitingForNetwork or JobState.WaitingForRetry,
                "Resume" => !s.IsCleanupPending && s.State is (JobState.Paused or JobState.Failed or JobState.NeedsAttention or JobState.Cancelled),
                "Stop and keep" => !s.IsCleanupPending && s.IsLiveRecording && s.State is not (JobState.Completed or JobState.Finalizing or JobState.Verifying or JobState.Converting),
                "Start now" => !s.IsCleanupPending && s.State is (JobState.Queued or JobState.Paused or JobState.WaitingForRetry or JobState.WaitingForNetwork),
                "Replace link…" => !s.IsCleanupPending && s.State is (JobState.Paused or JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed or JobState.Queued or JobState.WaitingForRetry),
                "Resume anyway…" => s.State == JobState.NeedsAttention && s.Reason == "no-validator",
                "Start over…" => s is { IsCleanupPending: true, State: JobState.NeedsAttention }
                    || s.CommittedBytes > 0 && s.State is (JobState.NeedsAttention or JobState.Failed or JobState.Suspect or JobState.Paused or JobState.NeedsLink),
                _ => false
            };
            if (command.Available != available) command.Available = available;
        }

        bool tunable = unfinished && s.State is not (JobState.Suspect or JobState.Finalizing or JobState.Verifying or JobState.Converting);
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
        ApplyTrayAppearance();
        LayoutMast();
        LayoutToolbar();
        LayoutSelectionToolbar();
    }
}
