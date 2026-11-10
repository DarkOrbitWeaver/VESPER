namespace VESPER;

// Editing happens on a copy. Only Save applies the preview preferences to the main window.
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
        ClientSize = new Size(746, 537);
        MinimumSize = new Size(700, 510);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9F);
        BackColor = _colors.Surface;
        ForeColor = _colors.Text;

        _sidebar = new Panel { Dock = DockStyle.Left, Width = 166, BackColor = _colors.Secondary };
        _sidebar.Paint += (_, e) =>
        {
            using var pen = new Pen(_colors.Border);
            e.Graphics.DrawLine(pen, _sidebar.Width - 1, 0, _sidebar.Width - 1, _sidebar.Height);
        };
        var title = Label("Settings", 17, bold: true);
        title.SetBounds(20, 25, 138, 38);
        _sidebar.Controls.Add(title);
        string[] tabs = ["Downloads", "Transfers", "Appearance"];
        for (int i = 0; i < tabs.Length; i++)
        {
            string tab = tabs[i];
            var nav = Button(tab, 146);
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
            using var pen = new Pen(_colors.Border);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        var cancel = Button("Cancel", 87);
        cancel.DialogResult = DialogResult.Cancel;
        var save = Button("Save settings", 125, primary: true);
        save.Click += (_, _) => Save();
        footer.Controls.AddRange([cancel, save]);
        void LayoutFooter()
        {
            save.SetBounds(footer.Width - 21 - 125, 13, 125, 34);
            cancel.SetBounds(save.Left - 8 - 87, 13, 87, 34);
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
        BuildAppearance();
        ShowPage("Downloads");
        ClientSize = new Size(ClientSize.Width, ClientSize.Height + VesperDialogChrome.HeaderHeight);
        VesperDialogChrome.Attach(this, "VESPER  /  Settings", _colors, VesperIcon.Gear);
    }

    private Label Label(string text, float size = 9, bool bold = false, bool muted = false)
    {
        return new Label
        {
            Text = text, AutoSize = false,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            BackColor = Color.Transparent,
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
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false, BackColor = _colors.Surface };
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
        BackColor = _colors.Border, Height = 1, Location = new Point(25, y), Width = 505,
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
        var check = new CheckBox
        {
            Text = text, Checked = value, Location = new Point(x, y), Size = new Size(480, 25),
            AutoSize = false, FlatStyle = FlatStyle.Flat,
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
            using var picker = new FolderBrowserDialog { Description = "Default folder for demonstration entries", UseDescriptionForTitle = true };
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
        page.Controls.AddRange([folderLabel, folderNote, frame, browse, separator1, categories, categoryNote,
            separator2, missing, missingNote]);
        void Layout()
        {
            int right = page.ClientSize.Width - 25;
            browse.Left = Math.Max(308, right - browse.Width);
            frame.Width = Math.Max(180, browse.Left - 34);
            int noteWidth = Math.Max(210, right - 45);
            categoryNote.Width = missingNote.Width = noteWidth;
        }
        page.Resize += (_, _) => Layout();
        Layout();
    }

    private void BuildTransfers()
    {
        var page = Page("Transfers", "Concurrency and speed limits · demonstration settings.");
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

    private void BuildAppearance()
    {
        var page = Page("Appearance", "A quieter interface by default.");
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
        var densityNote = Label("Compact is the default. Extra compact shows one line.", 8, muted: true);
        densityNote.SetBounds(25, 226, 260, 35);
        var densityChoice = Choice(["Compact · 46 px (default)", "Extra compact · 34 px", "Comfortable · 58 px"],
            _draft.Density switch { Density.ExtraCompact => 1, Density.Comfortable => 2, _ => 0 }, 204);
        densityChoice.SetBounds(326, 207, 204, 33);
        densityChoice.SelectedIndexChanged += (_, _) => _draft.Density = densityChoice.SelectedIndex switch
        { 1 => Density.ExtraCompact, 2 => Density.Comfortable, _ => Density.Compact };
        var separator2 = Divider(279);

        var motion = Label("Motion preference", 9, bold: true);
        motion.SetBounds(25, 298, 245, 23);
        var motionNote = Label("No idle animation loops. Effects are optional.", 8, muted: true);
        motionNote.SetBounds(25, 325, 255, 31);
        var motionChoice = Choice(["Off", "Subtle", "Botanical flourish"], (int)_draft.Motion, 184);
        motionChoice.SetBounds(346, 303, 184, 33);
        motionChoice.SelectedIndexChanged += (_, _) => _draft.Motion = (MotionChoice)motionChoice.SelectedIndex;
        var separator3 = Divider(378);

        var botanical = Check("Enable optional v9 botanical decoration", 25, 395, _draft.Botanical,
            value => _draft.Botanical = value);
        var botanicalNote = Label("Only the masthead illustration changes. Download rows stay clean.", 8, muted: true);
        botanicalNote.SetBounds(45, 423, 455, 34);
        page.Controls.AddRange([theme, themeNote, themeChoice, separator1, density, densityNote,
            densityChoice, separator2, motion, motionNote, motionChoice, separator3,
            botanical, botanicalNote]);
        void Layout()
        {
            int right = page.ClientSize.Width - 25;
            themeChoice.Left = Math.Max(300, right - themeChoice.Width);
            densityChoice.Left = Math.Max(282, right - densityChoice.Width);
            motionChoice.Left = Math.Max(300, right - motionChoice.Width);
            int leftWidth = Math.Max(160, densityChoice.Left - 40);
            themeNote.Width = densityNote.Width = motionNote.Width = leftWidth;
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
                MessageBox.Show(this, "Enter a global speed limit between 1 and 10,000,000 KB/s.", "Invalid limit",
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
            MessageBox.Show(this, "Choose a valid absolute default folder path.", "Invalid folder",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _draft.BandwidthPresetsKbs = values;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void InvalidPreset(PresetRow row)
    {
        ShowPage("Transfers");
        row.Value.Focus();
        MessageBox.Show(this, "Enter positive, unique bandwidth presets (up to 10,000,000 KB/s each).",
            "Invalid preset", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _navFont.Dispose(); _navActiveFont.Dispose(); }
        base.Dispose(disposing);
    }
}
