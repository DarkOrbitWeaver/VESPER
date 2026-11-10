namespace VESPER;

// UI-only Add Download dialog. No request is issued and no output file is created.
[System.ComponentModel.DesignerCategory("Form")]
internal sealed class AddDownloadForm : VesperPopupForm
{
    private readonly VesperPreferences _prefs;
    private readonly VesperPalette _colors;
    private readonly VesperInputFrame _urlFrame;
    private readonly TextBox _url;
    private readonly Label _hint;
    private readonly Panel _detected;
    private readonly Label _detectedTitle;
    private readonly Label _detectedNote;
    private readonly VesperButton _paste;
    private readonly VesperButton _destinationButton;
    private readonly Panel _destinationPanel;
    private readonly TextBox _destinationInput;
    private readonly Panel _footer;
    private readonly VesperButton _download;
    private readonly VesperButton _queue;
    private bool _folderExpanded;
    private bool _layingOut;

    public IReadOnlyList<string> DownloadUrls { get; private set; } = [];
    public string? Destination { get; private set; }
    public bool QueueOnly { get; private set; }

    public AddDownloadForm() : this(new VesperPreferences()) { }

    public AddDownloadForm(VesperPreferences preferences)
    {
        _prefs = preferences.Clone();
        _colors = UiKit.Palette(_prefs);
        VesperWindowChrome.Brand(this);
        Text = "Add download — VESPER";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(508, 307);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        BackColor = _colors.Surface;
        ForeColor = _colors.Text;

        var title = TextLabel("Add download", 16, true);
        title.SetBounds(23, 18, 440, 34);
        var description = TextLabel("Paste an HTTP or HTTPS link. Files are not transferred in this demo.", 8.5F, muted: true);
        description.SetBounds(23, 52, 455, 23);
        var fieldLabel = TextLabel("Download link", 9, true);
        fieldLabel.SetBounds(23, 84, 400, 22);

        _urlFrame = new VesperInputFrame
        {
            Location = new Point(23, 107), Size = new Size(462, 43),
            BackColor = _colors.Surface, BorderColor = _colors.Border, FocusColor = _colors.MutedGreen
        };
        _url = new TextBox
        {
            Name = "downloadUrl", BorderStyle = BorderStyle.None,
            PlaceholderText = "Paste a download link…", Multiline = true,
            ScrollBars = ScrollBars.None,
            Location = new Point(12, 12), Size = new Size(400, 22),
            BackColor = _colors.Surface, ForeColor = _colors.Text
        };
        _url.GotFocus += (_, _) => { _urlFrame.Active = true; _urlFrame.Invalidate(); };
        _url.LostFocus += (_, _) => { _urlFrame.Active = false; _urlFrame.Invalidate(); };
        _url.TextChanged += (_, _) => InspectUrl();
        _paste = MakeButton("", 31, symbol: VesperIcon.Clipboard);
        _paste.Quiet = true;
        _paste.AccessibleName = "Paste URL from clipboard";
        _paste.SetBounds(423, 5, 31, 32);
        _paste.Click += (_, _) =>
        {
            if (_url.TextLength > 0)
            {
                _url.Clear();
            }
            else
            {
                try { if (Clipboard.ContainsText()) _url.Text = Clipboard.GetText(); }
                catch { MessageBox.Show(this, "Clipboard is unavailable.", "Clipboard", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            }
            _url.Focus();
        };
        _urlFrame.Controls.AddRange([_url, _paste]);

        _hint = TextLabel("Enter a link to continue", 8, muted: true);
        _hint.SetBounds(23, 157, 462, 22);
        _detected = new Panel { BackColor = _colors.Secondary, Size = new Size(462, 55), Visible = false };
        _detectedTitle = TextLabel("", 9, true);
        _detectedNote = TextLabel("", 8, muted: true);
        _detectedTitle.SetBounds(12, 7, 437, 20);
        _detectedNote.SetBounds(12, 29, 437, 19);
        _detected.Controls.AddRange([_detectedTitle, _detectedNote]);

        _destinationButton = MakeButton("", 462, symbol: VesperIcon.Folder);
        _destinationButton.Name = "saveToToggle";
        _destinationButton.TrailingChevron = true;
        _destinationButton.TextAlign = ContentAlignment.MiddleLeft;
        _destinationButton.AccessibleName = "Choose download destination";
        _destinationButton.Click += (_, _) => { _folderExpanded = !_folderExpanded; LayoutContent(); };

        _destinationPanel = new Panel { Size = new Size(462, 82), BackColor = _colors.Surface, Visible = false };
        var folderLabel = TextLabel("Folder for this download", 8, muted: true);
        folderLabel.SetBounds(0, 0, 340, 19);
        var destinationFrame = new VesperInputFrame
        {
            Location = new Point(0, 22), Size = new Size(351, 34),
            BackColor = _colors.Surface, BorderColor = _colors.Border, FocusColor = _colors.MutedGreen
        };
        _destinationInput = new TextBox
        {
            Name = "destinationFolder", Text = _prefs.DefaultFolder, BorderStyle = BorderStyle.None,
            Location = new Point(9, 8), Width = 332, BackColor = _colors.Surface, ForeColor = _colors.Text
        };
        _destinationInput.TextChanged += (_, _) => UpdateDestinationText();
        _destinationInput.GotFocus += (_, _) => { destinationFrame.Active = true; destinationFrame.Invalidate(); };
        _destinationInput.LostFocus += (_, _) => { destinationFrame.Active = false; destinationFrame.Invalidate(); };
        destinationFrame.Controls.Add(_destinationInput);
        var browse = MakeButton("Browse…", 102);
        browse.SetBounds(360, 22, 102, 34);
        browse.Click += (_, _) =>
        {
            using var chooser = new FolderBrowserDialog { Description = "Choose a destination for this demonstration entry", UseDescriptionForTitle = true };
            if (Directory.Exists(_destinationInput.Text)) chooser.SelectedPath = _destinationInput.Text;
            if (chooser.ShowDialog(this) == DialogResult.OK) _destinationInput.Text = chooser.SelectedPath;
        };
        var reset = new LinkLabel
        {
            Text = "Use default folder", AutoSize = true, Location = new Point(0, 60),
            BackColor = _colors.Surface, LinkColor = _colors.Green, ActiveLinkColor = _colors.Green,
            Font = new Font("Segoe UI", 8F)
        };
        reset.Click += (_, _) => _destinationInput.Text = _prefs.DefaultFolder;
        _destinationPanel.Controls.AddRange([folderLabel, destinationFrame, browse, reset]);

        _footer = new VesperPanel { Dock = DockStyle.Bottom, Height = 62, BackColor = _colors.Surface };
        _footer.Paint += (_, e) =>
        {
            using var separator = new Pen(_colors.Border);
            e.Graphics.DrawLine(separator, 0, 0, _footer.Width, 0);
        };
        var cancel = MakeButton("Cancel", 85);
        cancel.DialogResult = DialogResult.Cancel;
        _queue = MakeButton("Queue", 91, symbol: VesperIcon.Queue);
        _queue.AccessibleName = "Add to queue (demonstration)";
        _queue.Click += (_, _) => Accept(queueOnly: true);
        _download = MakeButton("Download", 122, primary: true, symbol: VesperIcon.Download);
        _download.Click += (_, _) => Accept(queueOnly: false);
        _footer.Controls.AddRange([cancel, _queue, _download]);
        _footer.Resize += (_, _) =>
        {
            cancel.SetBounds(23, 14, 85, 34);
            _download.SetBounds(_footer.Width - 23 - 122, 14, 122, 34);
            _queue.SetBounds(_download.Left - 8 - 91, 14, 91, 34);
        };

        var body = new Panel { Dock = DockStyle.Fill, BackColor = _colors.Surface };
        body.Controls.AddRange([title, description, fieldLabel, _urlFrame, _hint,
            _detected, _destinationButton, _destinationPanel]);
        Controls.Add(body);
        Controls.Add(_footer);
        VesperDialogChrome.Attach(this, "VESPER  /  Add download", _colors, VesperIcon.Plus);
        CancelButton = cancel;
        AcceptButton = _download;
        Shown += (_, _) => _url.Focus();
        UpdateDestinationText();
        InspectUrl();
    }

    private Label TextLabel(string text, float size, bool bold = false, bool muted = false) => new()
    {
        Text = text, AutoSize = false, BackColor = Color.Transparent,
        ForeColor = muted ? _colors.Muted : _colors.Text,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private VesperButton MakeButton(string text, int width, bool primary = false, VesperIcon symbol = VesperIcon.None)
    {
        var button = new VesperButton { Text = text, Primary = primary, Symbol = symbol, Width = width, Height = 34 };
        button.ApplyPalette(_colors);
        return button;
    }

    private void UpdateDestinationText()
    {
        string path = _destinationInput.Text;
        _destinationButton.Text = "Save to   " + (path.Length > 48 ? "…" + path[^47..] : path);
    }

    private void InspectUrl()
    {
        string[] links = SplitLinks();
        _paste.Symbol = _url.TextLength > 0 ? VesperIcon.Close : VesperIcon.Clipboard;
        _paste.AccessibleName = _url.TextLength > 0 ? "Clear URL" : "Paste URL from clipboard";
        _paste.Invalidate();
        bool valid = links.Length > 0 && links.All(IsSupportedUrl);
        bool invalid = links.Length > 0 && !valid;
        _urlFrame.BorderColor = invalid ? _colors.Red : _colors.Border;
        _urlFrame.FocusColor = invalid ? _colors.Red : _colors.MutedGreen;
        _urlFrame.Invalidate();
        _queue.Enabled = _download.Enabled = valid;
        _detected.Visible = links.Length > 0;
        _detected.BackColor = invalid
            ? (_colors.Surface.R == 255 ? Color.FromArgb(253, 246, 243) : Color.FromArgb(54, 37, 35))
            : _colors.Secondary;
        if (links.Length == 0)
        {
            _hint.Text = "Enter a link to continue";
        }
        else if (!valid)
        {
            _hint.Text = "Check the link above";
            _detectedTitle.Text = "Unsupported or invalid address";
            _detectedTitle.ForeColor = _colors.Red;
            _detectedNote.Text = "Only direct HTTP/HTTPS links are supported in this preview.";
        }
        else
        {
            _hint.Text = "Ready · demonstration only";
            _detectedTitle.ForeColor = _colors.Green;
            if (links.Length > 1)
            {
                _detectedTitle.Text = $"{links.Length} links detected";
                _detectedNote.Text = "Each becomes a sample entry. No files are downloaded.";
            }
            else
            {
                _detectedTitle.Text = "Link recognized";
                _detectedNote.Text = "Choose Queue or Download to add a sample entry.";
            }
        }
        LayoutContent();
    }

    private string[] SplitLinks() => _url.Text.Split(['\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsSupportedUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var url)
        && (url.Scheme is "http" or "https")
        && url.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) == false
        && url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) == false
        && url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) == false;

    private void LayoutContent()
    {
        if (_layingOut) return;
        _layingOut = true;
        try
        {
            int destinationY = _detected.Visible ? 248 : 192;
            _detected.SetBounds(23, 181, 462, 55);
            _destinationButton.SetBounds(23, destinationY, 462, 34);
            _destinationPanel.Visible = _folderExpanded;
            _destinationPanel.Location = new Point(23, destinationY + 43);
            int contentBottom = destinationY + 34 + (_folderExpanded ? 92 : 0);
            ClientSize = new Size(508, contentBottom + 18 + _footer.Height + VesperDialogChrome.HeaderHeight);
        }
        finally { _layingOut = false; }
    }

    private void Accept(bool queueOnly)
    {
        string[] links = SplitLinks();
        if (links.Length == 0 || !links.All(IsSupportedUrl)) return;
        string folder = _destinationInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            MessageBox.Show(this, "Choose an absolute destination folder path.", "Invalid destination",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        DownloadUrls = links.Select(link => new Uri(link).AbsoluteUri).ToArray();
        Destination = folder;
        QueueOnly = queueOnly;
        DialogResult = DialogResult.OK;
        Close();
    }
}
