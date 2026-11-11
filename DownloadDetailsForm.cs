using Vesper.Engine;

namespace VESPER;

/// <summary>
/// Compact, state-aware details for one download. Everything shown comes from the engine; actions are limited to
/// what makes sense for the current state. Updates arrive through engine events, not polling.
/// </summary>
[System.ComponentModel.DesignerCategory("Form")]
internal sealed class DownloadDetailsForm : Form
{
    private readonly DownloadEngine _engine;
    private readonly DownloadActions _actions;
    private readonly VesperPalette _colors;
    private readonly Panel _content;
    private readonly Panel _footer;
    private readonly ToolTip _tips = new();
    private readonly VesperButton _close;
    private readonly Dictionary<string, Label> _live = [];
    private Panel? _progressFill;
    private int _progressWidth;
    private JobSnapshot _job;
    private bool _building;

    public DownloadDetailsForm(DownloadEngine engine, DownloadActions actions, JobSnapshot job, VesperPreferences preferences)
    {
        _engine = engine;
        _actions = actions;
        _job = job;
        _colors = UiKit.Palette(preferences);
        VesperWindowChrome.Brand(this);
        Text = "Download details — VESPER";
        Font = new Font("Segoe UI", 9F);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(516, 345);
        BackColor = _colors.Surface;
        ForeColor = _colors.Text;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;

        _content = new Panel { Dock = DockStyle.Fill, BackColor = _colors.Surface };
        _footer = new VesperPanel { Dock = DockStyle.Bottom, Height = 66, BackColor = _colors.Surface };
        _footer.Paint += (_, e) =>
        {
            using var line = new Pen(_colors.Border);
            e.Graphics.DrawLine(line, 19, 0, _footer.Width - 19, 0);
        };
        _close = NewButton("Close", 83, primary: false);
        _close.DialogResult = DialogResult.Cancel;
        _close.Click += (_, _) => Close();
        Controls.Add(_content);
        Controls.Add(_footer);
        VesperDialogChrome.Attach(this, "Download details", _colors);
        CancelButton = _close;

        _engine.JobChanged += OnJobChanged;
        _engine.Progress += OnProgress;
        Build();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _engine.JobChanged -= OnJobChanged;
        _engine.Progress -= OnProgress;
        base.OnFormClosed(e);
    }

    // ---- live updates ---------------------------------------------------------------------------------------------

    private void OnJobChanged(JobSnapshot s)
    {
        if (s.Id == _job.Id) Post(() => Apply(s));
    }

