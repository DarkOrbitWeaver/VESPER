namespace VESPER;

// UI preview only. Information comes from DownloadItem, not the disk or network.
[System.ComponentModel.DesignerCategory("Form")]
internal sealed class DownloadDetailsForm : Form
{
    private readonly DownloadItem _item;
    private readonly VesperPalette _colors;
    private readonly Action? _changed;
    private readonly Panel _content;
    private readonly Panel _footer;
    private readonly ToolTip _tips = new();
    private readonly VesperButton _close;
    private bool _updating;

    public DownloadDetailsForm() : this(DownloadItem.Examples()[3], new VesperPreferences()) { }

    public DownloadDetailsForm(DownloadItem item, VesperPreferences preferences, Action? changed = null)
    {
        _item = item;
        _changed = changed;
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
        MaximizeBox = false;
        MinimizeBox = false;

        _content = new Panel { Dock = DockStyle.Fill, BackColor = _colors.Surface };
        _footer = new VesperPanel { Dock = DockStyle.Bottom, Height = 66, BackColor = _colors.Surface };
        _footer.Paint += (_, e) =>
        {
            using var line = new Pen(_colors.Border);
            e.Graphics.DrawLine(line, 19, 0, _footer.Width - 19, 0);
        };
        _close = Button("Close", 83);
        _close.DialogResult = DialogResult.Cancel;
        _close.Click += (_, _) => Close();
        Controls.Add(_content);
        Controls.Add(_footer);
        VesperDialogChrome.Attach(this, "Download details", _colors);
        CancelButton = _close;
        Build();
    }

    private VesperButton Button(string text, int width, bool primary = false)
    {
        var button = new VesperButton { Text = text, Primary = primary, Size = new Size(width, 33) };
        button.ApplyPalette(_colors);
        return button;
    }

    private Label Label(string text, int x, int y, int width, int height, float fontSize = 9,
        bool bold = false, bool muted = false)
    {
        return new Label
        {
            Text = text, AutoSize = false, AutoEllipsis = true,
            Location = new Point(x, y), Size = new Size(width, height),
            BackColor = Color.Transparent,
            ForeColor = muted ? _colors.Muted : _colors.Text,
            Font = new Font("Segoe UI", fontSize, bold ? FontStyle.Bold : FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private void Build()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            _tips.RemoveAll();
            foreach (Control control in _content.Controls.Cast<Control>().ToArray()) control.Dispose();
            foreach (Control control in _footer.Controls.Cast<Control>().ToArray())
            {
                _footer.Controls.Remove(control);
                if (!ReferenceEquals(control, _close)) control.Dispose();
            }

            const int margin = 24;
            const int textX = 87;
            const int valueX = 164;
            int bodyWidth = ClientSize.Width;
            int contentWidth = bodyWidth - 2 * margin;

            // One recognizable file icon replaces the old repeated headings.
            var emblem = new Panel
            {
                Location = new Point(margin, 18), Size = new Size(49, 49),
                BackColor = _colors.Surface
            };
            emblem.Paint += (_, e) =>
            {
                var symbol = _item.FileType switch
                {
                    "Audio" => VesperIcon.Audio,
                    "Video" => VesperIcon.Video,
                    "Archive" => VesperIcon.Archive,
                    "Disc image" => VesperIcon.Disc,
                    "eBook" => VesperIcon.Book,
                    _ => VesperIcon.File
                };
                var g = e.Graphics;
                var old = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var outline = VesperDrawing.Rounded(new Rectangle(1, 1, 47, 47), 7))
                {
                    using var fill = new SolidBrush(_colors.Secondary);
                    using var stroke = new Pen(_colors.Border);
                    g.FillPath(fill, outline);
                    g.DrawPath(stroke, outline);
                }
                VesperDrawing.Icon(g, symbol, new Rectangle(11, 11, 27, 27), _colors.MutedGreen);
                g.SmoothingMode = old;
            };
            _content.Controls.Add(emblem);

            var name = Label(_item.Name, textX, 13, bodyWidth - textX - margin, 28, 10F, bold: true);
            _content.Controls.Add(name);
            _tips.SetToolTip(name, _item.Name);
            _content.Controls.Add(Label($"{_item.FileType} · {_item.Extension}  ({_item.Size})",
                textX, 39, bodyWidth - textX - margin, 19, 8.25F, muted: true));

            (string status, VesperIcon icon, Color ink) = Status();
            var state = Label(status + " · demo", textX + 19, 62, bodyWidth - textX - margin - 19, 21, 9, true);
            state.ForeColor = ink;
            _content.Controls.Add(state);
            var stateIcon = new Panel { Location = new Point(textX, 64), Size = new Size(16, 16), BackColor = _colors.Surface };
            stateIcon.Paint += (_, e) => VesperDrawing.Icon(e.Graphics, icon, new Rectangle(0, 0, 16, 16), ink);
            _content.Controls.Add(stateIcon);

            int y = 100;
            if (_item.Percent is int percent && _item.Status is JobStatus.Downloading or JobStatus.Paused or JobStatus.NeedsLink)
            {
                var progress = new Panel { Location = new Point(margin, y), Size = new Size(contentWidth, 4), BackColor = _colors.Border };
                var fill = new Panel
                {
                    Location = Point.Empty,
                    Size = new Size(contentWidth * Math.Clamp(percent, 0, 100) / 100, 4),
                    BackColor = _item.Status == JobStatus.Paused ? _colors.Amber : _colors.Green
                };
                progress.Controls.Add(fill);
                _content.Controls.Add(progress);
                y += 19;
            }

            string? explanation = _item.Status switch
            {
                JobStatus.Missing => "The saved file could not be found at its recorded location.",
                JobStatus.NeedsLink => "The original link needs replacing. Saved progress is not discarded.",
                _ => null
            };
            if (explanation is not null)
            {
                var note = Label(explanation, margin, y, contentWidth, 37, 8.5F, muted: true);
                note.AutoEllipsis = false;
                _content.Controls.Add(note);
                y += 43;
            }
            var divider = new Panel { Location = new Point(margin, y), Size = new Size(contentWidth, 1), BackColor = _colors.Border };
            _content.Controls.Add(divider);
            y += 12;

            foreach (var (key, value) in Fields())
            {
                VesperIcon glyph = key switch
                {
                    "Progress" => VesperIcon.Download,
                    "Transfer" or "Bandwidth" => VesperIcon.Gauge,
                    "Time left" => VesperIcon.Clock,
                    "Source" => VesperIcon.Link,
                    "Save to" or "Last known path" => VesperIcon.Folder,
                    _ => VesperIcon.File
                };
                var fieldIcon = new Panel { Location = new Point(margin, y + 6), Size = new Size(15, 15), BackColor = _colors.Surface };
                fieldIcon.Paint += (_, e) => VesperDrawing.Icon(e.Graphics, glyph, new Rectangle(0, 0, 15, 15), _colors.MutedGreen);
                _content.Controls.Add(fieldIcon);
                _content.Controls.Add(Label(key, margin + 23, y, valueX - margin - 31, 28, 8.5F, muted: true));
                var field = Label(value, valueX, y, bodyWidth - valueX - margin, 28, 8.8F);
                _content.Controls.Add(field);
                _tips.SetToolTip(field, value);
                y += 31;
            }
            // The footer contains all actions: no oversized disclosure panel.
            int height = VesperDialogChrome.HeaderHeight + y + 13 + _footer.Height;
            ClientSize = new Size(bodyWidth, height);

            _footer.Controls.Add(Label("DEMO · No files or transfers verified", 23, 5, 335, 18, 7.7F, muted: true));
            int actionY = 27;
            int x = 23;
            foreach (var (text, primary) in Actions())
            {
                int buttonWidth = text.Length > 13 ? 145 : 102;
                var action = Button(text, buttonWidth, primary);
                action.SetBounds(x, actionY, buttonWidth, 33);
                action.Click += (_, _) => PerformDemoAction(text);
                _footer.Controls.Add(action);
                x += buttonWidth + 8;
            }
            _close.SetBounds(_footer.Width - 23 - 83, actionY, 83, 33);
            _close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _footer.Controls.Add(_close);
            _footer.Invalidate();
        }
        finally { _updating = false; }
    }

