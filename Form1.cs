namespace VESPER;

public partial class Form1 : Form
{
    // The list is intentionally session-only. Nothing here downloads or manages files.
    private readonly List<DownloadItem> _records = DownloadItem.Examples();
    private readonly VesperPreferences _prefs = PreviewPreferences.Load();
    private readonly ContextMenuStrip _rowMenu = new() { ShowImageMargin = true };
    private readonly Dictionary<string, ToolStripMenuItem> _menuCommands = [];
    private readonly Dictionary<ToolStripMenuItem, VesperIcon> _menuSymbols = [];
    private readonly List<ToolStripMenuItem> _rateOptions = [];
    private ToolStripMenuItem _bandwidthMenu = null!;
    private readonly ToolTip _tooltips = new() { InitialDelay = 450, ReshowDelay = 100 };

    private DownloadItem? _menuTarget;
    private VesperPalette _colors = VesperPalette.Light;
    private Image? _moth;
    private Image? _branchLight;
    private Image? _branchDark;

    public Form1()
    {
        InitializeComponent();
        HandleCreated += (_, _) => VesperWindowChrome.Apply(this, UiKit.IsDark(_prefs));

        VesperWindowChrome.Brand(this);

        _moth = UiKit.LoadImage("moth-mark-transparent-96.png");
        _branchLight = UiKit.LoadImage("header-branch-light-720x288.png");
        _branchDark = UiKit.LoadImage("header-branch-dark-720x288.png");
        _brandIcon.Image = _moth;

        WireEvents();
        ApplyAppearance();
        RefreshList();
    }

    private void WireEvents()
    {
        _filter.SetItems("Everything", "Downloading", "Paused", "Queued", "Completed", "Needs attention");
        _sort.SetItems("Newest first", "Name", "Size", "Status");

        _grid.CheckStateChanged += UpdateSelectedTools;
        _grid.DetailsRequested += ShowDetails;
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
        // Initialize native menu handles after the first window paint, without a timer.
        Shown += (_, _) => BeginInvoke((Action)(() =>
        {
            if (!IsDisposed) { _ = _rowMenu.Handle; _ = _bandwidthMenu.DropDown.Handle; }
        }));
        _tooltips.SetToolTip(_settings, "Settings");
        _tooltips.SetToolTip(_add, "Add download (Ctrl+N)");
        _tooltips.SetToolTip(_searchFrame, "Search demonstration downloads");
        _add.Click += (_, _) => AddDownload();
        _settings.Click += (_, _) => OpenSettings();
        _search.TextChanged += (_, _) => RefreshList();
        _filter.SelectedIndexChanged += (_, _) => RefreshList();
        _sort.SelectedIndexChanged += (_, _) => RefreshList();
        _pause.Click += (_, _) => BatchChange(JobStatus.Downloading, JobStatus.Paused);
        _resume.Click += (_, _) => BatchChange(JobStatus.Paused, JobStatus.Queued);
        _remove.Click += (_, _) => RemoveDemoRecords(_grid.CheckedItems);
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
        int x = UiKit.Scale(this, 24);
        _selectedLabel.Location = new Point(x, top + (height - _selectedLabel.Height) / 2);
        x += UiKit.Scale(this, 108);

        foreach (var button in new[] { _pause, _resume, _remove })
        {
            button.SetBounds(x, top, UiKit.Scale(this, 85), height);
            x += UiKit.Scale(this, 93);
        }
        _clear.SetBounds(_selectedTools.Width - UiKit.Scale(this, 24) - UiKit.Scale(this, 137),
            top, UiKit.Scale(this, 137), height);
    }

