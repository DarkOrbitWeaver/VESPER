using Vesper.Engine;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;

namespace VESPER;

/// <summary>
/// Replace Link / Rescue Assistant. It never promises recovery up front: the engine checks the new link first and
/// this dialog reports its verdict honestly. A rejected link changes nothing; the saved data stays untouched.
/// </summary>
[System.ComponentModel.DesignerCategory("Form")]
internal sealed class RescueForm : VesperPopupForm
{
    private readonly DownloadEngine _engine;
    private readonly JobSnapshot _job;
    private readonly VesperPalette _colors;
    private readonly Panel _footer;
    private readonly TextBox _url;
    private readonly TextBox _sha;
    private readonly Label _pieceLabel;
    private readonly Label _result;
    private readonly VesperButton _replace;
    private readonly VesperButton _separateCopy;
    private readonly VesperButton _cancel;
    private readonly LinkLabel _advanced;
    private readonly Label _shaLabel;
    private readonly VesperInputFrame _shaFrame;
    private readonly VesperButton _pieceButton;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LinkLabel _refreshSource;
    private bool _advancedVisible;
    private string? _pieceJson;

    public RescueForm(DownloadEngine engine, JobSnapshot job, VesperPreferences preferences)
    {
        _engine = engine;
        _job = job;
        _colors = UiKit.Palette(preferences);
        VesperWindowChrome.Brand(this);
        Text = "Replace link — VESPER";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 330 + VesperDialogChrome.HeaderHeight);
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        BackColor = _colors.Surface;
        ForeColor = _colors.Text;

        var name = Text_(job.FileName, 11F, bold: true);
        name.SetBounds(23, 16, 474, 26);
        string kept = job.CommittedBytes > 0
            ? $"{Formatting.Bytes(job.CommittedBytes)} saved{(job.TotalBytes is long t ? $" of {Formatting.Bytes(t)}" : "")}"
            : "Nothing saved yet";
        var status = Text_(kept + (job.Message.Length > 0 ? " · " + job.Message : ""), 8.5F, muted: true);
        status.SetBounds(23, 44, 474, 38);
        status.AutoEllipsis = false;

        var linkLabel = Text_("New download link", 9, bold: true);
        linkLabel.SetBounds(23, 90, 474, 20);
        (VesperInputFrame urlFrame, _url) = InputBox(23, 112, 474);

        _advanced = new LinkLabel
        {
            Text = "Advanced verification (optional)", AutoSize = true,
            Location = new Point(23, 163),
            LinkColor = _colors.Link, ActiveLinkColor = _colors.Text,
            Font = new Font("Segoe UI", 8.5F)
        };
        _advanced.LinkClicked += (_, _) => ToggleAdvanced();
        _refreshSource = new LinkLabel
        {
            Text = "Refresh original media link", AutoSize = true, Location = new Point(322, 163),
            LinkColor = _colors.Link, ActiveLinkColor = _colors.Text,
            Font = new Font("Segoe UI", 8.5F), Visible = job.Kind == JobKind.Http
                && job.OriginalPageUrl is not null && job.MediaId is not null && job.MediaFormatId is not null
        };
        _refreshSource.LinkClicked += async (_, _) => await RefreshSourceAsync();
        FormClosing += (_, _) => _lifetime.Cancel();
        FormClosed += (_, _) => _lifetime.Dispose();
        _shaLabel = Text_("Publisher-provided SHA-256", 9, bold: true);
        _shaLabel.SetBounds(23, 194, 474, 20);
        (_shaFrame, _sha) = InputBox(23, 216, 474);
        _sha.PlaceholderText = "Paste a checksum only if the source provides one";
        _sha.Text = "";

        _pieceButton = new VesperButton { Text = "Choose piece checksums…", Width = 184, Height = 30 };
        _pieceButton.ApplyPalette(_colors);
        _pieceButton.SetBounds(23, 263, 184, 30);
        _pieceButton.Click += (_, _) => ChoosePieceFile();
        _pieceLabel = Text_("Only if the publisher provides a trusted checksum list.", 8, muted: true);
        _pieceLabel.SetBounds(218, 263, 279, 30);
        _pieceLabel.AutoEllipsis = false;

        _result = Text_("", 8.75F);
        _result.SetBounds(23, 191, 474, 62);
        _result.AutoEllipsis = false;
        _result.TextAlign = ContentAlignment.TopLeft;

