namespace VESPER;

// Editing happens on a copy. Only Save applies the preferences to the running app.
[System.ComponentModel.DesignerCategory("Form")]
internal sealed class SettingsForm : VesperPopupForm
{
    private sealed class PresetRow(Label caption, VesperInputFrame frame, TextBox value, VesperChoice unit, VesperButton remove)
    {
        public Label Caption { get; } = caption;
        public VesperInputFrame Frame { get; } = frame;
        public TextBox Value { get; } = value;
        public VesperChoice Unit { get; } = unit;
        public VesperButton Remove { get; } = remove;
    }

    private readonly VesperPreferences _draft;
    private readonly VesperPalette _colors;
    private readonly Panel _content;
    private readonly Panel _sidebar;
    private readonly Dictionary<string, Panel> _pages = [];
    private readonly Dictionary<string, VesperButton> _navigation = [];
    private readonly List<PresetRow> _presets = [];
    private readonly Font _navFont = new("Segoe UI", 9F);
    private readonly Font _navActiveFont = new("Segoe UI", 9F, FontStyle.Bold);
    private Panel _presetArea = null!;
    private VesperButton _addPreset = null!;
    private TextBox _globalInput = null!;
    private VesperInputFrame _globalFrame = null!;
    private CheckBox _globalEnabled = null!;
    private Action? _layoutTransfers;
    private TextBox _scheduleStart = null!;
    private TextBox _scheduleEnd = null!;
    private VesperInputFrame _scheduleStartFrame = null!;
    private VesperInputFrame _scheduleEndFrame = null!;
    private TextBox _proxyAddress = null!;
    private VesperInputFrame _proxyFrame = null!;

    public VesperPreferences Result => _draft.Clone();
    public SettingsForm() : this(new VesperPreferences()) { }

    public SettingsForm(VesperPreferences preferences)
    {
        _draft = preferences.Clone();
        _colors = UiKit.Palette(_draft);
        VesperWindowChrome.Brand(this);
        Text = "VESPER Settings";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(746, 590);
        MinimumSize = new Size(700, 560);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9F);
        BackColor = _colors.Surface;
        ForeColor = _colors.Text;

        _sidebar = new Panel { Dock = DockStyle.Left, Width = 166, BackColor = _colors.Botanical ? _colors.Back : _colors.Secondary };
        _sidebar.Paint += (_, e) =>
        {
            using var pen = new Pen(_colors.Divider);
            e.Graphics.DrawLine(pen, _sidebar.Width - 1, 0, _sidebar.Width - 1, _sidebar.Height);
        };
        var title = Label("Settings", 17, bold: true);
        title.SetBounds(20, 25, 138, 38);
        _sidebar.Controls.Add(title);
        (string Title, VesperIcon Icon)[] tabs =
        [
            ("Downloads", VesperIcon.Download), ("Transfers", VesperIcon.Gauge), ("Network", VesperIcon.Provider),
            ("Appearance", VesperIcon.Leaf), ("Browser", VesperIcon.Link)
        ];
        for (int i = 0; i < tabs.Length; i++)
        {
            var (tab, icon) = tabs[i];
            var nav = Button(tab, 146, symbol: icon);
            nav.Name = "navigation" + tab;
            nav.NavigationItem = true;
            nav.TextAlign = ContentAlignment.MiddleLeft;
            nav.SetBounds(10, 102 + i * 43, 146, 37);
            nav.Click += (_, _) => ShowPage(tab);
            _navigation.Add(tab, nav);
            _sidebar.Controls.Add(nav);
        }
        _content = new Panel { Dock = DockStyle.Fill, BackColor = _colors.Surface };
        var footer = new VesperPanel { Dock = DockStyle.Bottom, Height = 59, BackColor = _colors.Surface };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(_colors.Divider);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        var cancel = Button("Cancel", 87);
        cancel.DialogResult = DialogResult.Cancel;
        var save = Button("Save settings", 125, primary: true);
        save.Click += (_, _) => Save();
        footer.Controls.AddRange([cancel, save]);
        void LayoutFooter()
        {
            int S(int px) => IsHandleCreated ? UiKit.Scale(this, px) : px;
            save.SetBounds(footer.Width - S(21 + 125), S(13), S(125), S(34));
            cancel.SetBounds(save.Left - S(8 + 87), S(13), S(87), S(34));
        }
        footer.Resize += (_, _) => LayoutFooter();
        Controls.Add(_content);
        Controls.Add(_sidebar);
        Controls.Add(footer);
        LayoutFooter();
        AcceptButton = save;
        CancelButton = cancel;