    private void PaintMast(Graphics graphics)
    {
        graphics.Clear(_colors.Surface);
        if (!SystemInformation.HighContrast)
        {
            Image? branch = UiKit.IsDark(_prefs) ? _branchDark : _branchLight;
            if (branch is not null)
            {
                var destination = new Rectangle(Math.Max(245, _mast.Width - 405), -28, 265, 125);
                using var attributes = new System.Drawing.Imaging.ImageAttributes();
                var tint = new System.Drawing.Imaging.ColorMatrix
                {
                    Matrix33 = _prefs.Botanical ? 0.25f : 0.13f
                };
                attributes.SetColorMatrix(tint);
                graphics.DrawImage(branch, destination, 0, 0, branch.Width, branch.Height,
                    GraphicsUnit.Pixel, attributes);
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
        _selectedTools.BackColor = UiKit.IsDark(_prefs)
            ? Color.FromArgb(37, 63, 45) : Color.FromArgb(233, 241, 232);
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

    private void RefreshList()
    {
        IEnumerable<DownloadItem> records = _records;
        if (_prefs.HideMissingCompleted)
            records = records.Where(r => r.Status != JobStatus.Missing);

        string term = _search.Text.Trim();
        if (term.Length > 0)
            records = records.Where(r => r.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.Extension.Contains(term, StringComparison.OrdinalIgnoreCase));

        records = _filter.SelectedText switch
        {
            "Downloading" => records.Where(r => r.Status == JobStatus.Downloading),
            "Paused" => records.Where(r => r.Status == JobStatus.Paused),
            "Queued" => records.Where(r => r.Status == JobStatus.Queued),
            "Completed" => records.Where(r => r.Status is JobStatus.Completed or JobStatus.Missing),
            "Needs attention" => records.Where(r => r.Status is JobStatus.NeedsLink or JobStatus.Missing
                or JobStatus.Suspect or JobStatus.Waiting),
            _ => records
        };
        records = _sort.SelectedText switch
        {
            "Name" => records.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            "Size" => records.OrderByDescending(r => EstimateSize(r.Size)),
            "Status" => records.OrderBy(r => r.Status).ThenBy(r => r.Name),
            _ => records
        };

        _grid.SetItems(records.ToList());
        int active = _records.Count(r => r.Status == JobStatus.Downloading);
        int queued = _records.Count(r => r.Status == JobStatus.Queued);
        var parts = new List<string> { "DEMO · no files transferred" };
        if (active > 0) parts.Add($"{active} downloading");
        if (queued > 0) parts.Add($"{queued} queued");
        _footerLabel.Text = string.Join("    |    ", parts);
        UpdateSelectedTools();
    }

    private static double EstimateSize(string text)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !double.TryParse(parts[0],
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double amount))
            return 0;
        double multiplier = parts[1] switch
        {
            "GB" => 1024d * 1024 * 1024,
            "MB" => 1024d * 1024,
            "KB" => 1024,
            _ => 1
        };
        return amount * multiplier;
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
        _pause.Enabled = selected.Any(r => r.Status == JobStatus.Downloading);
        _resume.Enabled = selected.Any(r => r.Status == JobStatus.Paused);
        _remove.Enabled = count > 0;
    }

    private void BatchChange(JobStatus current, JobStatus next)
    {
        foreach (var record in _grid.CheckedItems.Where(r => r.Status == current))
        {
            record.Status = next;
            record.Transfer = "";
            record.Eta = "";
        }
        RefreshList();
    }

    private void RemoveDemoRecords(IEnumerable<DownloadItem> selected)
    {
        var items = selected.ToList();
        if (items.Count == 0) return;
        string message = $"Remove {items.Count} example {(items.Count == 1 ? "entry" : "entries")} from this session?\n\nNo files will be touched.";
        if (MessageBox.Show(this, message, "Remove entries", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) != DialogResult.Yes) return;

        var ids = items.Select(item => item.Id).ToHashSet();
        _records.RemoveAll(item => ids.Contains(item.Id));
        _grid.PruneChecks(_records.Select(item => item.Id));
        RefreshList();
    }

    private void ShowDetails(DownloadItem record)
    {
        using var dialog = new DownloadDetailsForm(record, _prefs, RefreshList);
        dialog.ShowDialog(this);
    }

    private void AddDownload()
    {
        using var dialog = new AddDownloadForm(_prefs);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // Demo data only: status previews change, but there are no transfers or filesystem writes.
        foreach (string url in dialog.DownloadUrls.Reverse())
        {
            string fileName = FileNameFromUrl(url);
            string extension = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
            string type = extension switch
            {
                "MP3" or "WAV" or "FLAC" or "M4A" => "Audio",
                "MP4" or "MKV" or "WEBM" => "Video",
                "ZIP" or "RAR" or "7Z" => "Archive",
                "ISO" => "Disc image",
                "EPUB" => "eBook",
                _ => "File"
            };
            _records.Insert(0, new DownloadItem
            {
                Name = fileName,
                FileType = type,
                Extension = extension,
                Size = "Unknown",
                SourceUrl = url,
                Destination = dialog.Destination!,
                Status = dialog.QueueOnly ? JobStatus.Queued : JobStatus.Downloading,
                Percent = dialog.QueueOnly ? null : 0,
                Detail = "DEMO: no network or file operation was performed."
            });
        }
        RefreshList();
    }

    private static string FileNameFromUrl(string url)
    {
        try
        {
            string segment = Uri.UnescapeDataString(new Uri(url).Segments.LastOrDefault() ?? "");
            foreach (char invalid in Path.GetInvalidFileNameChars()) segment = segment.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(segment) || segment == "/"
                ? "New download" : segment[..Math.Min(segment.Length, 120)];
        }
        catch { return "New download"; }
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(_prefs);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var updated = dialog.Result;
        _prefs.Theme = updated.Theme;
        _prefs.Density = updated.Density;
        _prefs.Motion = updated.Motion;
        _prefs.Botanical = updated.Botanical;
        _prefs.HideMissingCompleted = updated.HideMissingCompleted;
        _prefs.CategoryFolders = updated.CategoryFolders;
        _prefs.DefaultFolder = updated.DefaultFolder;
        _prefs.ConcurrentTransfers = updated.ConcurrentTransfers;
        _prefs.GlobalRateLimitKbs = updated.GlobalRateLimitKbs;
        _prefs.PerTransferRateLimitKbs = updated.PerTransferRateLimitKbs;
        _prefs.BandwidthPresetsKbs = [.. updated.BandwidthPresetsKbs];
        BuildRateOptions();
        PreviewPreferences.Save(_prefs);
        ApplyAppearance();
        RefreshList();
    }

    private void BuildMenuOnce()
    {
        // Prebuilt commands are reused for every record. This keeps first and
        // subsequent right-clicks free of image allocation and menu reconstruction.
        void Add(string text, VesperIcon icon, Action<DownloadItem> action)
        {
            var command = new ToolStripMenuItem(text);
            command.Click += (_, _) =>
            {
                if (_menuTarget is { } record) action(record);
            };
            _menuCommands.Add(text, command);
            _menuSymbols.Add(command, icon);
            _rowMenu.Items.Add(command);
        }

        Add("Details", VesperIcon.Info, ShowDetails);
        Add("Locate file…", VesperIcon.Folder, _ => NeedsEngine("Locating files"));
        Add("Open file", VesperIcon.File, _ => NeedsEngine("Opening a file"));
        Add("Open containing folder", VesperIcon.Folder, _ => NeedsEngine("Opening a folder"));
        Add("Pause", VesperIcon.Pause, record => ChangeState(record, JobStatus.Paused));
        Add("Resume", VesperIcon.Play, record => ChangeState(record, JobStatus.Queued));
        Add("Replace link…", VesperIcon.Link, ReplaceLink);
        Add("Start now", VesperIcon.Play, record =>
        {
            ChangeState(record, JobStatus.Waiting);
            NeedsEngine("Starting downloads");
        });

        _bandwidthMenu = new ToolStripMenuItem("Bandwidth · Unlimited");
        _menuSymbols.Add(_bandwidthMenu, VesperIcon.Gauge);
        _rowMenu.Items.Add(_bandwidthMenu);
        Add("Copy source URL", VesperIcon.Clipboard, CopySourceUrl);
        Add("Rename entry…", VesperIcon.Pencil, RenameRecord);
        _rowMenu.Items.Add(new ToolStripSeparator());
        Add("Remove from list…", VesperIcon.Trash, record => RemoveDemoRecords([record]));
        Add("Delete downloaded file…", VesperIcon.Trash, _ => NeedsEngine("Deleting downloaded files"));
        BuildRateOptions();
        VesperMenuTheme.Apply(_rowMenu, _colors, withIcons: true);
        RefreshMenuStyle();
        _menuTarget = _records.FirstOrDefault();
        ConfigureRowMenu(null, new System.ComponentModel.CancelEventArgs());
    }

    private void BuildRateOptions()
    {
        ToolStripItem[] previous = _bandwidthMenu.DropDownItems.Cast<ToolStripItem>().ToArray();
        foreach (ToolStripItem item in previous)
            if (item is ToolStripMenuItem command) _menuSymbols.Remove(command);
        _bandwidthMenu.DropDownItems.Clear();
        foreach (ToolStripItem item in previous) item.Dispose();
        _rateOptions.Clear();
        void AddRate(string label)
        {
            var option = new ToolStripMenuItem(label);
            option.Click += (_, _) =>
            {
                if (_menuTarget is not { } record) return;
                record.RateCap = label;
                RefreshList();
            };
            _rateOptions.Add(option);
            _bandwidthMenu.DropDownItems.Add(option);
        }
        AddRate("Unlimited");
        foreach (int kb in _prefs.BandwidthPresetsKbs.Distinct().Order())
            AddRate(kb % 1000 == 0 ? $"{kb / 1000} MB/s" : $"{kb} KB/s");
        _bandwidthMenu.DropDownItems.Add(new ToolStripSeparator());
        var custom = new ToolStripMenuItem("Custom limit…");
        custom.Click += (_, _) =>
        {
            if (_menuTarget is { } record) SetCustomLimit(record);
        };
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

    private void ConfigureRowMenu(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_menuTarget is not { } record) { e.Cancel = true; return; }
        // Only change items that actually differ. Turning every item off and
        // back on during Opening forced ToolStrip to lay out the menu repeatedly.
        foreach (var (name, command) in _menuCommands)
        {
            bool available = name switch
            {
                "Details" or "Copy source URL" or "Rename entry…" or "Remove from list…" => true,
                "Locate file…" => record.Status == JobStatus.Missing,
                "Open file" or "Open containing folder" or "Delete downloaded file…"
                    => record.Status == JobStatus.Completed,
                "Pause" => record.Status == JobStatus.Downloading,
                "Resume" => record.Status == JobStatus.Paused,
                "Replace link…" => record.Status is JobStatus.Paused or JobStatus.NeedsLink,
                "Start now" => record.Status == JobStatus.Queued,
                _ => false
            };
            if (command.Available != available) command.Available = available;
        }
        bool rateVisible = record.Status is JobStatus.Downloading or JobStatus.Paused
            or JobStatus.Queued or JobStatus.NeedsLink or JobStatus.Waiting;
        if (_bandwidthMenu.Available != rateVisible) _bandwidthMenu.Available = rateVisible;
        string rateLabel = $"Bandwidth · {record.RateCap}";
        if (_bandwidthMenu.Text != rateLabel) _bandwidthMenu.Text = rateLabel;
        foreach (var option in _rateOptions)
        {
            bool selected = option.Text == record.RateCap;
            if (option.Checked != selected) option.Checked = selected;
        }
    }

    private void NeedsEngine(string action) => MessageBox.Show(this,
        $"{action} requires the future download engine or verified files.\n\nNothing was changed.",
        "UI preview", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void CopySourceUrl(DownloadItem record)
    {
        if (string.IsNullOrWhiteSpace(record.SourceUrl))
        {
            NeedsEngine("Copying a missing source URL");
            return;
        }
        try { Clipboard.SetText(record.SourceUrl); }
        catch { MessageBox.Show(this, "The clipboard is unavailable.", "Clipboard", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }

    private void SetCustomLimit(DownloadItem record)
    {
        string? value = AskForText("Custom bandwidth · UI preview", "Maximum KB/s", "1024");
        if (value is null) return;
        if (!int.TryParse(value, out int kb) || kb is < 1 or > 10000000)
        {
            MessageBox.Show(this, "Enter a number between 1 and 10,000,000 KB/s.", "Invalid limit");
            return;
        }
        record.RateCap = $"{kb} KB/s";
        RefreshList();
    }

    private void ChangeState(DownloadItem record, JobStatus next)
    {
        record.Status = next;
        record.Transfer = "";
        record.Eta = "";
        RefreshList();
    }

    private void ReplaceLink(DownloadItem record)
    {
        string? result = AskForText("Replace link · UI preview", "New HTTP or HTTPS link", record.SourceUrl);
        if (result is null) return;
        if (!Uri.TryCreate(result, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(this, "Enter a valid HTTP or HTTPS URL.");
            return;
        }
        record.SourceUrl = uri.AbsoluteUri;
        record.Detail = "Preview link saved. No identity verification or partial-byte reuse occurred.";
        RefreshList();
    }

    private void RenameRecord(DownloadItem record)
    {
        string? name = AskForText("Rename entry", "Display name", record.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        record.Name = name.Trim();
        RefreshList();
    }

    private string? AskForText(string title, string caption, string initial)
    {
        using var dialog = new VesperPopupForm
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            AutoScaleDimensions = new SizeF(96, 96),
            AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(442, 193 + VesperDialogChrome.HeaderHeight),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Font = new Font("Segoe UI", 9F),
            BackColor = _colors.Surface,
            ForeColor = _colors.Text
        };
        VesperWindowChrome.Brand(dialog);
        var heading = new Label
        {
            Text = caption,
            Location = new Point(22, 21),
            Size = new Size(400, 26),
            ForeColor = _colors.Text,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold)
        };
        var hint = new Label
        {
            Text = "This changes the demonstration entry only.",
            Location = new Point(22, 48),
            Size = new Size(398, 20),
            ForeColor = _colors.Muted,
            Font = new Font("Segoe UI", 8F)
        };
        var frame = new VesperInputFrame
        {
            Location = new Point(22, 79),
            Size = new Size(398, 37),
            BorderColor = _colors.Border,
            FocusColor = _colors.MutedGreen,
            BackColor = _colors.Surface
        };
        var input = new TextBox
        {
            Text = initial,
            BorderStyle = BorderStyle.None,
            Location = new Point(10, 10),
            Width = 377,
            BackColor = _colors.Surface,
            ForeColor = _colors.Text
        };
        input.GotFocus += (_, _) => { frame.Active = true; frame.Invalidate(); };
        input.LostFocus += (_, _) => { frame.Active = false; frame.Invalidate(); };
        frame.Controls.Add(input);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = _colors.Surface };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(_colors.Border);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        var cancel = new VesperButton { Text = "Cancel" };
        var save = new VesperButton { Text = "Save", Primary = true };
        cancel.ApplyPalette(_colors);
        save.ApplyPalette(_colors);
        cancel.SetBounds(242, 13, 82, 34);
        save.SetBounds(333, 13, 86, 34);
        cancel.DialogResult = DialogResult.Cancel;
        save.DialogResult = DialogResult.OK;
        footer.Controls.AddRange([cancel, save]);
        var body = new Panel { Dock = DockStyle.Fill, BackColor = _colors.Surface };
        body.Controls.AddRange([heading, hint, frame]);
        dialog.Controls.Add(body);
        dialog.Controls.Add(footer);
        VesperDialogChrome.Attach(dialog, "VESPER  /  " + title, _colors, VesperIcon.Pencil);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => input.Focus();
        return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _moth?.Dispose();
        _branchLight?.Dispose();
        _branchDark?.Dispose();
        _rowMenu.Dispose();
        _tooltips.Dispose();
        base.OnFormClosed(e);
    }
}
