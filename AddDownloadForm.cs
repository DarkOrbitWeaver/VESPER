namespace VESPER;

// Add Download. Validates links and the destination; the engine re-validates and creates the jobs.
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
    private readonly LinkLabel _discoverLink;
    private readonly VesperButton _paste;
    private readonly LinkLabel _multipleLinks;
    private readonly VesperButton _openPage;
    private readonly VesperButton _destinationButton;
    private readonly Panel _destinationPanel;
    private readonly TextBox _destinationInput;
    private readonly Panel _footer;
    private readonly VesperButton _download;
    private readonly VesperButton _queue;
    private bool _folderExpanded;
    private bool _multiLine;
    private bool _layingOut;

    public IReadOnlyList<string> DownloadUrls { get; private set; } = [];
    public IReadOnlyList<MediaDownloadChoice> MediaJobs { get; private set; } = [];
    public string? Destination { get; private set; }
    public bool QueueOnly { get; private set; }

    public AddDownloadForm() : this(new VesperPreferences()) { }

    public AddDownloadForm(VesperPreferences preferences, string? initialUrl = null)
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

        var title = TextLabel("Add download", 17, true);
        title.SetBounds(23, 18, 440, 34);
        var description = TextLabel("Paste one or more HTTP or HTTPS links, one per line.", 8.5F, muted: true);
        description.SetBounds(23, 52, 455, 23);
        var fieldLabel = TextLabel("Download link", 9, true);
        fieldLabel.SetBounds(23, 84, 310, 22);
        _multipleLinks = new LinkLabel
        {
            Text = "Multiple links", AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(352, 84), Size = new Size(133, 22),
            LinkColor = _colors.Link, ActiveLinkColor = _colors.Text,
            Font = new Font("Segoe UI", 8F)
        };
        _multipleLinks.Click += (_, _) => SetMultipleLinks(!_multiLine);

        _urlFrame = new VesperInputFrame
        {
            Location = new Point(23, 107), Size = new Size(462, 43),
            BackColor = _colors.Surface, BorderColor = _colors.Border, FocusColor = _colors.MutedGreen
        };
        _url = new TextBox
        {
            Name = "downloadUrl", BorderStyle = BorderStyle.None,
            PlaceholderText = "Paste a download link…", Multiline = false,
            WordWrap = false, ScrollBars = ScrollBars.None,
            Location = new Point(12, 12), Size = new Size(400, 22),
            BackColor = _colors.Surface, ForeColor = _colors.Text
        };
        _url.GotFocus += (_, _) => { _urlFrame.Active = true; _urlFrame.Invalidate(); };
        _url.LostFocus += (_, _) => { _urlFrame.Active = false; _urlFrame.Invalidate(); };
        _url.TextChanged += (_, _) => InspectUrl();
        _url.KeyDown += (_, e) =>
        {
            if (!e.Control || e.KeyCode != Keys.V) return;
            if (!TryReadClipboard(out string clipboard) || !clipboard.Contains('\n')) return;
            SetMultipleLinks(true);
            _url.SelectedText = clipboard;
            e.SuppressKeyPress = true;
        };
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
                if (TryReadClipboard(out string clipboard)) SetLinkText(clipboard);
                else MessageBox.Show(this, "There isn't a usable link on the clipboard.", "Paste link", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            _url.Focus();
        };
        _urlFrame.Controls.AddRange([_url, _paste]);

        _hint = TextLabel("Enter a link to continue", 8, muted: true);
        _hint.SetBounds(23, 157, 462, 22);
        _detected = new VesperPanel { BackColor = _colors.Surface, Size = new Size(462, 47), Visible = false };
        _detectedTitle = TextLabel("", 9, true);
        _detectedNote = TextLabel("", 8, muted: true);
        _detectedTitle.SetBounds(23, 1, 308, 20);
        _detectedNote.SetBounds(23, 22, 437, 19);
        _discoverLink = new LinkLabel
        {
            Text = "Find media", TextAlign = ContentAlignment.MiddleRight,
            LinkColor = _colors.Link, ActiveLinkColor = _colors.Text,
            Location = new Point(340, 0), Size = new Size(121, 21), Visible = false,
            Font = new Font("Segoe UI", 8.5F)
        };
        _discoverLink.Click += (_, _) => DiscoverVideo(allowGeneric: true);
        _detected.Controls.AddRange([_detectedTitle, _detectedNote, _discoverLink]);

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
            using var chooser = new FolderBrowserDialog { Description = "Choose a folder for this download", UseDescriptionForTitle = true };
            if (Directory.Exists(_destinationInput.Text)) chooser.SelectedPath = _destinationInput.Text;
            if (chooser.ShowDialog(this) == DialogResult.OK) _destinationInput.Text = chooser.SelectedPath;
        };
        var reset = new LinkLabel
        {
            Text = "Use default folder", AutoSize = true, Location = new Point(0, 60),
            LinkColor = _colors.Link, ActiveLinkColor = _colors.Text,
            Font = new Font("Segoe UI", 8F)
        };
        reset.Click += (_, _) => _destinationInput.Text = _prefs.DefaultFolder;
        _destinationPanel.Controls.AddRange([folderLabel, destinationFrame, browse, reset]);

        _footer = new VesperPanel { Dock = DockStyle.Bottom, Height = 62, BackColor = _colors.Surface };
        _footer.Paint += (_, e) =>
        {
            using var separator = new Pen(_colors.Divider);
            e.Graphics.DrawLine(separator, 0, 0, _footer.Width, 0);
        };
        var cancel = MakeButton("Cancel", 85);
        cancel.DialogResult = DialogResult.Cancel;
        _queue = MakeButton("Queue", 91, symbol: VesperIcon.Queue);
        _queue.AccessibleName = "Add to queue";
        _queue.Click += (_, _) => Accept(queueOnly: true);
        _download = MakeButton("Download", 122, primary: true, symbol: VesperIcon.Download);
        _download.Click += (_, _) => Accept(queueOnly: false);
        _openPage = MakeButton("Find video", 154, primary: true, symbol: VesperIcon.Download);
        _openPage.AccessibleName = "Discover available media formats";
        _openPage.Visible = false;
        _openPage.Click += (_, _) => DiscoverVideo();
        _detected.Paint += (_, e) =>
        {
            int size = UiKit.Scale(this, 16);
            VesperIcon icon = _detectedTitle.ForeColor == _colors.Red ? VesperIcon.Warning
                : _openPage.Visible ? VesperIcon.Video : VesperIcon.Info;
            VesperDrawing.Icon(e.Graphics, icon, new Rectangle(0, UiKit.Scale(this, 4), size, size),
                VesperDrawing.Ink(icon, _colors));
        };
        _footer.Controls.AddRange([cancel, _queue, _download, _openPage]);
        void LayoutFooter()
        {
            cancel.SetBounds(S(23), S(14), S(85), S(34));
            _download.SetBounds(_footer.Width - S(23 + 122), S(14), S(122), S(34));
            _queue.SetBounds(_download.Left - S(8 + 91), S(14), S(91), S(34));
            _openPage.SetBounds(_footer.Width - S(23 + 154), S(14), S(154), S(34));
        }
        _footer.Resize += (_, _) => LayoutFooter();

        var body = new Panel { Dock = DockStyle.Fill, BackColor = _colors.Surface };
        body.Controls.AddRange([title, description, fieldLabel, _multipleLinks, _urlFrame, _hint,
            _detected, _destinationButton, _destinationPanel]);
        Controls.Add(body);
        Controls.Add(_footer);
        VesperDialogChrome.Attach(this, "VESPER  /  Add download", _colors, VesperIcon.Plus);
        CancelButton = cancel;
        AcceptButton = _download;
        Shown += (_, _) =>
        {
            // Check once on opening; do not poll the clipboard in the background.
            if (_url.TextLength == 0 && TryReadClipboard(out string clipboard))
            {
                string[] candidates = SplitLines(clipboard);
                if (candidates.Length is > 0 and <= 30 && candidates.All(candidate =>
                    Vesper.Engine.UrlPolicy.TryParse(candidate, out _, out _)))
                    SetLinkText(clipboard);
            }
            LayoutContent();
            LayoutFooter();
            _url.Focus();
        };
        DpiChanged += (_, _) => BeginInvoke(() => { if (!IsDisposed) { LayoutContent(); LayoutFooter(); } });
        if (!string.IsNullOrWhiteSpace(initialUrl)) SetLinkText(initialUrl.Trim());
        UpdateDestinationText();
        InspectUrl();
    }

    private Label TextLabel(string text, float size, bool bold = false, bool muted = false) => new()
    {
        Text = text, AutoSize = false,
        ForeColor = muted ? _colors.Muted : _colors.Text,
        Font = bold && size >= 16 ? UiKit.Heading(_colors, size)
            : new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
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

    private static string[] SplitLines(string text) => text.Split(['\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryReadClipboard(out string text)
    {
        text = "";
        try
        {
            if (!Clipboard.ContainsText()) return false;
            text = Clipboard.GetText().Trim();
            return text.Length is > 0 and <= 32768;
        }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
    }

    private void SetLinkText(string text)
    {
        if (SplitLines(text).Length > 1) SetMultipleLinks(true);
        _url.Text = text.Trim();
    }

    private void SetMultipleLinks(bool enabled)
    {
        if (_multiLine == enabled) return;
        if (!enabled && SplitLines(_url.Text).Length > 1)
        {
            MessageBox.Show(this, "Remove the extra links before switching back to one line.",
                "Multiple links", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _multiLine = enabled;
        string previousText = _url.Text;
        _url.Multiline = enabled;
        _url.WordWrap = false;
        _url.ScrollBars = enabled ? ScrollBars.Vertical : ScrollBars.None;
        _url.Text = previousText;
        _multipleLinks.Text = enabled ? "Single link" : "Multiple links";
        LayoutContent();
    }

    private void InspectUrl()
    {
        string[] links = SplitLinks();
        _paste.Symbol = _url.TextLength > 0 ? VesperIcon.Close : VesperIcon.Clipboard;
        _paste.AccessibleName = _url.TextLength > 0 ? "Clear links" : "Paste link from clipboard";
        _paste.Invalidate();
        bool valid = links.Length is > 0 and <= 200 && links.All(IsSupportedUrl);
        bool hasMedia = links.Any(IsMediaLink);
        bool allClassified = links.Length is > 0 and <= 200 &&
            links.All(link => IsSupportedUrl(link) || IsMediaLink(link));
        bool mediaRoute = hasMedia && allClassified;
        bool invalid = links.Length > 0 && !allClassified;
        _urlFrame.BorderColor = invalid ? _colors.Red : _colors.Border;
        _urlFrame.FocusColor = invalid ? _colors.Red : _colors.MutedGreen;
        _urlFrame.Invalidate();
        _queue.Enabled = _download.Enabled = valid;
        _queue.Visible = _download.Visible = !hasMedia;
        _openPage.Visible = mediaRoute;
        _discoverLink.Visible = !hasMedia && links.Length == 1 && valid;
        _hint.Visible = links.Length == 0;
        _detected.Visible = links.Length > 0;
        _detectedTitle.ForeColor = invalid ? _colors.Red : _colors.Text;

        if (links.Length == 0)
        {
            _hint.Text = "Paste a file link to continue";
        }
        else if (mediaRoute)
        {
            int mediaCount = links.Count(IsMediaLink);
            int fileCount = links.Length - mediaCount;
            _detectedTitle.Text = fileCount == 0
                ? mediaCount == 1 ? "Media link" : $"{mediaCount} media sources"
                : $"{mediaCount} media + {fileCount} direct file links";
            _detectedNote.Text = fileCount == 0 ? "Find media to see available formats."
                : "Direct file links are added alongside your media selection.";
            _openPage.Text = "Find media";
        }
        else if (!valid)
        {
            _detectedTitle.Text = "This link cannot be added as a file";
            _detectedNote.Text = "Try a file URL, HLS playlist, or a supported video page.";
        }
        else if (links.Length > 1)
        {
            _detectedTitle.Text = $"{links.Length} file addresses";
            _detectedNote.Text = "VESPER will check each file when its download begins.";
        }
        else
        {
            _detectedTitle.Text = "File link to check";
            _detectedNote.Text = "VESPER checks the file when the download starts.";
        }
        _detected.Invalidate();
        LayoutContent();
    }

    private string[] SplitLinks() => SplitLines(_url.Text);

    private static bool IsSupportedUrl(string text) =>
        Vesper.Engine.UrlPolicy.TryParse(text, out Uri url, out _)
        && !Vesper.Engine.MediaPageLinks.TryGetSite(url, out _)
        && !url.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
        && !url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);

    private static bool IsMediaLink(string text) =>
        Vesper.Engine.UrlPolicy.TryParse(text, out Uri url, out _) &&
        (Vesper.Engine.MediaPageLinks.TryGetSite(url, out _) ||
         url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase));

    private int S(int px) => IsHandleCreated ? UiKit.Scale(this, px) : px;

    private void LayoutContent()
    {
        if (_layingOut) return;
        _layingOut = true;
        try
        {
            int extra = _multiLine ? 56 : 0;
            _urlFrame.SetBounds(S(23), S(107), S(462), S(43 + extra));
            _url.SetBounds(S(12), S(12), S(400), S(22 + extra));
            _hint.SetBounds(S(23), S(157 + extra), S(462), S(22));
            _detected.SetBounds(S(23), S(156 + extra), S(462), S(47));
            int destinationY = (_url.TextLength > 0 && SplitLinks().Length > 0 ? 214 : 192) + extra;
            _destinationButton.SetBounds(S(23), S(destinationY), S(462), S(34));
            _destinationPanel.Visible = _folderExpanded;
            _destinationPanel.Location = new Point(S(23), S(destinationY + 43));
            int contentBottom = destinationY + 34 + (_folderExpanded ? 92 : 0);
            int chrome = IsHandleCreated ? VesperDialogChrome.VerticalInset(this) : VesperDialogChrome.HeaderHeight + 2;
            ClientSize = new Size(S(508), S(contentBottom + 18) + _footer.Height + chrome);
        }
        finally { _layingOut = false; }
    }

    private void DiscoverVideo(bool allowGeneric = false)
    {
        string[] links = SplitLinks();
        if (links.Length is < 1 or > 200 ||
            !links.All(link => IsSupportedUrl(link) || IsMediaLink(link))) return;
        string[] media = allowGeneric ? links : links.Where(IsMediaLink).ToArray();
        if (media.Length == 0) return;
        Uri[] urls = media.Select(link => new Uri(link)).ToArray();
        using var picker = urls.Length == 1
            ? new MediaDiscoveryForm(_prefs, urls[0])
            : new MediaDiscoveryForm(_prefs, urls);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selections.Count == 0)
        {
            string[] ordinary = allowGeneric ? [] : links.Where(IsSupportedUrl).ToArray();
            if (ordinary.Length == 0 || MessageBox.Show(this,
                $"Media discovery was cancelled or found no supported streams. Add the {ordinary.Length} ordinary file link(s) instead?",
                "Add remaining files", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            MediaJobs = [];
            DownloadUrls = ordinary.Select(link => new Uri(link).AbsoluteUri).ToArray();
            Destination = _destinationInput.Text.Trim();
            QueueOnly = false;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        MediaJobs = picker.Selections;
        // Mixed batch: preserve ordinary files instead of dropping/rejecting them.
        DownloadUrls = allowGeneric ? [] : links.Where(IsSupportedUrl).Select(link => new Uri(link).AbsoluteUri).ToArray();
        Destination = _destinationInput.Text.Trim();
        QueueOnly = picker.QueueOnly;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OpenVideoPage()
    {
        string[] links = SplitLinks();
        if (links.Length != 1 || !Vesper.Engine.UrlPolicy.TryParse(links[0], out Uri uri, out _)
            || !Vesper.Engine.MediaPageLinks.TryGetSite(uri, out _)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
                { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "The browser couldn't open this page: " + ex.Message,
                "Open video page", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void Accept(bool queueOnly)
    {
        string[] links = SplitLinks();
        if (links.Length is < 1 or > 200 || !links.All(IsSupportedUrl)) return;
        string folder = _destinationInput.Text.Trim();
        if (!Vesper.Engine.PathSafety.TryResolveFolder(folder, create: false, out string resolved, out string folderProblem)
            && !(folderProblem.Contains("does not exist") && Path.IsPathFullyQualified(folder)))
        {
            MessageBox.Show(this, folderProblem, "Invalid destination",
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