        BuildDownloads();
        BuildTransfers();
        BuildNetwork();
        BuildAppearance();
        BuildBrowser();
        ShowPage("Downloads");
        ClientSize = new Size(ClientSize.Width, ClientSize.Height + VesperDialogChrome.HeaderHeight);
        VesperDialogChrome.Attach(this, "VESPER  /  Settings", _colors, VesperIcon.Gear);
    }

    private Label Label(string text, float size = 9, bool bold = false, bool muted = false)
    {
        return new Label
        {
            Text = text, AutoSize = false,
            Font = bold && size >= 16 ? UiKit.Heading(_colors, size)
                : new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = muted ? _colors.Muted : _colors.Text,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private VesperButton Button(string text, int width, bool primary = false, VesperIcon symbol = VesperIcon.None)
    {
        var button = new VesperButton { Text = text, Width = width, Height = 34, Primary = primary, Symbol = symbol };
        button.ApplyPalette(_colors);
        return button;
    }

    private VesperChoice Choice(string[] options, int selected, int width)
    {
        var control = new VesperChoice { Width = width, Height = 33 };
        control.SetItems(options);
        control.SelectedIndex = Math.Clamp(selected, 0, options.Length - 1);
        control.ApplyPalette(_colors);
        return control;
    }

    private Panel Page(string key, string subtitle)
    {
        var page = new VesperScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false, BackColor = _colors.Surface };
        var heading = Label(key, 16, bold: true);
        heading.SetBounds(25, 22, 390, 33);
        var intro = Label(subtitle, 8.5F, muted: true);
        intro.SetBounds(25, 57, 470, 23);
        var divider = Divider(90);
        page.Controls.AddRange([heading, intro, divider]);
        page.Resize += (_, _) =>
        {
            intro.Width = Math.Max(200, page.ClientSize.Width - 50);
            divider.Width = Math.Max(200, page.ClientSize.Width - 50);
        };
        _pages.Add(key, page);
        _content.Controls.Add(page);
        return page;
    }

    private Panel Divider(int y) => new()
    {
        BackColor = _colors.Divider, Height = 1, Location = new Point(25, y), Width = 505,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
    };

    private void ShowPage(string tab)
    {
        foreach (var (name, page) in _pages)
            page.Visible = name == tab;
        if (_pages.TryGetValue(tab, out var active)) active.BringToFront();
        foreach (var (name, nav) in _navigation)
        {
            nav.NavigationActive = name == tab;
            nav.Font = name == tab ? _navActiveFont : _navFont;
            nav.Invalidate();
        }
    }

    private CheckBox Check(string text, int x, int y, bool value, Action<bool> update)
    {
        var check = new VesperCheckBox(_colors)
        {
            Text = text, Checked = value, Location = new Point(x, y), Size = new Size(480, 25),
            AutoSize = false,
            ForeColor = _colors.Text, BackColor = _colors.Surface
        };
        check.CheckedChanged += (_, _) => update(check.Checked);
        return check;
    }

    private (VesperInputFrame frame, TextBox input) Input(string text, int width)
    {
        var frame = new VesperInputFrame
        {
            Size = new Size(width, 34), BackColor = _colors.Surface,
            BorderColor = _colors.Border, FocusColor = _colors.MutedGreen
        };
        var input = new TextBox
        {
            Text = text, BorderStyle = BorderStyle.None,
            Location = new Point(10, 8), Width = width - 20,
            BackColor = _colors.Surface, ForeColor = _colors.Text
        };
        input.GotFocus += (_, _) => { frame.Active = true; frame.Invalidate(); };
        input.LostFocus += (_, _) => { frame.Active = false; frame.Invalidate(); };
        frame.Controls.Add(input);
        frame.Resize += (_, _) => input.Width = Math.Max(15, frame.ClientSize.Width - 20);
        return (frame, input);
    }

    private void BuildDownloads()
    {
        var page = Page("Downloads", "Where new files will be saved.");
        var folderLabel = Label("Default download folder", 9, bold: true);
        folderLabel.SetBounds(25, 108, 400, 22);
        var folderNote = Label("Used unless you choose a different folder for a download.", 8, muted: true);
        folderNote.SetBounds(25, 132, 490, 20);
        var (frame, folder) = Input(_draft.DefaultFolder, 375);
        frame.Location = new Point(25, 162);
        folder.TextChanged += (_, _) => _draft.DefaultFolder = folder.Text;
        var browse = Button("Browse…", 96);
        browse.SetBounds(447, 162, 96, 34);
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "Default folder for new downloads", UseDescriptionForTitle = true };
            if (Directory.Exists(folder.Text)) picker.SelectedPath = folder.Text;
            if (picker.ShowDialog(this) == DialogResult.OK) folder.Text = picker.SelectedPath;
        };
        var separator1 = Divider(219);
        var categories = Check("Create category subfolders", 25, 236, _draft.CategoryFolders, v => _draft.CategoryFolders = v);
        var categoryNote = Label("Organize future files into Music, Video, Archives and other folders.", 8, muted: true);
        categoryNote.SetBounds(45, 265, 470, 23);
        var separator2 = Divider(305);
        var missing = Check("Hide missing completed files", 25, 324, _draft.HideMissingCompleted, v => _draft.HideMissingCompleted = v);
        var missingNote = Label("Only completed records are hidden. Paused and recoverable entries stay visible.", 8, muted: true);
        missingNote.SetBounds(45, 353, 470, 37);
        var separator3 = Divider(398);
        var notify = Check("Show a notification when a download finishes", 25, 410, _draft.NotifyOnFinish, v => _draft.NotifyOnFinish = v);
        var sound = Check("Play completion sound", 25, 438, _draft.PlayCompletionSound, v =>
        {
            _draft.PlayCompletionSound = v;
            if (!v) VesperSounds.Stop();
        });
        var previewSound = Button("Preview sound", 140, symbol: VesperIcon.Audio);
        previewSound.SetBounds(257, 437, 140, 29);
        previewSound.Click += (_, _) => VesperSounds.PlayCompletion();
        var clipboard = Check("Offer to download links I copy", 25, 475, _draft.WatchClipboard, v => _draft.WatchClipboard = v);
        var tray = Check("Keep VESPER in the tray when I close the window", 25, 503, _draft.CloseToTray, v => _draft.CloseToTray = v);
        var collisions = Label("Existing files are never overwritten: a number is added to the new file's name instead.", 8, muted: true);
        collisions.SetBounds(25, 533, 490, 23);
        page.Controls.AddRange([folderLabel, folderNote, frame, browse, separator1, categories, categoryNote,
            separator2, missing, missingNote, separator3, notify, sound, previewSound, clipboard, tray, collisions]);
        void Layout()
        {
            int right = page.ClientSize.Width - 25;
            browse.Left = Math.Max(308, right - browse.Width);
            frame.Width = Math.Max(180, browse.Left - 34);
            int noteWidth = Math.Max(210, right - 45);
            categoryNote.Width = missingNote.Width = collisions.Width = noteWidth;
        }
        page.Resize += (_, _) => Layout();
        Layout();
    }

    private void BuildTransfers()
    {
        var page = Page("Transfers", "How many downloads run at once, and how fast.");
        var concurrencyLabel = Label("Simultaneous downloads", 9, bold: true);
        concurrencyLabel.SetBounds(25, 108, 280, 22);
        var concurrencyNote = Label("Maximum number of active jobs.", 8, muted: true);
        concurrencyNote.SetBounds(25, 133, 260, 22);
        int[] counts = [1, 2, 3, 4, 5, 6, 8];
        var concurrency = Choice(["1", "2", "3", "4", "5", "6", "8"],
            Math.Max(0, Array.IndexOf(counts, _draft.ConcurrentTransfers)), 126);
        concurrency.SetBounds(404, 109, 126, 33);
        concurrency.SelectedIndexChanged += (_, _) => _draft.ConcurrentTransfers = int.Parse(concurrency.SelectedText);
        var separator1 = Divider(169);
        _globalEnabled = Check("Enable global speed limit", 25, 185, _draft.GlobalRateLimitKbs > 0,
            _ => UpdateGlobalEnabled());
        var globalNote = Label("Maximum combined speed across all active downloads.", 8, muted: true);
        globalNote.SetBounds(45, 211, 490, 20);
        (_globalFrame, _globalInput) = Input(_draft.GlobalRateLimitKbs > 0
            ? _draft.GlobalRateLimitKbs.ToString() : "1024", 143);
        _globalFrame.SetBounds(25, 240, 143, 34);
        var unit = Label("KB/s · disable the option for unlimited speed", 8, muted: true);
        unit.SetBounds(179, 243, 346, 27);
        var separator2 = Divider(291);
        var presetHeading = Label("Per-download bandwidth presets", 9, bold: true);
        presetHeading.SetBounds(25, 303, 420, 22);
        var presetDescription = Label("Shown in the download's right-click Bandwidth menu.", 8, muted: true);
        presetDescription.SetBounds(25, 326, 470, 21);
        _presetArea = new Panel
        {
            Location = new Point(25, 354), Size = new Size(485, 105),
            BackColor = _colors.Surface, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _addPreset = Button("Add preset", 112, symbol: VesperIcon.Plus);
        _addPreset.Click += (_, _) => { if (_presets.Count < 8) AddPreset(2048); };
        page.Controls.AddRange([concurrencyLabel, concurrencyNote, concurrency, separator1,
            _globalEnabled, globalNote, _globalFrame, unit, separator2,
            presetHeading, presetDescription, _presetArea, _addPreset]);
        void Layout()
        {
            concurrency.Left = Math.Max(292, page.ClientSize.Width - 25 - concurrency.Width);
            unit.Width = Math.Max(160, page.ClientSize.Width - 204);
            bool showLimit = _globalEnabled.Checked;
            _globalFrame.Visible = unit.Visible = showLimit;
            int offset = showLimit ? 0 : -44;
            separator2.Top = 291 + offset;
            presetHeading.Top = 303 + offset;
            presetDescription.Top = 326 + offset;
            _presetArea.Top = 354 + offset;
            _presetArea.Width = Math.Max(315, page.ClientSize.Width - 50);
            LayoutPresets();
        }
        _layoutTransfers = Layout;
        page.Resize += (_, _) => Layout();
        foreach (int kb in _draft.BandwidthPresetsKbs.Take(8)) AddPreset(Math.Clamp(kb, 1, 10000000));
        UpdateGlobalEnabled();
        Layout();
    }

    private void UpdateGlobalEnabled()
    {
        if (_globalFrame is null || _globalInput is null || _globalEnabled is null) return;
        _globalInput.Enabled = _globalEnabled.Checked;
        _globalFrame.Invalidate();
        if (!_globalEnabled.Checked) _draft.GlobalRateLimitKbs = 0;
        _layoutTransfers?.Invoke();
    }

    private void AddPreset(int kb)
    {
        if (_presets.Count >= 8) return;
        var caption = Label($"Preset {_presets.Count + 1}", 8, muted: true);
        var (frame, value) = Input(kb.ToString(), 105);
        var unit = Choice(["KB/s", "MB/s"], 0, 83);
        var remove = Button("", 31, symbol: VesperIcon.Close);
        remove.AccessibleName = "Remove bandwidth preset";
        var row = new PresetRow(caption, frame, value, unit, remove);
        remove.Click += (_, _) =>
        {
            _presets.Remove(row);
            _presetArea.Controls.Remove(caption);
            _presetArea.Controls.Remove(frame);
            _presetArea.Controls.Remove(unit);
            _presetArea.Controls.Remove(remove);
            caption.Dispose(); frame.Dispose(); unit.Dispose(); remove.Dispose();
            LayoutPresets();
        };
        _presets.Add(row);
        _presetArea.Controls.AddRange([caption, frame, unit, remove]);
        LayoutPresets();
    }

    private void LayoutPresets()
    {
        if (_presetArea is null || _addPreset is null) return;
        for (int i = 0; i < _presets.Count; i++)
        {
            int y = i * 37;
            var row = _presets[i];
            row.Caption.Text = $"Preset {i + 1}";
            row.Caption.SetBounds(0, y, 59, 33);
            row.Frame.SetBounds(66, y, 105, 32);
            row.Unit.SetBounds(178, y, 83, 32);
            row.Remove.SetBounds(269, y, 31, 32);
        }
        _presetArea.Height = _presets.Count * 37;
        _addPreset.SetBounds(25, _presetArea.Bottom + 5, 112, 32);
        _addPreset.Enabled = _presets.Count < 8;
    }


    private void BuildNetwork()
    {
        var page = Page("Network", "Connections, schedule and privacy.");
        var connLabel = Label("Download connections", 9, bold: true);
        connLabel.SetBounds(25, 108, 300, 22);
        var connNote = Label("Downloads a file in parts when supported. Four is a good default.", 8, muted: true);
        connNote.SetBounds(25, 133, 360, 20);
        int[] counts = [1, 2, 3, 4, 6, 8];
        var connections = Choice(["1", "2", "3", "4", "6", "8"], Math.Max(0, Array.IndexOf(counts, _draft.MaxConnectionsPerJob)), 126);
        connections.SetBounds(404, 109, 126, 33);
        connections.SelectedIndexChanged += (_, _) => _draft.MaxConnectionsPerJob = int.Parse(connections.SelectedText);
        var separator1 = Divider(169);

        var schedule = Check("Only download between set hours", 25, 185, _draft.ScheduleEnabled, v => { _draft.ScheduleEnabled = v; UpdateSchedule(); });
        var scheduleNote = Label("Outside this window nothing starts and running downloads pause until it opens again.", 8, muted: true);
        scheduleNote.SetBounds(45, 211, 490, 20);
        (_scheduleStartFrame, _scheduleStart) = Input(_draft.ScheduleStart, 90);
        _scheduleStartFrame.Location = new Point(25, 240);
        var to = Label("to", 9, muted: true);
        to.SetBounds(124, 240, 24, 34);
        (_scheduleEndFrame, _scheduleEnd) = Input(_draft.ScheduleEnd, 90);
        _scheduleEndFrame.Location = new Point(152, 240);
        var scheduleHint = Label("24-hour times, e.g. 01:00 to 07:00", 8, muted: true);
        scheduleHint.SetBounds(255, 247, 270, 20);
        var separator2 = Divider(291);

        var proxyLabel = Label("Proxy", 9, bold: true);
        proxyLabel.SetBounds(25, 303, 250, 22);
        var proxyNote = Label("Most people can leave this on Direct. Use a proxy only if needed.", 8, muted: true);
        proxyNote.SetBounds(25, 328, 360, 20);
        int proxyIndex = _draft.ProxyMode switch { "System" => 1, "Custom" => 2, _ => 0 };
        var proxy = Choice(["Direct", "System", "Custom"], proxyIndex, 126);
        proxy.SetBounds(404, 304, 126, 33);
        (_proxyFrame, _proxyAddress) = Input(_draft.ProxyAddress, 505);
        _proxyFrame.Location = new Point(25, 356);
        _proxyAddress.PlaceholderText = "http://proxy.example:8080";
        proxy.SelectedIndexChanged += (_, _) =>
        {
            _draft.ProxyMode = proxy.SelectedText;
            _proxyFrame.Visible = proxy.SelectedText == "Custom";
        };
        _proxyFrame.Visible = proxyIndex == 2;
        var separator3 = Divider(404);

        var privateNetworks = Check("Allow downloads from local devices", 25, 418, _draft.AllowPrivateNetworks, v => _draft.AllowPrivateNetworks = v);
        var privateNote = Label("Only turn this on for files on your home network, such as a NAS. Off by default for safety. Requires restart.", 8, muted: true);
        privateNote.SetBounds(45, 446, 480, 34);
        privateNote.AutoEllipsis = false;
        var sleep = Check("Keep the PC awake while downloads are running", 25, 488, _draft.PreventSleep, v => _draft.PreventSleep = v);

        page.Controls.AddRange([connLabel, connNote, connections, separator1, schedule, scheduleNote, _scheduleStartFrame, to, _scheduleEndFrame,
            scheduleHint, separator2, proxyLabel, proxyNote, proxy, _proxyFrame, separator3, privateNetworks, privateNote, sleep]);
        void Layout()
        {
            int right = page.ClientSize.Width - 25;
            connections.Left = Math.Max(292, right - connections.Width);
            proxy.Left = Math.Max(292, right - proxy.Width);
            _proxyFrame.Width = Math.Max(200, right - 25);
            scheduleNote.Width = Math.Max(200, right - 45);
            privateNote.Width = Math.Max(200, right - 45);
        }
        page.Resize += (_, _) => Layout();
        Layout();
        UpdateSchedule();
    }

    private void UpdateSchedule()
    {
        if (_scheduleStart is null || _scheduleEnd is null) return;
        bool on = _draft.ScheduleEnabled;
        _scheduleStart.Enabled = _scheduleEnd.Enabled = on;
        _scheduleStartFrame.Invalidate();
        _scheduleEndFrame.Invalidate();
    }

    private void BuildBrowser()
    {
        var page = Page("Browser", "The browser butterfly sends supported downloads to VESPER.");
        var heading = Label("Browser downloads", 10, bold: true);
        heading.SetBounds(25, 109, 390, 25);
        var note = Label("VESPER works without an extension. Install the browser add-on to capture downloads while browsing.", 8.5F, muted: true);
        note.SetBounds(25, 137, 505, 43);
        note.AutoEllipsis = false;

        var statusTitle = Label("Desktop helper", 9, bold: true);
        statusTitle.SetBounds(25, 190, 230, 23);
        var status = Label("", 8.5F, muted: true);
        status.SetBounds(25, 213, 505, 44);
        status.AutoEllipsis = false;
        void Refresh()
        {
            status.Text = !NativeHostSetup.HostPresent
                ? "The browser helper isn't included in this development build. The release installer installs it automatically."
                : NativeHostSetup.IsRegistered
                    ? "Desktop helper is registered. Enable the VESPER extension in your browser to start using it."
                    : "Desktop helper found. The release installer normally registers it automatically.";
        }

        var sep1 = Divider(265);
        var handoff = Label("When the butterfly captures a download", 9, bold: true);
        handoff.SetBounds(25, 283, 320, 22);
        var action = Choice(["Ask me", "Download now", "Add to queue"], (int)_draft.BrowserAction, 190);
        action.SetBounds(335, 281, 190, 33);
        action.SelectedIndexChanged += (_, _) => _draft.BrowserAction = (BrowserAction)action.SelectedIndex;
        var handoffNote = Label("Choose what VESPER should do after the browser extension sends a file.", 8.5F, muted: true);
        handoffNote.SetBounds(25, 320, 505, 26);

        var sep2 = Divider(361);
        var extensionTitle = Label("Extension setup", 9, bold: true);
        extensionTitle.SetBounds(25, 377, 290, 22);
        var extensionNote = Label(NativeHostSetup.ExtensionPresent
            ? "Development build: load the add-on from the extension folder. A Store extension will require your approval."
            : "The extension is not bundled here. Browser add-ons require your approval to install and enable.",
            8.5F, muted: true);
        extensionNote.SetBounds(25, 402, 505, 42);
        extensionNote.AutoEllipsis = false;
        var open = Button("Open extension files", 178, symbol: VesperIcon.Folder);
        open.SetBounds(25, 448, 178, 34);
        open.Enabled = NativeHostSetup.ExtensionPresent;
        open.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{NativeHostSetup.ExtensionFolder}\"") { UseShellExecute = false }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { VesperMessageBox.Show(this, ex.Message, "Open extension folder", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        var repair = Button("Repair helper", 131, symbol: VesperIcon.Refresh);
        repair.SetBounds(213, 448, 131, 34);
        repair.Enabled = NativeHostSetup.HostPresent;
        repair.Click += (_, _) =>
        {
            string? error = NativeHostSetup.Register();
            if (error is not null) VesperMessageBox.Show(this, error, "Repair browser helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Refresh();
        };
        var developerTip = Label("For unpacked Chrome builds: chrome://extensions → Developer mode → Load unpacked.", 8, muted: true);
        developerTip.SetBounds(25, 489, 505, 33);

        page.Controls.AddRange([heading, note, statusTitle, status, sep1, handoff, action,
            handoffNote, sep2, extensionTitle, extensionNote, open, repair, developerTip]);
        page.Resize += (_, _) =>
        {
            int right = page.ClientSize.Width - 25;
            action.Left = Math.Max(315, right - action.Width);
            foreach (Label l in new[] { note, status, handoffNote, extensionNote, developerTip })
                l.Width = Math.Max(180, right - 25);
            handoff.Width = Math.Max(180, action.Left - 36);
        };
        Refresh();
    }

    private void BuildAppearance()
    {
        var page = Page("Appearance", "Choose how VESPER looks.");
        var theme = Label("Color theme", 9, bold: true);
        theme.SetBounds(25, 107, 240, 23);
        var themeNote = Label("Match your preferred light or dark palette.", 8, muted: true);
        themeNote.SetBounds(25, 134, 245, 29);
        var themeChoice = Choice(["System", "Light", "Dark"], (int)_draft.Theme, 184);
        themeChoice.SetBounds(346, 112, 184, 33);
        themeChoice.SelectedIndexChanged += (_, _) => _draft.Theme = (ThemeChoice)themeChoice.SelectedIndex;
        var separator1 = Divider(184);

        var density = Label("Download row height", 9, bold: true);
        density.SetBounds(25, 201, 250, 23);
        var densityNote = Label("Gallery shows media thumbnails. Compact is the default.", 8, muted: true);
        densityNote.SetBounds(25, 226, 260, 35);
        var densityChoice = Choice(["Compact · 46 px (default)", "Extra compact · 34 px", "Comfortable · 58 px", "Gallery · 84 px"],
            _draft.Density switch { Density.ExtraCompact => 1, Density.Comfortable => 2, Density.Gallery => 3, _ => 0 }, 204);
        densityChoice.SetBounds(326, 207, 204, 33);
        densityChoice.SelectedIndexChanged += (_, _) => _draft.Density = densityChoice.SelectedIndex switch
        { 1 => Density.ExtraCompact, 2 => Density.Comfortable, 3 => Density.Gallery, _ => Density.Compact };
        var separator2 = Divider(279);

        var botanical = Check("Botanical theme", 25, 303, _draft.Botanical,
            value => _draft.Botanical = value);
        var botanicalNote = Label("Illustrated headers, watercolor scenery, and a warm palette.", 8, muted: true);
        botanicalNote.SetBounds(45, 331, 470, 34);
        page.Controls.AddRange([theme, themeNote, themeChoice, separator1, density, densityNote,
            densityChoice, separator2, botanical, botanicalNote]);
        void Layout()
        {
            int right = page.ClientSize.Width - 25;
            themeChoice.Left = Math.Max(300, right - themeChoice.Width);
            densityChoice.Left = Math.Max(282, right - densityChoice.Width);
            int leftWidth = Math.Max(160, densityChoice.Left - 40);
            themeNote.Width = densityNote.Width = leftWidth;
            botanicalNote.Width = Math.Max(225, page.ClientSize.Width - 70);
        }
        page.Resize += (_, _) => Layout();
        Layout();
    }

    private void Save()
    {
        if (_globalEnabled.Checked)
        {
            if (!int.TryParse(_globalInput.Text.Trim(), out int limit) || limit is < 1 or > 10000000)
            {
                ShowPage("Transfers");
                _globalInput.Focus();
                VesperMessageBox.Show(this, "Enter a global speed limit between 1 and 10,000,000 KB/s.", "Invalid limit",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _draft.GlobalRateLimitKbs = limit;
        }
        else _draft.GlobalRateLimitKbs = 0;

        var values = new List<int>();
        foreach (var row in _presets)
        {
            if (!int.TryParse(row.Value.Text.Trim(), out int amount) || amount < 1)
            {
                InvalidPreset(row); return;
            }
            long kb = (long)amount * (row.Unit.SelectedText == "MB/s" ? 1000 : 1);
            if (kb > 10000000 || values.Contains((int)kb))
            {
                InvalidPreset(row); return;
            }
            values.Add((int)kb);
        }
        if (string.IsNullOrWhiteSpace(_draft.DefaultFolder) || !Path.IsPathFullyQualified(_draft.DefaultFolder))
        {
            ShowPage("Downloads");
            VesperMessageBox.Show(this, "Choose a valid absolute default folder path.", "Invalid folder",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_draft.ScheduleEnabled && (!TimeOnly.TryParse(_scheduleStart.Text, out _) || !TimeOnly.TryParse(_scheduleEnd.Text, out _)))
        {
            ShowPage("Network");
            VesperMessageBox.Show(this, "Enter the download hours as 24-hour times, for example 01:00 and 07:00.", "Invalid hours", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _draft.ScheduleStart = _scheduleStart.Text.Trim();
        _draft.ScheduleEnd = _scheduleEnd.Text.Trim();
        if (_draft.ProxyMode == "Custom" && !Vesper.Engine.UrlPolicy.TryParse(_proxyAddress.Text, out _, out string proxyProblem))
        {
            ShowPage("Network");
            VesperMessageBox.Show(this, "Enter the proxy as a full address such as http://proxy.example:8080.\n" + proxyProblem, "Invalid proxy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _draft.ProxyAddress = _proxyAddress.Text.Trim();
        _draft.BandwidthPresetsKbs = values;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void InvalidPreset(PresetRow row)
    {
        ShowPage("Transfers");
        row.Value.Focus();
        VesperMessageBox.Show(this, "Enter positive, unique bandwidth presets (up to 10,000,000 KB/s each).",
            "Invalid preset", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _navFont.Dispose(); _navActiveFont.Dispose(); }
        base.Dispose(disposing);
    }
}
