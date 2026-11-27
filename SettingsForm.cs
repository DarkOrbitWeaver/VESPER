using Vesper.Engine;

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
    private readonly DownloadEngine? _engine;
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
    private TextBox _fileNameTemplate = null!;
    private ListBox _siteRuleList = null!;
    private TextBox _siteHost = null!;
    private TextBox _siteFolder = null!;
    private CheckBox _siteAudio = null!;
    private CheckBox _siteNeverCapture = null!;
    private bool _loadingSiteRule;
    private bool _siteRuleDirty;
    private Func<bool>? _saveSiteRuleEditor;

    public VesperPreferences Result => _draft.Clone();
    public SettingsForm() : this(new VesperPreferences()) { }

    public SettingsForm(VesperPreferences preferences, DownloadEngine? engine = null,
        Action<IWin32Window>? importDownloadList = null, Action<IWin32Window>? exportDownloadList = null)
    {
        _draft = preferences.Clone();
        _engine = engine;
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
            ("Appearance", VesperIcon.Leaf), ("Browser", VesperIcon.Link),
            ("About", VesperIcon.Info)
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

        BuildDownloads(importDownloadList, exportDownloadList);
        BuildTransfers();
        BuildNetwork();
        BuildAppearance();
        BuildSiteRules();
        BuildBrowser();
        BuildAbout();
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

    private int S(int logicalPixels) => UiKit.Scale(this, logicalPixels);

    private VesperChoice Choice(string[] options, int selected, int width, string accessibleName)
    {
        var control = new VesperChoice
        {
            Width = width, Height = 33,
            AccessibleName = accessibleName
        };
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
            intro.Width = Math.Max(S(200), page.ClientSize.Width - S(50));
            divider.Width = Math.Max(S(200), page.ClientSize.Width - S(50));
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
        string section = tab == "Sites" ? "Browser" : tab;
        foreach (var (name, nav) in _navigation)
        {
            nav.NavigationActive = name == section;
            nav.Font = name == section ? _navActiveFont : _navFont;
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

    private void BuildDownloads(Action<IWin32Window>? importDownloadList, Action<IWin32Window>? exportDownloadList)
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
            if (PathSafety.TryGetFullPath(folder.Text, out string selected, out _) && Directory.Exists(selected))
                picker.SelectedPath = selected;
            if (picker.ShowDialog(this) == DialogResult.OK) folder.Text = picker.SelectedPath;
        };
        var separator1 = Divider(219);
        var categories = Check("Create category subfolders", 25, 236, _draft.CategoryFolders, v => _draft.CategoryFolders = v);
        var categoryNote = Label("Organize future files into Music, Video, Archives and other folders.", 8, muted: true);
        categoryNote.SetBounds(45, 265, 470, 23);
        var separator2 = Divider(305);
        var templateHeading = Label("Name new files", 9, bold: true);
        templateHeading.SetBounds(25, 317, 400, 22);
        var templateNote = Label("{name} adds the title. {host} adds the site. Leave blank to keep the original name.", 8, muted: true);
        templateNote.SetBounds(25, 342, 500, 27);
        var (templateFrame, templateInput) = Input(_draft.FileNameTemplate, 500);
        templateFrame.SetBounds(25, 371, 500, 34);
        templateInput.AccessibleName = "Filename template";
        templateInput.PlaceholderText = "{name} - {host}";
        _fileNameTemplate = templateInput;
        var templatePreview = Label("", 8, muted: true);
        templatePreview.SetBounds(25, 408, 500, 22);
        void UpdateTemplatePreview()
        {
            _draft.FileNameTemplate = templateInput.Text;
            if (string.IsNullOrWhiteSpace(templateInput.Text))
            {
                templatePreview.Text = "Example: {name} - {host} → Coastal walk - media.example.mp4";
            }
            else if (!FileNames.TryValidateTemplate(templateInput.Text, out string problem))
            {
                templatePreview.Text = "Template issue: " + problem;
            }
            else
            {
                templatePreview.Text = "Preview: " + FileNames.ApplyTemplate("Coastal walk.mp4", templateInput.Text,
                    new Uri("https://media.example/"));
            }
        }
        templateInput.TextChanged += (_, _) => UpdateTemplatePreview();
        UpdateTemplatePreview();
        var separator3 = Divider(444);
        var missing = Check("Show completed downloads whose files are missing", 25, 461, _draft.ShowMissingCompleted, v => _draft.ShowMissingCompleted = v);
        var missingNote = Label("Paused and recoverable downloads are always shown.", 8, muted: true);
        missingNote.SetBounds(45, 490, 470, 37);
        var separator4 = Divider(535);
        var notify = Check("Notify me when downloads finish or need attention", 25, 547, _draft.NotifyOnFinish, v => _draft.NotifyOnFinish = v);
        var sound = Check("Play completion sound", 25, 575, _draft.PlayCompletionSound, v =>
        {
            _draft.PlayCompletionSound = v;
            if (!v) VesperSounds.Stop();
        });
        var previewSound = Button("Preview sound", 140, symbol: VesperIcon.Audio);
        previewSound.SetBounds(257, 574, 140, 29);
        sound.Width = Math.Max(UiKit.Scale(this, 120), previewSound.Left - sound.Left - UiKit.Scale(this, 8));
        previewSound.Click += (_, _) => VesperSounds.PlayCompletion();
        var flashOnCompletion = Check("Flash the taskbar when a download finishes", 25, 612,
            _draft.FlashTaskbarOnCompletion, v => _draft.FlashTaskbarOnCompletion = v);
        const int completionRow = 28;
        var clipboard = Check("Offer to download links I copy", 25, 612 + completionRow, _draft.WatchClipboard, v => _draft.WatchClipboard = v);
        var tray = Check("Keep VESPER in the tray when I close the window", 25, 640 + completionRow, _draft.CloseToTray, v => _draft.CloseToTray = v);
        var separator5 = Divider(679 + completionRow);
        var openFolder = Check("Open the latest download folder when the queue finishes", 25, 692 + completionRow,
            _draft.OpenFolderWhenQueueFinishes, value => _draft.OpenFolderWhenQueueFinishes = value);
        var sleepQueue = Check("Ask before putting the PC to sleep when the queue finishes", 25, 723 + completionRow,
            _draft.SleepWhenQueueFinishes, value => _draft.SleepWhenQueueFinishes = value);
        var shutdownQueue = Check("Ask before shutting down the PC when the queue finishes", 25, 754 + completionRow,
            _draft.ShutdownWhenQueueFinishes, value => _draft.ShutdownWhenQueueFinishes = value);
        var powerNote = Label("The prompt closes after 60 seconds or when new downloads start.", 8, muted: true);
        powerNote.SetBounds(45, 782 + completionRow, 470, 28);
        page.Controls.AddRange([folderLabel, folderNote, frame, browse, separator1, categories, categoryNote,
            separator2, templateHeading, templateNote, templateFrame, templatePreview, separator3, missing, missingNote,
            separator4, notify, sound, previewSound, flashOnCompletion, clipboard, tray, separator5, openFolder,
            sleepQueue, shutdownQueue, powerNote]);
        if (importDownloadList is not null && exportDownloadList is not null)
        {
            var lists = Label("Download lists", 9, bold: true);
            lists.SetBounds(25, 833 + completionRow, 250, 22);
            var import = Button("Import list…", 134, symbol: VesperIcon.Folder);
            import.SetBounds(25, 863 + completionRow, 134, 34);
            import.Click += (_, _) => importDownloadList(this);
            var export = Button("Export list…", 134, symbol: VesperIcon.Clipboard);
            export.SetBounds(169, 863 + completionRow, 134, 34);
            export.Click += (_, _) => exportDownloadList(this);
            page.Controls.AddRange([Divider(820 + completionRow), lists, import, export]);
        }
        void Layout()
        {
            int right = page.ClientSize.Width - S(25);
            browse.Left = Math.Max(S(308), right - browse.Width);
            frame.Width = Math.Max(S(180), browse.Left - S(34));
            int noteWidth = Math.Max(S(210), right - S(45));
            categoryNote.Width = missingNote.Width = templateNote.Width = templatePreview.Width = powerNote.Width = noteWidth;
            templateFrame.Width = Math.Max(S(180), right - S(50));
        }
        page.Resize += (_, _) => Layout();
        Layout();
    }

    private void BuildSiteRules()
    {
        var page = Page("Sites", "Save downloads from a site to a folder, prefer audio, or leave them in Chrome.");
        var back = Button("Back to Browser", 145);
        back.SetBounds(380, 98, 145, 32);
        back.Click += (_, _) => ShowPage("Browser");
        var listLabel = Label("Saved rules", 9, bold: true);
        listLabel.SetBounds(25, 104, 210, 22);
        _siteRuleList = new ListBox
        {
            Name = "siteRuleList", AccessibleName = "Site rules", IntegralHeight = false,
            Location = new Point(25, 134), Size = new Size(505, 142),
            BackColor = _colors.Surface, ForeColor = _colors.Text, Font = new Font("Segoe UI", 9)
        };
        var hostLabel = Label("Website name", 9, bold: true);
        hostLabel.SetBounds(25, 287, 250, 22);
        var (hostFrame, host) = Input("", 350);
        hostFrame.SetBounds(25, 313, 350, 34);
        host.Name = "siteHostInput";
        host.AccessibleName = "Website host";
        host.PlaceholderText = "www.example.com";
        _siteHost = host;
        var audio = Check("Prefer audio-only media", 25, 354, false, _ => { });
        audio.Name = "siteAlwaysAudio";
        audio.AccessibleName = "Prefer audio-only media on this site";
        _siteAudio = audio;
        var capture = Check("Keep automatic browser downloads in Chrome", 25, 384, false, _ => { });
        capture.Name = "siteNeverCapture";
        capture.AccessibleName = "Keep automatic browser downloads in Chrome for this site";
        _siteNeverCapture = capture;

        var folderLabel = Label("Default folder for this site", 9, bold: true);
        folderLabel.SetBounds(25, 418, 310, 22);
        var (folderFrame, folder) = Input("", 385);
        folderFrame.SetBounds(25, 445, 385, 34);
        folder.Name = "siteFolderInput";
        folder.AccessibleName = "Website download folder";
        _siteFolder = folder;
        var browse = Button("Browse…", 96);
        browse.SetBounds(434, 445, 96, 34);
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "Folder for this website", UseDescriptionForTitle = true };
            if (PathSafety.TryGetFullPath(_siteFolder.Text, out string selected, out _) && Directory.Exists(selected))
                picker.SelectedPath = selected;
            if (picker.ShowDialog(this) == DialogResult.OK) _siteFolder.Text = picker.SelectedPath;
        };

        var saveRule = Button("Add rule", 130, symbol: VesperIcon.Plus);
        saveRule.Name = "saveSiteRule";
        saveRule.AccessibleName = "Add or update site rule";
        saveRule.SetBounds(25, 492, 130, 34);
        var removeRule = Button("Remove rule", 130, symbol: VesperIcon.Trash);
        removeRule.Name = "removeSiteRule";
        removeRule.SetBounds(163, 492, 130, 34);
        var note = Label("Use the site name from Chrome’s address bar. www.example.com and example.com need separate rules.",
            8, muted: true);
        note.SetBounds(25, 538, 505, 42);
        note.AutoEllipsis = false;

        void MarkDirty()
        {
            if (!_loadingSiteRule) _siteRuleDirty = true;
        }

        void LoadSelectedRule()
        {
            _loadingSiteRule = true;
            int selected = _siteRuleList.SelectedIndex;
            if (selected >= 0 && selected < _draft.SiteRules.Count)
            {
                SiteRule rule = _draft.SiteRules[selected];
                _siteHost.Text = rule.Host;
                _siteAudio.Checked = rule.AlwaysAudio;
                _siteFolder.Text = rule.Folder ?? "";
                _siteNeverCapture.Checked = rule.NeverCapture;
                saveRule.Text = "Update rule";
                removeRule.Enabled = true;
            }
            else
            {
                _siteHost.Clear();
                _siteAudio.Checked = false;
                _siteFolder.Clear();
                _siteNeverCapture.Checked = false;
                saveRule.Text = "Add rule";
                removeRule.Enabled = false;
            }
            _loadingSiteRule = false;
            _siteRuleDirty = false;
        }

        void RefreshRules(int selected = -1)
        {
            _loadingSiteRule = true;
            _siteRuleList.BeginUpdate();
            _siteRuleList.Items.Clear();
            foreach (SiteRule rule in _draft.SiteRules)
            {
                var choices = new List<string>();
                if (rule.AlwaysAudio) choices.Add("audio");
                if (rule.Folder is { Length: > 0 }) choices.Add("folder set");
                if (rule.NeverCapture) choices.Add("keep in Chrome");
                _siteRuleList.Items.Add(rule.Host + "  ·  " + string.Join("  ·  ", choices));
            }
            _siteRuleList.SelectedIndex = selected >= 0 && selected < _siteRuleList.Items.Count ? selected : -1;
            _siteRuleList.EndUpdate();
            _loadingSiteRule = false;
            LoadSelectedRule();
        }

        bool SaveSiteRule()
        {
            if (!SiteRule.TryNormalizeHost(_siteHost.Text, out string host))
            {
                ShowPage("Sites");
                _siteHost.Focus();
                VesperMessageBox.Show(this, "Enter a website host such as example.com. Do not include a page path, port, or wildcard.",
                    "Invalid website host", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            string? folder = null;
            if (!string.IsNullOrWhiteSpace(_siteFolder.Text))
            {
                if (!PathSafety.TryGetFullPath(_siteFolder.Text, out folder, out _))
                {
                    ShowPage("Sites");
                    _siteFolder.Focus();
                    VesperMessageBox.Show(this, "Choose a valid absolute folder path.", "Invalid site folder",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
            }
            if (!_siteAudio.Checked && folder is null && !_siteNeverCapture.Checked)
            {
                ShowPage("Sites");
                VesperMessageBox.Show(this, "Choose at least one site rule before saving.", "No site rule selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            int selected = _siteRuleList.SelectedIndex;
            int existing = _draft.SiteRules.FindIndex(rule => string.Equals(rule.Host, host, StringComparison.OrdinalIgnoreCase));
            if (selected >= 0 && existing >= 0 && selected != existing)
            {
                ShowPage("Sites");
                VesperMessageBox.Show(this, "A rule for that host already exists. Select it to make changes.",
                    "Duplicate site rule", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            int index = selected >= 0 ? selected : existing;
            if (index < 0 && _draft.SiteRules.Count >= 100)
            {
                VesperMessageBox.Show(this, "VESPER supports up to 100 site rules.", "Site rule limit",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            var updated = new SiteRule
            {
                Host = host, AlwaysAudio = _siteAudio.Checked, Folder = folder, NeverCapture = _siteNeverCapture.Checked
            };
            if (index < 0)
            {
                index = _draft.SiteRules.Count;
                _draft.SiteRules.Add(updated);
            }
            else _draft.SiteRules[index] = updated;
            RefreshRules(index);
            return true;
        }

        _saveSiteRuleEditor = SaveSiteRule;

        _siteRuleList.SelectedIndexChanged += (_, _) => LoadSelectedRule();
        _siteHost.TextChanged += (_, _) => MarkDirty();
        _siteFolder.TextChanged += (_, _) => MarkDirty();
        _siteAudio.CheckedChanged += (_, _) => MarkDirty();
        _siteNeverCapture.CheckedChanged += (_, _) => MarkDirty();
        saveRule.Click += (_, _) => SaveSiteRule();
        removeRule.Click += (_, _) =>
        {
            int index = _siteRuleList.SelectedIndex;
            if (index < 0 || index >= _draft.SiteRules.Count) return;
            _draft.SiteRules.RemoveAt(index);
            RefreshRules(Math.Min(index, _draft.SiteRules.Count - 1));
        };
        page.Controls.AddRange([back, listLabel, _siteRuleList, hostLabel, hostFrame, audio, capture,
            folderLabel, folderFrame, browse, saveRule, removeRule, note]);
        void Layout()
        {
            int available = page.ClientSize.Width - S(50);
            int gap = S(8);
            _siteRuleList.Width = Math.Max(S(200), available);
            back.Left = _siteRuleList.Right - back.Width;
            hostFrame.Width = Math.Max(S(200), available);
            folderFrame.Width = Math.Max(S(200), available - browse.Width - gap);
            browse.Left = folderFrame.Right + gap;
            note.Width = Math.Max(S(200), available);
        }
        page.Resize += (_, _) => Layout();
        Layout();
        RefreshRules();
    }

    private void BuildTransfers()
    {
        var page = Page("Transfers", "How many downloads run at once, and how fast.");
        var concurrencyLabel = Label("Simultaneous downloads", 9, bold: true);
        concurrencyLabel.SetBounds(25, 108, 280, 22);
        int[] counts = [1, 2, 3, 4, 5, 6, 8];
        var concurrency = Choice(["1", "2", "3", "4", "5", "6", "8"],
            Math.Max(0, Array.IndexOf(counts, _draft.ConcurrentTransfers)), 126, "Simultaneous downloads");
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
        page.Controls.AddRange([concurrencyLabel, concurrency, separator1,
            _globalEnabled, globalNote, _globalFrame, unit, separator2,
            presetHeading, presetDescription, _presetArea, _addPreset]);
        void Layout()
        {
            concurrency.Left = Math.Max(S(292), page.ClientSize.Width - S(25) - concurrency.Width);
            globalNote.Width = Math.Max(S(200), page.ClientSize.Width - S(70));
            unit.Width = Math.Max(S(160), page.ClientSize.Width - S(204));
            bool showLimit = _globalEnabled.Checked;
            _globalFrame.Visible = unit.Visible = showLimit;
            int offset = showLimit ? 0 : -S(44);
            separator2.Top = S(291) + offset;
            presetHeading.Top = S(303) + offset;
            presetDescription.Top = S(326) + offset;
            _presetArea.Top = S(354) + offset;
            _presetArea.Width = Math.Max(S(315), page.ClientSize.Width - S(50));
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
        var unit = Choice(["KB/s", "MB/s"], 0, 83, $"{caption.Text} unit");
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
            int y = i * S(37);
            var row = _presets[i];
            row.Caption.Text = $"Preset {i + 1}";
            row.Caption.SetBounds(0, y, S(59), S(33));
            row.Frame.SetBounds(S(66), y, S(105), S(32));
            row.Unit.SetBounds(S(178), y, S(83), S(32));
            row.Remove.SetBounds(S(269), y, S(31), S(32));
        }
        _presetArea.Height = _presets.Count * S(37);
        _addPreset.SetBounds(S(25), _presetArea.Bottom + S(5), S(112), S(32));
        _addPreset.Enabled = _presets.Count < 8;
    }


    private void BuildNetwork()
    {
        var page = Page("Network", "Connections, schedule and privacy.");
        var connLabel = Label("Connections per download", 9, bold: true);
        connLabel.SetBounds(25, 108, 300, 22);
        var connNote = Label("Downloads use parts when supported; eight is a good default.", 8, muted: true);
        connNote.SetBounds(25, 133, 360, 20);
        int[] counts = [1, 2, 3, 4, 6, 8];
        var connections = Choice(["1", "2", "3", "4", "6", "8"],
            Math.Max(0, Array.IndexOf(counts, _draft.MaxConnectionsPerJob)), 126, "Connections per download");
        connections.SetBounds(404, 109, 126, 33);
        connections.SelectedIndexChanged += (_, _) => _draft.MaxConnectionsPerJob = int.Parse(connections.SelectedText);
        var separator1 = Divider(169);

        var schedule = Check("Only download between set hours", 25, 185, _draft.ScheduleEnabled, v => { _draft.ScheduleEnabled = v; UpdateSchedule(); });
        var scheduleNote = Label("Downloads wait outside these hours; active downloads pause.", 8, muted: true);
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
        var proxyNote = Label("Direct is fine for most people. Use a proxy if needed.", 8, muted: true);
        proxyNote.SetBounds(25, 328, 360, 20);
        int proxyIndex = _draft.ProxyMode switch { "System" => 1, "Custom" => 2, _ => 0 };
        var proxy = Choice(["Direct", "System", "Custom"], proxyIndex, 126, "Proxy mode");
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

        var privateNetworks = Check("Allow direct file downloads from local devices", 25, 418, _draft.AllowPrivateNetworks, v => _draft.AllowPrivateNetworks = v);
        var privateNote = Label("For files on your home network, such as a NAS. Restart required.", 8, muted: true);
        privateNote.SetBounds(45, 446, 480, 34);
        privateNote.AutoEllipsis = false;
        var sleep = Check("Keep the PC awake while downloads are running", 25, 488, _draft.PreventSleep, v => _draft.PreventSleep = v);
        var metered = Check("Pause on metered or unknown networks", 25, 532, _draft.PauseOnMeteredNetwork,
            v => _draft.PauseOnMeteredNetwork = v);
        var meteredNote = Label("When enabled, Windows must report an unmetered connection. Choose Start now to allow one download.", 8, muted: true);
        meteredNote.SetBounds(45, 559, 480, 34);
        meteredNote.AutoEllipsis = false;

        page.Controls.AddRange([connLabel, connNote, connections, separator1, schedule, scheduleNote, _scheduleStartFrame, to, _scheduleEndFrame,
            scheduleHint, separator2, proxyLabel, proxyNote, proxy, _proxyFrame, separator3, privateNetworks, privateNote, sleep,
            metered, meteredNote]);
        page.AutoScrollMinSize = new Size(0, 610);
        void Layout()
        {
            int right = page.ClientSize.Width - S(25);
            connections.Left = Math.Max(S(292), right - connections.Width);
            connNote.Width = Math.Max(S(200), connections.Left - connNote.Left - S(8));
            proxy.Left = Math.Max(S(292), right - proxy.Width);
            proxyNote.Width = Math.Max(S(200), proxy.Left - proxyNote.Left - S(8));
            _proxyFrame.Width = Math.Max(S(200), right - S(25));
            scheduleNote.Width = Math.Max(S(200), right - S(45));
            scheduleHint.Width = Math.Max(S(120), right - scheduleHint.Left);
            privateNote.Width = Math.Max(S(200), right - S(45));
            meteredNote.Width = Math.Max(S(200), right - S(45));
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
        var page = Page("Browser", "Send files and videos from Chrome, Edge, or Brave.");
        var status = Label("", 8.5F, muted: true);
        status.SetBounds(25, 134, 505, 34);
        status.AutoEllipsis = false;
        var extensionTitle = Label("Browser extension", 9, bold: true);
        extensionTitle.SetBounds(25, 110, 290, 22);
        var open = Button("Open extension files", 178, symbol: VesperIcon.Folder);
        open.SetBounds(25, 236, 178, 34);
        open.Enabled = NativeHostSetup.ExtensionPresent;
        open.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{NativeHostSetup.ExtensionFolder}\"") { UseShellExecute = false }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { VesperMessageBox.Show(this, ex.Message, "Open extension folder", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        bool installed = NativeHostSetup.IsInstalledApp;
        var helperAction = Button("Connect browser helper", 210, symbol: VesperIcon.Link);
        helperAction.SetBounds(213, 236, 210, 34);
        var developerTip = Label(NativeHostSetup.ExtensionPresent
            ? "Load the extension in Chrome, Edge, or Brave from the browser's extension settings."
            : "This build does not include the extension files.", 8, muted: true);
        developerTip.SetBounds(25, 277, 505, 35);
        var siteRules = Button("Site rules…", 142, symbol: VesperIcon.Provider);
        siteRules.SetBounds(25, 320, 142, 34);
        siteRules.Click += (_, _) => ShowPage("Sites");
        helperAction.Click += (_, _) =>
        {
            NativeHostRegistrationStatus registrationStatus = NativeHostSetup.RegistrationStatus;
            if (registrationStatus == NativeHostRegistrationStatus.Unknown) { RefreshHelper(); return; }
            if (registrationStatus == NativeHostRegistrationStatus.Registered && installed) { RefreshHelper(); return; }

            bool disconnect = registrationStatus == NativeHostRegistrationStatus.Registered;
            bool repair = registrationStatus == NativeHostRegistrationStatus.RepairRequired;
            string title = disconnect ? "Disconnect browser helper"
                : repair ? "Repair browser connection" : "Connect browser helper";
            string prompt = disconnect
                ? $"Disconnect this copy's helper registration from {NativeHostRegistration.BrowserList} for your Windows account?"
                : repair
                    ? $"Repair the browser helper registration for {NativeHostRegistration.BrowserList}? This rewrites the shared per-user helper manifest. If another VESPER copy uses this registration, it will be replaced."
                    : registrationStatus == NativeHostRegistrationStatus.AnotherCopyRegistered
                        ? $"At least one of Chrome, Microsoft Edge, or Brave already has a different browser helper manifest registered. Connect this copy of VESPER to replace it in {NativeHostRegistration.BrowserList} for your Windows account?"
                        : $"Connect this copy of VESPER to {NativeHostRegistration.BrowserList} for your Windows account?";
            if (VesperMessageBox.Show(this, prompt, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string? error = disconnect ? NativeHostSetup.Unregister()
                : repair ? NativeHostSetup.Repair()
                : NativeHostSetup.Register(replaceExisting: registrationStatus == NativeHostRegistrationStatus.AnotherCopyRegistered);
            if (error is not null) VesperMessageBox.Show(this, error, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshHelper();
        };

        page.Controls.AddRange([extensionTitle, status, open, helperAction, developerTip, siteRules]);
        void RefreshHelper()
        {
            bool hostPresent = NativeHostSetup.HostPresent;
            NativeHostRegistrationStatus registrationStatus = NativeHostSetup.RegistrationStatus;
            status.Text = !hostPresent ? "This build is missing the browser helper."
                : registrationStatus switch
                {
                    NativeHostRegistrationStatus.Registered => $"Registered for {NativeHostRegistration.BrowserList} for your Windows account.",
                    NativeHostRegistrationStatus.NotRegistered => "The browser helper is not registered in Chrome, Microsoft Edge, or Brave.",
                    NativeHostRegistrationStatus.RepairRequired => "The browser helper registration needs repair.",
                    NativeHostRegistrationStatus.AnotherCopyRegistered => "A different browser helper manifest is registered for one or more browsers.",
                    _ => "Could not check the browser helper registration. No changes were made."
                };
            helperAction.Visible = ShouldShowBrowserHelperAction(hostPresent, registrationStatus, installed);
            helperAction.Text = registrationStatus switch
            {
                NativeHostRegistrationStatus.Registered => "Disconnect browser helper",
                NativeHostRegistrationStatus.RepairRequired => "Repair browser connection",
                _ => "Connect browser helper"
            };
            helperAction.Top = open.Top = S(178);
            developerTip.Top = open.Bottom + S(7);
            siteRules.Top = developerTip.Bottom + S(6);
        }
        void Layout()
        {
            int right = page.ClientSize.Width - S(25);
            status.Width = developerTip.Width = Math.Max(S(180), right - S(25));
        }
        page.Resize += (_, _) => Layout();
        Layout();
        RefreshHelper();
    }

    private void BuildAbout()
    {
        var page = Page("About", "DarkOrbitWeaver · ko-fi.com/darkorbitweaver");
        string version = Vesper.Shared.BuildInfo.Version(typeof(SettingsForm).Assembly);
        var about = Label("VESPER " + version, 10, bold: true);
        about.Name = "aboutBuild";
        about.AccessibleName = "VESPER build version";
        about.SetBounds(25, 105, 485, 27);
        var github = Button("VESPER on GitHub", 156, symbol: VesperIcon.Link);
        github.SetBounds(25, 143, 156, 34);
        github.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/DarkOrbitWeaver/VESPER")
                { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { VesperMessageBox.Show(this, "Could not open GitHub. " + ex.Message, "About", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        var updates = Button("Check for updates", 182, symbol: VesperIcon.Refresh);
        updates.SetBounds(191, 143, 182, 34);
        updates.Click += async (_, _) =>
        {
            updates.Enabled = false;
            try
            {
                var release = await ReleaseCheck.LatestAsync(EngineHost.MediaNetwork());
                if (IsDisposed) return;
                string installed = Vesper.Shared.BuildInfo.Number(typeof(SettingsForm).Assembly);
                string message = release.Tag == "v" + installed || release.Tag == installed
                    ? "This version matches the latest published release. Open its release notes?"
                    : $"Installed: {installed}\nLatest published release: {release.Tag}\n\nOpen the release page? VESPER will not download or run an update automatically.";
                if (VesperMessageBox.Show(this, message, "VESPER updates", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.Url) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or OperationCanceledException
                or Vesper.MediaResolver.Models.MediaResolverException or System.Text.Json.JsonException
                or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                if (!IsDisposed) VesperMessageBox.Show(this, "The update check did not complete. No software was changed. " + ex.Message,
                    "VESPER updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally { if (!IsDisposed) updates.Enabled = true; }
        };
        var separator = Divider(195);
        var diagnosticsTitle = Label("Diagnostics", 9, bold: true);
        diagnosticsTitle.SetBounds(25, 208, 300, 22);
        var diagnosticsNote = Label(_engine is null
            ? "Open Settings from the running VESPER window to export diagnostics."
            : "Exports system details and the redacted event log. Event details can include filenames; review before sharing.", 8, muted: true);
        diagnosticsNote.SetBounds(25, 233, 485, 38);
        diagnosticsNote.AutoEllipsis = false;
        var exportDiagnostics = Button("Export diagnostics bundle", 188, symbol: VesperIcon.Folder);
        exportDiagnostics.Name = "exportDiagnostics";
        exportDiagnostics.AccessibleName = "Export diagnostics bundle";
        exportDiagnostics.SetBounds(25, 277, 200, 34);
        exportDiagnostics.Enabled = _engine is not null;
        exportDiagnostics.Click += (_, _) => ExportDiagnostics();

        page.Controls.AddRange([about, github, updates, separator, diagnosticsTitle, diagnosticsNote, exportDiagnostics]);
        page.Resize += (_, _) =>
        {
            about.Width = diagnosticsNote.Width = Math.Max(S(180), page.ClientSize.Width - S(50));
        };
    }

    internal static bool ShouldShowBrowserHelperAction(bool hostPresent, NativeHostRegistrationStatus registrationStatus, bool installed) =>
        hostPresent && registrationStatus != NativeHostRegistrationStatus.Unknown
            && (registrationStatus != NativeHostRegistrationStatus.Registered || !installed);

    private void ExportDiagnostics()
    {
        if (_engine is null) return;
        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "zip",
            FileName = $"VESPER-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip",
            Filter = "Diagnostics archive (*.zip)|*.zip",
            OverwritePrompt = true,
            RestoreDirectory = true,
            Title = "Export VESPER diagnostics"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Diagnostics.ExportBundle(_engine, dialog.FileName, Vesper.Shared.BuildInfo.Version(typeof(SettingsForm).Assembly));
            VesperMessageBox.Show(this, "The diagnostics archive was saved locally. It contains system details and a scrubbed event log; inspect it before sharing.",
                "Diagnostics exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or NotSupportedException or System.Security.SecurityException)
        {
            VesperMessageBox.Show(this, "The diagnostics archive could not be saved. " + ex.Message,
                "Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void BuildAppearance()
    {
        var page = Page("Appearance", "");
        var theme = Label("Color theme", 9, bold: true);
        theme.SetBounds(25, 107, 240, 23);
        var themeChoice = Choice(["System", "Light", "Dark"], (int)_draft.Theme, 184, "Color theme");
        themeChoice.SetBounds(346, 112, 184, 33);
        themeChoice.SelectedIndexChanged += (_, _) => _draft.Theme = (ThemeChoice)themeChoice.SelectedIndex;
        var separator1 = Divider(184);

        var density = Label("Download row height", 9, bold: true);
        density.SetBounds(25, 201, 250, 23);
        var densityNote = Label("Gallery shows media thumbnails.", 8, muted: true);
        densityNote.SetBounds(25, 226, 260, 35);
        var densityChoice = Choice(["Compact", "Extra compact", "Comfortable", "Gallery"],
            _draft.Density switch { Density.ExtraCompact => 1, Density.Comfortable => 2, Density.Gallery => 3, _ => 0 }, 204,
            "Download row height");
        densityChoice.SetBounds(326, 207, 204, 33);
        densityChoice.SelectedIndexChanged += (_, _) => _draft.Density = densityChoice.SelectedIndex switch
        { 1 => Density.ExtraCompact, 2 => Density.Comfortable, 3 => Density.Gallery, _ => Density.Compact };
        var separator2 = Divider(279);

        var botanical = Check("Botanical theme", 25, 303, _draft.Botanical,
            value => _draft.Botanical = value);
        var botanicalNote = Label("Illustrated headers, watercolor scenery, and a warm palette.", 8, muted: true);
        botanicalNote.SetBounds(45, 331, 470, 34);
        page.Controls.AddRange([theme, themeChoice, separator1, density, densityNote,
            densityChoice, separator2, botanical, botanicalNote]);
        void Layout()
        {
            int right = page.ClientSize.Width - S(25);
            themeChoice.Left = Math.Max(S(300), right - themeChoice.Width);
            densityChoice.Left = Math.Max(S(282), right - densityChoice.Width);
            int leftWidth = Math.Max(S(160), densityChoice.Left - S(40));
            densityNote.Width = leftWidth;
            botanicalNote.Width = Math.Max(S(225), page.ClientSize.Width - S(70));
        }
        page.Resize += (_, _) => Layout();
        Layout();
    }

    private void Save()
    {
        if (_siteRuleDirty && _saveSiteRuleEditor is not null && !_saveSiteRuleEditor()) return;
        if (!FileNames.TryValidateTemplate(_draft.FileNameTemplate, out string templateProblem))
        {
            ShowPage("Downloads");
            _fileNameTemplate.Focus();
            VesperMessageBox.Show(this, templateProblem, "Invalid filename template",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _draft.FileNameTemplate = _draft.FileNameTemplate.Trim();

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
        if (!PathSafety.TryGetFullPath(_draft.DefaultFolder, out string defaultFolder, out _))
        {
            ShowPage("Downloads");
            VesperMessageBox.Show(this, "Choose a valid absolute default folder path.", "Invalid folder",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _draft.DefaultFolder = defaultFolder;
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