    private void OnProgress(IReadOnlyList<JobSnapshot> batch)
    {
        JobSnapshot? mine = batch.FirstOrDefault(s => s.Id == _job.Id);
        if (mine is not null) Post(() => Apply(mine));
    }

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { /* closing */ }
    }

    private void Apply(JobSnapshot s)
    {
        bool structural = s.State != _job.State || s.Message != _job.Message || s.FileMissing != _job.FileMissing
            || s.FileName != _job.FileName || s.Reason != _job.Reason || s.SourceUrl != _job.SourceUrl;
        _job = s;
        if (structural) Build(); else UpdateLive();
    }

    private void UpdateLive()
    {
        foreach ((string key, Label label) in _live)
        {
            string value = Fields().FirstOrDefault(f => f.Key == key).Value ?? label.Text;
            if (label.Text != value) label.Text = value;
        }
        if (_progressFill is not null)
            _progressFill.Width = (int)(_progressWidth * Math.Clamp(_job.Fraction ?? 0, 0, 1));
    }

    // ---- layout ---------------------------------------------------------------------------------------------------

    private VesperButton NewButton(string text, int width, bool primary)
    {
        var button = new VesperButton { Text = text, Primary = primary, Size = new Size(width, 33) };
        button.ApplyPalette(_colors);
        return button;
    }

    private Label Text_(string text, int x, int y, int width, int height, float size = 9, bool bold = false, bool muted = false) => new()
    {
        Text = text, AutoSize = false, AutoEllipsis = true, Location = new Point(x, y), Size = new Size(width, height),
        BackColor = Color.Transparent, ForeColor = muted ? _colors.Muted : _colors.Text, TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };

    private void Build()
    {
        if (_building) return;
        _building = true;
        try
        {
            _tips.RemoveAll();
            _live.Clear();
            foreach (Control control in _content.Controls.Cast<Control>().ToArray()) control.Dispose();
            foreach (Control control in _footer.Controls.Cast<Control>().ToArray())
            {
                _footer.Controls.Remove(control);
                if (!ReferenceEquals(control, _close)) control.Dispose();
            }

            const int margin = 24, textX = 87, valueX = 164;
            int bodyWidth = ClientSize.Width, contentWidth = bodyWidth - 2 * margin;

            var emblem = new Panel { Location = new Point(margin, 18), Size = new Size(49, 49), BackColor = _colors.Surface };
            emblem.Paint += (_, e) =>
            {
                VesperIcon symbol = DownloadItem.From(_job).FileType switch
                {
                    "Audio" => VesperIcon.Audio, "Video" => VesperIcon.Video, "Archive" => VesperIcon.Archive,
                    "Disc image" => VesperIcon.Disc, "eBook" => VesperIcon.Book, _ => VesperIcon.File
                };
                Graphics g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var outline = VesperDrawing.Rounded(new Rectangle(1, 1, 47, 47), 7))
                {
                    using var fill = new SolidBrush(_colors.Secondary);
                    using var stroke = new Pen(_colors.Border);
                    g.FillPath(fill, outline);
                    g.DrawPath(stroke, outline);
                }
                VesperDrawing.Icon(g, symbol, new Rectangle(11, 11, 27, 27), _colors.MutedGreen);
            };
            _content.Controls.Add(emblem);

            Label name = Text_(_job.FileName, textX, 13, bodyWidth - textX - margin, 28, 10F, bold: true);
            _content.Controls.Add(name);
            _tips.SetToolTip(name, _job.FileName);
            DownloadItem item = DownloadItem.From(_job);
            _content.Controls.Add(Text_($"{item.FileType} · {item.Extension}  ({item.Size})", textX, 39, bodyWidth - textX - margin, 19, 8.25F, muted: true));

            (string status, VesperIcon icon, Color ink) = StatusLine();
            Label state = Text_(status, textX + 19, 62, bodyWidth - textX - margin - 19, 21, 9, bold: true);
            state.ForeColor = ink;
            _content.Controls.Add(state);
            var stateIcon = new Panel { Location = new Point(textX, 64), Size = new Size(16, 16), BackColor = _colors.Surface };
            stateIcon.Paint += (_, e) => VesperDrawing.Icon(e.Graphics, icon, new Rectangle(0, 0, 16, 16), ink);
            _content.Controls.Add(stateIcon);

            int y = 100;
            _progressFill = null;
            if (_job.Fraction is double fraction && _job.ProgressBytes > 0 && _job.State != JobState.Completed)
            {
                _progressWidth = contentWidth;
                var track = new Panel { Location = new Point(margin, y), Size = new Size(contentWidth, 4), BackColor = _colors.Border };
                _progressFill = new Panel
                {
                    Location = Point.Empty, Size = new Size((int)(contentWidth * fraction), 4),
                    BackColor = _job.State is JobState.Downloading ? _colors.Green : _job.State == JobState.Paused ? _colors.Amber : _colors.MutedGreen
                };
                track.Controls.Add(_progressFill);
                _content.Controls.Add(track);
                y += 19;
            }

            if (Explanation() is { Length: > 0 } explanation)
            {
                Label note = Text_(explanation, margin, y, contentWidth, 54, 8.5F, muted: true);
                note.AutoEllipsis = false;
                note.TextAlign = ContentAlignment.TopLeft;
                int lines = Math.Max(1, (int)Math.Ceiling(TextRenderer.MeasureText(explanation, note.Font).Width / (double)(contentWidth - 6)));
                note.Height = Math.Min(70, 8 + lines * 16);
                _content.Controls.Add(note);
                y += note.Height + 6;
            }
            _content.Controls.Add(new Panel { Location = new Point(margin, y), Size = new Size(contentWidth, 1), BackColor = _colors.Border });
            y += 12;

            foreach ((string key, string value) in Fields())
            {
                VesperIcon glyph = key switch
                {
                    "Progress" => VesperIcon.Download, "Transfer" or "Bandwidth" => VesperIcon.Gauge, "Time left" => VesperIcon.Clock,
                    "Source" => VesperIcon.Link, "Save to" or "Last known path" or "Partial file" => VesperIcon.Folder,
                    "Integrity" or "Recovery" => VesperIcon.Check, _ => VesperIcon.File
                };
                var fieldIcon = new Panel { Location = new Point(margin, y + 6), Size = new Size(15, 15), BackColor = _colors.Surface };
                fieldIcon.Paint += (_, e) => VesperDrawing.Icon(e.Graphics, glyph, new Rectangle(0, 0, 15, 15), _colors.MutedGreen);
                _content.Controls.Add(fieldIcon);
                _content.Controls.Add(Text_(key, margin + 23, y, valueX - margin - 31, 28, 8.5F, muted: true));
                Label field = Text_(value, valueX, y, bodyWidth - valueX - margin, 28, 8.8F);
                _content.Controls.Add(field);
                _live[key] = field;
                _tips.SetToolTip(field, key == "Source" ? "Use the row menu → Copy source URL for the full link." : value);
                y += 31;
            }

            ClientSize = new Size(bodyWidth, VesperDialogChrome.HeaderHeight + y + 13 + _footer.Height);

            int actionY = 17, x = 23;
            foreach ((string text, bool primary, Action action) in Actions())
            {
                int width = Math.Max(92, TextRenderer.MeasureText(text, Font).Width + 40);
                VesperButton button = NewButton(text, width, primary);
                button.SetBounds(x, actionY, width, 33);
                button.Click += (_, _) => action();
                _footer.Controls.Add(button);
                x += width + 8;
            }
            _close.SetBounds(_footer.Width - 23 - 83, actionY, 83, 33);
            _close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _footer.Controls.Add(_close);
            _footer.Invalidate();
        }
        finally { _building = false; }
    }

    // ---- content by state -----------------------------------------------------------------------------------------

    private (string Text, VesperIcon Icon, Color Color) StatusLine()
    {
        int percent = _job.Fraction is double f ? (int)Math.Floor(f * 100) : 0;
        return _job.State switch
        {
            JobState.Completed when _job.FileMissing => ("Completed · file missing", VesperIcon.Warning, _colors.Amber),
            JobState.Completed => ("Completed", VesperIcon.Check, _colors.Green),
            JobState.Downloading => (_job.Fraction is null ? "Downloading" : $"Downloading ({percent}%)", VesperIcon.Download, _colors.Green),
            JobState.Resolving => ("Contacting server", VesperIcon.Info, _colors.Muted),
            JobState.Paused => ("Paused", VesperIcon.Pause, _colors.Amber),
            JobState.Queued => ("Queued", VesperIcon.Queue, _colors.Muted),
            JobState.WaitingForNetwork => ("Waiting for network", VesperIcon.Info, _colors.Amber),
            JobState.WaitingForRetry => ("Waiting to retry", VesperIcon.Clock, _colors.Amber),
            JobState.NeedsLink => ("Needs a new link", VesperIcon.Warning, _colors.Red),
            JobState.NeedsAttention => ("Needs attention", VesperIcon.Warning, _colors.Red),
            JobState.Verifying => ("Verifying checksum", VesperIcon.Info, _colors.Green),
            JobState.Finalizing => ("Finishing", VesperIcon.Info, _colors.Green),
            JobState.Suspect => ("Suspect integrity", VesperIcon.Warning, _colors.Red),
            JobState.Failed => ("Failed", VesperIcon.Warning, _colors.Red),
            _ => ("Cancelled", VesperIcon.Info, _colors.Muted)
        };
    }

    private string Explanation() => _job.State switch
    {
        JobState.Completed when _job.FileMissing => "The saved file is no longer at its recorded location. The download itself did not fail.",
        JobState.NeedsLink => $"The link no longer works. {(_job.CommittedBytes > 0 ? $"{Formatting.Bytes(_job.CommittedBytes)} is saved. " : "")}" +
            "VESPER checks that a replacement serves the same file before reusing any saved data.",
        JobState.Paused when _job.CommittedBytes > 0 => $"{Formatting.Bytes(_job.CommittedBytes)} is saved safely and will be reused.",
        JobState.Completed or JobState.Downloading or JobState.Queued or JobState.Resolving => _job.State == JobState.Downloading ? _job.Message : "",
        _ => _job.Message
    };

    private IEnumerable<(string Key, string Value)> Fields()
    {
        bool unfinished = _job.State != JobState.Completed;
        if (_job.TotalBytes is long total && (unfinished && _job.ProgressBytes > 0))
        {
            string progress = _job.State == JobState.Downloading
                ? $"{Formatting.Bytes(_job.ProgressBytes)} of {Formatting.Bytes(total)}"
                : $"{Formatting.Bytes(_job.CommittedBytes)} saved of {Formatting.Bytes(total)}";
            yield return ("Progress", progress);
        }
        else if (unfinished && _job.CommittedBytes > 0) yield return ("Progress", $"{Formatting.Bytes(_job.CommittedBytes)} saved");

        if (_job.State == JobState.Downloading && _job.SpeedBytesPerSecond > 0)
            yield return ("Transfer", $"{Formatting.Rate(_job.SpeedBytesPerSecond)} · {_job.Connections} connection{(_job.Connections == 1 ? "" : "s")}");
        if (_job.Eta is { } eta) yield return ("Time left", Formatting.Eta(eta));
        if (_job.TotalBytes is null || !unfinished || _job.ProgressBytes == 0)
            yield return ("Size", _job.TotalBytes is long t ? Formatting.Bytes(t) : "Unknown");

        string path = _job.State == JobState.Completed ? _job.FinalPath ?? "" : Path.Combine(_job.Destination, _job.FileName);
        yield return (_job.State == JobState.Completed && _job.FileMissing ? "Last known path" : "Save to", PathSafety.ForDisplay(path));
        if (unfinished && _job.CommittedBytes > 0) yield return ("Partial file", PathSafety.ForDisplay(_job.PartialPath));
        if (_job.OriginalPageUrl is { Length: > 0 } original)
        {
            yield return ("Original page", UrlPolicy.Redact(original));
            if (_job.MediaProvider is { Length: > 0 } provider) yield return ("Provider", provider);
            if (_job.MediaQuality is { Length: > 0 } quality) yield return ("Quality", quality);
            if (_job.MediaExpiresAt is { } expiry) yield return ("Source link expires", expiry.ToLocalTime().ToString("g"));
        }
        else if (_job.SourceUrl.Length > 0) yield return ("Source", UrlPolicy.Redact(_job.SourceUrl));
        if (_job.Sha256Verified) yield return ("Integrity", "Matches the trusted SHA-256");
        else if (_job.HasExpectedHash && unfinished) yield return ("Integrity", "A trusted SHA-256 will be checked at the end");
        if (_job.Rescue is not (RescueMode.None or RescueMode.R4)) yield return ("Recovery", RescueText(_job.Rescue));
        if (_job.State is JobState.Downloading or JobState.Paused or JobState.Queued or JobState.WaitingForRetry)
            yield return ("Bandwidth", Formatting.RateLimit(_job.SpeedLimitBytes));
    }

    private static string RescueText(RescueMode mode) => mode switch
    {
        RescueMode.R0 => "Same file confirmed by strong identity",
        RescueMode.R1 => "Continued from a new link; final checksum is mandatory",
        RescueMode.R1b => "Verified piece by piece against trusted checksums",
        _ => ""
    };

    private IEnumerable<(string Text, bool Primary, Action Action)> Actions()
    {
        JobSnapshot j = _job;
        if (j.OriginalPageUrl is { Length: > 0 } original && Uri.TryCreate(original, UriKind.Absolute, out Uri? source)
            && (source.Scheme is "https" or "http"))
            yield return ("View original", false, () =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(source.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                { MessageBox.Show(this, ex.Message, "Open source", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            });
        switch (j.State)
        {
            case JobState.Completed when j.FileMissing: yield return ("Locate file", true, () => _actions.Locate(j)); break;
            case JobState.Completed:
                yield return ("Open", true, () => _actions.Open(j));
                yield return ("Show in folder", false, () => _actions.ShowFolder(j));
                break;
            case JobState.Resolving or JobState.Downloading or JobState.WaitingForNetwork or JobState.WaitingForRetry:
                yield return ("Pause", false, () => _engine.Pause(j.Id));
                break;
            case JobState.Paused: yield return ("Resume", true, () => _engine.Resume(j.Id)); break;
            case JobState.Queued: yield return ("Start now", true, () => _engine.StartNow(j.Id)); break;
            case JobState.NeedsLink: yield return ("Replace link", true, () => _actions.ReplaceLink(j)); break;
            case JobState.Failed:
                yield return ("Try again", true, () => _engine.Resume(j.Id));
                yield return ("Replace link", false, () => _actions.ReplaceLink(j));
                break;
            case JobState.Suspect:
                yield return ("Show partial file", false, () => _actions.ShowFolder(j));
                if (j.CommittedBytes > 0) yield return ("Start over…", false, () => _actions.StartOver(j));
                break;
            case JobState.NeedsAttention:
                foreach (var action in AttentionActions(j)) yield return action;
                break;
        }
    }

    private IEnumerable<(string, bool, Action)> AttentionActions(JobSnapshot j)
    {
        switch (j.Reason)
        {
            case "no-validator":
                yield return ("Resume anyway…", true, () => _actions.ApproveUnverified(j));
                yield return ("Replace link", false, () => _actions.ReplaceLink(j));
                break;
            case "changed" or "range-ignored" or "range-rejected" or "bad-range" or "piece-mismatch" or "html" or "auth" or "forbidden":
                yield return ("Replace link", true, () => _actions.ReplaceLink(j));
                if (j.CommittedBytes > 0) yield return ("Start over…", false, () => _actions.StartOver(j));
                break;
            default:
                yield return ("Try again", true, () => _engine.Resume(j.Id));
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