    private (string Text, VesperIcon Icon, Color Color) Status() => _item.Status switch
    {
        JobStatus.Missing => ("File missing", VesperIcon.Warning, _colors.Amber),
        JobStatus.NeedsLink => ("Needs link", VesperIcon.Warning, _colors.Red),
        JobStatus.Downloading => ($"Downloading ({_item.Percent ?? 0}%)", VesperIcon.Download, _colors.Green),
        JobStatus.Paused => ("Paused", VesperIcon.Pause, _colors.Amber),
        JobStatus.Completed => ("Completed", VesperIcon.Check, _colors.Green),
        JobStatus.Queued => ("Queued", VesperIcon.Queue, _colors.Muted),
        _ => ("Waiting", VesperIcon.Info, _colors.Muted)
    };

    private IEnumerable<(string Key, string Value)> Fields()
    {
        if (_item.Percent.HasValue) yield return ("Progress", $"{_item.Percent}% saved (sample)");
        if (!string.IsNullOrWhiteSpace(_item.Transfer)) yield return ("Transfer", _item.Transfer);
        if (!string.IsNullOrWhiteSpace(_item.Eta)) yield return ("Time left", _item.Eta);
        if (!_item.Percent.HasValue) yield return ("Size", _item.Size);
        string destination = string.IsNullOrWhiteSpace(_item.Destination)
            ? "%USERPROFILE%\\Downloads\\" + _item.Name
            : Path.Combine(_item.Destination, _item.Name);
        yield return (_item.Status == JobStatus.Missing ? "Last known path" : "Save to", destination + " (sample)");
        if (!string.IsNullOrWhiteSpace(_item.SourceUrl)) yield return ("Source", _item.SourceUrl);
        if (_item.Status is JobStatus.Downloading or JobStatus.Paused or JobStatus.NeedsLink)
            yield return ("Bandwidth", _item.RateCap);
    }

    private IEnumerable<(string Text, bool Primary)> Actions()
    {
        return _item.Status switch
        {
            JobStatus.Missing => [("Locate file", true), ("Download again", false)],
            JobStatus.NeedsLink => [("Replace link", true)],
            JobStatus.Downloading => [("Pause", false)],
            JobStatus.Paused => [("Resume", true)],
            JobStatus.Completed => [("Show in folder", false)],
            _ => []
        };
    }

    private void PerformDemoAction(string action)
    {
        if (action == "Pause")
        {
            _item.Status = JobStatus.Paused;
            _item.Transfer = "";
            _item.Eta = "";
            _changed?.Invoke();
            Build();
        }
        else if (action == "Resume")
        {
            _item.Status = JobStatus.Queued;
            _changed?.Invoke();
            Build();
        }
        else
            MessageBox.Show(this, $"{action} requires the future download engine or a verified file. Nothing was changed.",
                "UI demonstration", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