        _footer = new VesperPanel { Dock = DockStyle.Bottom, Height = 62, BackColor = _colors.Surface };
        _footer.Paint += (_, e) => { using var pen = new Pen(_colors.Divider); e.Graphics.DrawLine(pen, 0, 0, _footer.Width, 0); };
        _cancel = new VesperButton { Text = "Close", Width = 85, Height = 34, DialogResult = DialogResult.Cancel };
        _separateCopy = new VesperButton { Text = "Download as new copy", Width = 170, Height = 34, Visible = false };
        _replace = new VesperButton { Text = "Replace link", Width = 130, Height = 34, Primary = true, Symbol = VesperIcon.Link };
        foreach (VesperButton b in new[] { _cancel, _separateCopy, _replace }) b.ApplyPalette(_colors);
        _replace.Click += async (_, _) => await ReplaceAsync();
        _separateCopy.Click += (_, _) => StartSeparateCopy();
        _footer.Controls.AddRange([_cancel, _separateCopy, _replace]);
        void LayoutFooter()
        {
            _replace.SetBounds(_footer.Width - S(23 + 130), S(14), S(130), S(34));
            _separateCopy.SetBounds(_replace.Left - S(8 + 170), S(14), S(170), S(34));
            _cancel.SetBounds(S(23), S(14), S(85), S(34));
        }
        _footer.Resize += (_, _) => LayoutFooter();

