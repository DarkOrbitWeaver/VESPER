using Vesper.Engine;

namespace VESPER;

/// <summary>Native confirmation for browser-proposed links. Destinations remain desktop-owned.</summary>
internal sealed class BatchReviewForm : VesperPopupForm
{
    private readonly IReadOnlyList<BridgeItem> _items;
    private readonly VesperCheckedList _files;
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    private readonly VesperCheckBox _trustPageChecksums;
    private readonly TextBox _checksumDetails = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        WordWrap = true,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly VesperButton _download = new()
    {
        Name = "downloadReviewedFiles",
        Text = "Download",
        Width = 122,
        Height = 34,
        TextAlign = ContentAlignment.MiddleCenter,
        Primary = true
    };
    private readonly VesperButton _queue = new()
    {
        Name = "queueReviewedFiles",
        Text = "Queue",
        Width = 91,
        Height = 34,
        TextAlign = ContentAlignment.MiddleCenter
    };
    private readonly Label _status = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        UseMnemonic = false
    };
    private readonly VesperPanel _footer;

    public QuickDownloadForm.Outcome Result { get; private set; }
    public string Destination => PathSafety.TryGetFullPath(_folder.Text, out string full, out _) ? full : _folder.Text.Trim();
    public IReadOnlySet<int> SelectedIndices => _files.CheckedIndices.Cast<int>().ToHashSet();
    public IReadOnlySet<int> TrustedChecksumIndices => _trustPageChecksums.Checked
        ? _files.CheckedIndices.Cast<int>().Where(index => _items[index].Sha256 is not null).ToHashSet()
        : new HashSet<int>();

    public BatchReviewForm(VesperPreferences preferences, IReadOnlyList<BridgeItem> items)
    {
        var colors = UiKit.Palette(preferences);
        VesperWindowChrome.Brand(this);
        Text = "Review files - VESPER";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(680, 530);
        MinimumSize = new Size(520, 360);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = colors.Surface;
        ForeColor = colors.Text;
        _items = items;

        _files = new VesperCheckedList(colors)
        {
            Name = "filesToReview",
            AccessibleName = "Files to download",
            AccessibleDescription = "Clear a check to leave a proposed link out of this batch.",
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            BackColor = colors.Surface,
            ForeColor = colors.Text,
            Font = Font
        };
        foreach (var item in items)
        {
            var uri = new Uri(item.Url);
            string title = item.ReviewLabel ?? item.FileName ?? FileNames.FromUrl(uri);
            string type = item.ReviewType ?? "File";
            string duration = "";
            if (item.ReviewDurationSeconds is double seconds)
                duration = " · " + TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
            else if (type.EndsWith("video", StringComparison.OrdinalIgnoreCase) || type.EndsWith("audio", StringComparison.OrdinalIgnoreCase))
                duration = " · Duration unknown";
            string checksum = item.Sha256 is null ? "" : "  |  page SHA-256 available";
            _files.Items.Add($"{type}{duration}  |  {title}  |  {uri.Host}{checksum}", true);
        }

        _trustPageChecksums = new VesperCheckBox(colors)
        {
            Name = "trustPageChecksums",
            Text = "Use page supplied SHA-256 checksums for selected files",
            AccessibleName = "Use page supplied SHA-256 checksums for selected files",
            AccessibleDescription = "VESPER verifies each selected file before publishing it. A mismatch is kept and marked for review.",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = UiKit.Scale(this, 26),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = colors.Surface,
            ForeColor = colors.Text,
            Font = Font,
            Enabled = false
        };

        bool hasChecksums = items.Any(item => item.Sha256 is not null);
        _checksumDetails.Name = "pageChecksumDetails";
        _checksumDetails.AccessibleName = "Page supplied checksum details";
        _checksumDetails.BackColor = colors.Secondary;
        _checksumDetails.ForeColor = colors.Text;
        _checksumDetails.Font = Font;
        _checksumDetails.Text = "Select a file with a page checksum to view it here.";

        var checksumPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 8, 0, 4),
            Visible = hasChecksums
        };
        checksumPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        checksumPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        checksumPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiKit.Scale(this, 58)));
        checksumPanel.Controls.Add(_trustPageChecksums, 0, 0);
        checksumPanel.Controls.Add(_checksumDetails, 0, 1);

        var layout = new TableLayoutPanel
        {
            Name = "reviewContent",
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = "Choose files to download. Unchecked links will be skipped.",
            AutoSize = true,
            MaximumSize = new Size(610, 0),
            Margin = new Padding(0, 0, 0, 10),
            ForeColor = colors.Text
        }, 0, 0);
        layout.Controls.Add(_files, 0, 1);

        var destination = new TableLayoutPanel
        {
            Name = "reviewDestination",
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            Margin = new Padding(0, 12, 0, 8)
        };
        destination.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        destination.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destination.Controls.Add(new Label
        {
            Text = "Save to",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 12, 0),
            ForeColor = colors.Text
        }, 0, 0);

        var folderFrame = new VesperInputFrame
        {
            Name = "destinationFolderFrame",
            Dock = DockStyle.Fill,
            Height = 36,
            Padding = new Padding(10, 6, 10, 6),
            BackColor = colors.Surface,
            BorderColor = colors.Border,
            FocusColor = colors.MutedGreen
        };
        _folder.Name = "destinationFolder";
        _folder.AccessibleName = "Destination folder";
        _folder.AccessibleDescription = "Folder where the selected files will be saved.";
        _folder.Text = preferences.DefaultFolder;
        _folder.BackColor = colors.Surface;
        _folder.ForeColor = colors.Text;
        _folder.Font = Font;
        _folder.GotFocus += (_, _) => { folderFrame.Active = true; folderFrame.Invalidate(); };
        _folder.LostFocus += (_, _) => { folderFrame.Active = false; folderFrame.Invalidate(); };
        folderFrame.Controls.Add(_folder);
        destination.Controls.Add(folderFrame, 1, 0);

        var browse = new VesperButton
        {
            Name = "browseDestinationFolder",
            Text = "Browse…",
            Width = 102,
            Height = 36,
            TextAlign = ContentAlignment.MiddleCenter,
            Symbol = VesperIcon.Folder,
            Margin = new Padding(8, 0, 0, 0)
        };
        browse.ApplyPalette(colors);
        browse.Font = Font;
        browse.AccessibleName = "Choose destination folder";
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "Choose a folder for the selected files", UseDescriptionForTitle = true };
            if (PathSafety.TryGetFullPath(Destination, out string selected, out _) && Directory.Exists(selected))
                picker.SelectedPath = selected;
            if (picker.ShowDialog(this) == DialogResult.OK) _folder.Text = picker.SelectedPath;
        };
        destination.Controls.Add(browse, 2, 0);
        layout.Controls.Add(destination, 0, 2);
        layout.Controls.Add(checksumPanel, 0, 3);

        _status.Name = "reviewSelectionStatus";
        _status.AccessibleName = "File selection status";
        _status.ForeColor = colors.Muted;
        _status.Font = Font;
        _status.Margin = new Padding(0, 0, 0, 2);
        layout.Controls.Add(_status, 0, 4);

        _footer = new VesperPanel { Name = "reviewFooter", Dock = DockStyle.Bottom, Height = 62, BackColor = colors.Surface };
        _footer.Paint += (_, e) =>
        {
            using var separator = new Pen(colors.Divider);
            e.Graphics.DrawLine(separator, 0, 0, _footer.Width, 0);
        };

        var cancel = new VesperButton
        {
            Name = "cancelReview",
            Text = "Cancel",
            Width = 85,
            Height = 34,
            TextAlign = ContentAlignment.MiddleCenter,
            DialogResult = DialogResult.Cancel
        };
        foreach (var button in new[] { cancel, _queue, _download })
        {
            button.ApplyPalette(colors);
            button.Font = Font;
            button.Margin = Padding.Empty;
            _footer.Controls.Add(button);
        }

        void LayoutFooter()
        {
            int scale(int value) => UiKit.Scale(this, value);
            int buttonHeight = scale(34);
            int gap = scale(8);
            int right = scale(23);
            int cancelWidth = scale(85);
            int queueWidth = scale(91);
            int downloadWidth = scale(122);
            int left = Math.Max(scale(12), _footer.ClientSize.Width - right - cancelWidth - queueWidth - downloadWidth - (gap * 2));
            int top = (_footer.ClientSize.Height - buttonHeight) / 2;
            cancel.SetBounds(left, top, cancelWidth, buttonHeight);
            _queue.SetBounds(cancel.Right + gap, top, queueWidth, buttonHeight);
            _download.SetBounds(_queue.Right + gap, top, downloadWidth, buttonHeight);
        }

        _footer.Resize += (_, _) => LayoutFooter();
        Controls.Add(layout);
        Controls.Add(_footer);
        _download.Click += (_, _) => Accept(QuickDownloadForm.Outcome.Download);
        _queue.Click += (_, _) => Accept(QuickDownloadForm.Outcome.Queue);
        _files.ItemCheck += (_, _) => BeginInvoke(UpdateSelection);
        _files.SelectedIndexChanged += (_, _) => UpdateChecksumDetails();
        _trustPageChecksums.CheckedChanged += (_, _) => UpdateChecksumDetails();
        Shown += (_, _) =>
        {
            LayoutFooter();
            UpdateSelection();
            UpdateChecksumDetails();
        };
        DpiChanged += (_, _) => BeginInvoke(() => { if (!IsDisposed) LayoutFooter(); });
        AcceptButton = _download;
        CancelButton = cancel;
        VesperDialogChrome.Attach(this, "VESPER  /  Review files", colors, VesperIcon.Download);
    }

    private void UpdateSelection()
    {
        if (IsDisposed) return;
        int count = _files.CheckedItems.Count;
        _status.Text = $"{count} of {_files.Items.Count} files selected";
        _download.Enabled = _queue.Enabled = count > 0;
        UpdateChecksumDetails();
    }

    private void UpdateChecksumDetails()
    {
        int index = _files.SelectedIndex;
        BridgeItem? item = index >= 0 && index < _items.Count ? _items[index] : null;
        _checksumDetails.Text = item?.Sha256 is { } hash
            ? $"SHA-256 from {PageHost(item.Referer)}:\r\n{hash}\r\nCheck this value before trusting the page."
            : "Select a file with a page checksum to view it here.";

        bool hasSelectedChecksum = _files.CheckedIndices.Cast<int>().Any(i => _items[i].Sha256 is not null);
        _trustPageChecksums.Enabled = hasSelectedChecksum;
        if (!hasSelectedChecksum) _trustPageChecksums.Checked = false;
    }

    private static string PageHost(string? referer) => UrlPolicy.TryParse(referer, out Uri uri, out _)
        ? $"{uri.Scheme}://{uri.Authority}" : "The page";

    private void Accept(QuickDownloadForm.Outcome outcome)
    {
        if (_files.CheckedItems.Count == 0) return;
        if (!PathSafety.TryResolveFolder(Destination, create: true, out string resolved, out string error))
        { _status.Text = error; return; }
        _folder.Text = resolved;
        Result = outcome;
        DialogResult = DialogResult.OK;
    }
}