        var body = new Panel { Dock = DockStyle.Fill, BackColor = _colors.Surface };
        body.Controls.AddRange([name, status, linkLabel, urlFrame, _advanced, _shaLabel, _shaFrame,
            _pieceButton, _pieceLabel, _result, _refreshSource]);
        Controls.Add(body);
        Controls.Add(_footer);
        VesperDialogChrome.Attach(this, "VESPER  /  Replace link", _colors, VesperIcon.Link);
        CancelButton = _cancel;
        AcceptButton = _replace;
        Shown += (_, _) => { LayoutAdvanced(); LayoutFooter(); _url.Focus(); };
        DpiChanged += (_, _) => BeginInvoke(() => { if (!IsDisposed) { LayoutAdvanced(); LayoutFooter(); } });
        LayoutAdvanced();
    }

    private Label Text_(string text, float size, bool bold = false, bool muted = false) => new()
    {
        Text = text, AutoSize = false, AutoEllipsis = true,
        ForeColor = muted ? _colors.Muted : _colors.Text, TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };

    private (VesperInputFrame, TextBox) InputBox(int x, int y, int width)
    {
        var frame = new VesperInputFrame { Location = new Point(x, y), Size = new Size(width, 36), BackColor = _colors.Surface, BorderColor = _colors.Border, FocusColor = _colors.MutedGreen };
        var box = new TextBox { BorderStyle = BorderStyle.None, Location = new Point(10, 9), Width = width - 20, BackColor = _colors.Surface, ForeColor = _colors.Text };
        box.GotFocus += (_, _) => { frame.Active = true; frame.Invalidate(); };
        box.LostFocus += (_, _) => { frame.Active = false; frame.Invalidate(); };
        frame.Controls.Add(box);
        return (frame, box);
    }

    private void ToggleAdvanced()
    {
        _advancedVisible = !_advancedVisible;
        LayoutAdvanced();
    }

    private int S(int px) => IsHandleCreated ? UiKit.Scale(this, px) : px;

    private void LayoutAdvanced()
    {
        _shaLabel.Visible = _shaFrame.Visible = _pieceButton.Visible = _pieceLabel.Visible = _advancedVisible;
        _advanced.Text = _advancedVisible ? "Hide advanced verification" : "Advanced verification (optional)";
        int messageY = _advancedVisible ? 310 : 191;
        _result.SetBounds(S(23), S(messageY), S(474), S(70));
        _result.Visible = _result.Text.Length > 0;
        int body = messageY + (_result.Text.Length > 0 ? 78 : 17);
        int chrome = IsHandleCreated ? VesperDialogChrome.VerticalInset(this) : VesperDialogChrome.HeaderHeight + 2;
        ClientSize = new Size(S(520), S(body) + _footer.Height + chrome);
    }

    private void ChoosePieceFile()
    {
        using var picker = new OpenFileDialog { Title = "Trusted piece-hash file", Filter = "Piece hashes (*.json)|*.json|All files|*.*", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 8 * 1024 * 1024) { Show("That file is too large to be a piece-hash list.", error: true); return; }
            string json = File.ReadAllText(picker.FileName);
            if (!PieceMap.TryParse(json, out PieceMap map, out string problem)) { Show(problem, error: true); return; }
            _pieceJson = json;
            _pieceLabel.Text = $"{Path.GetFileName(picker.FileName)} · {map.PieceCount} pieces of {Formatting.Bytes(map.PieceLength)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Show("The file could not be read: " + ex.Message, error: true); }
    }

    private async Task ReplaceAsync()
    {
        string link = _url.Text.Trim();
        if (link.Length == 0) { Show("Paste the new download link first.", error: true); return; }
        if (UrlPolicy.TryParse(link, out Uri uri, out _) && MediaPageLinks.TryGetSite(uri, out string site))
        {
            Show($"That's a {site} video page, not a direct file. Open the page in your browser and use VESPER's butterfly if a downloadable format is found.", error: true);
            return;
        }

        SetBusy(true);
        Show("Checking the new link…", error: false);
        try
        {
            RescueResult result = await _engine.ReplaceLinkAsync(_job.Id, link, new RescueInput(_sha.Text.Trim(), _pieceJson), _lifetime.Token);
            if (result.Accepted)
            {
                string message = result.RetainedBytes == 0
                    ? "Link updated. VESPER will check the file when it starts."
                    : result.Mode == RescueMode.R1
                        ? "New link saved. Your existing data will be checked against the trusted checksum before the file is finished."
                        : "Link updated. Your saved progress was kept where it could be safely reused.";
                if (result.PiecesToRefetch > 0)
                    message += $" {result.PiecesToRefetch} missing or damaged parts will be fetched.";
                Show(message, error: false);
                _replace.Visible = false;
                _cancel.Text = "Done";
            }
            else
            {
                Show("This link can't safely continue the saved file. Your existing data was not changed. Try another link or download a new copy.", error: true);
                _result.AccessibleDescription = result.Message;
                _separateCopy.Visible = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Show("Something went wrong while checking the link: " + ex.Message + "\nYour saved data was not changed.", error: true);
        }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private async Task RefreshSourceAsync()
    {
        if (!Uri.TryCreate(_job.OriginalPageUrl, UriKind.Absolute, out var page)) return;
        CancellationToken token = _lifetime.Token;
        SetBusy(true);
        Show("Resolving the original page and checking the selected format…", error: false);
        try
        {
            using var resolver = new DefaultMediaResolver();
            var discovery = await Task.Run(() => resolver.ResolveAsync(new MediaDiscoveryRequest
            { Url = page, Options = new DiscoveryOptions { CollectionIntent = CollectionIntent.ItemOnly } }, token), token);
            if (IsDisposed || token.IsCancellationRequested) return;
            if (discovery.Item is not { } item)
            { Show(discovery.Error?.UserMessage ?? "The original page could not be resolved. Saved data was kept.", true); return; }
            var format = item.Representations.FirstOrDefault(r => r.Id == _job.MediaFormatId);
            if (format is null || format.Protocol != StreamProtocol.Http || format.Context.HasCredentials
                || format.Context.Origin is not null || format.Context.AcceptLanguage is not null)
            { Show("The exact format or its required request context is unavailable. Restart as a separate copy; saved data was kept.", true); return; }
            if (item.MediaId is null)
            { Show("The source does not expose a verifiable media identity. Saved data was kept.", true); return; }
            var result = await _engine.RefreshMediaSourceAsync(_job.Id, new ResolvedMediaSource(item.Provider,
                item.MediaId, format.Id, format.Url.AbsoluteUri, format.ExpiresAt), token);
            if (IsDisposed || token.IsCancellationRequested) return;
            Show(result.Message, !result.Accepted);
            if (result.Accepted) { _replace.Visible = false; _refreshSource.Visible = false; _cancel.Text = "Done"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!IsDisposed) Show("Source refresh failed. No replacement was accepted; saved data was kept.", true); }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private void StartSeparateCopy()
    {
        AddResult added = _engine.Add(new AddRequest(_url.Text.Trim(), DestinationFolder: _job.Destination, StartNow: false));
        if (!added.Ok) { Show(added.Message, error: true); return; }
        Show("A separate download was queued. The original partial file was left as it was.", error: false);
        _separateCopy.Visible = false;
    }

    private void SetBusy(bool busy)
    {
        _replace.Enabled = _refreshSource.Enabled = !busy;
        _url.Enabled = _sha.Enabled = _pieceButton.Enabled = _advanced.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void Show(string text, bool error)
    {
        _result.ForeColor = error ? _colors.Red : _colors.Text;
        _result.Text = text;
        LayoutAdvanced();
    }
}
