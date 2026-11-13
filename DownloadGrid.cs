using System.ComponentModel;
using System.Drawing.Drawing2D;
using Vesper.Engine;
using Vesper.MediaResolver.Core;

namespace VESPER;

// A virtual list, not a DataGridView. Only visible records are painted.
// This avoids native cell borders and focus outlines bleeding through the design.
public sealed class DownloadGrid : ScrollableControl
{
    private IReadOnlyList<DownloadItem> _items = [];
    private readonly HashSet<Guid> _checked = [];
    private readonly ToolTip _tips = new() { InitialDelay = 750, ReshowDelay = 250 };
    private readonly Font _textFont = new("Segoe UI", 9F);
    private readonly Font _headerFont = new("Segoe UI", 8F, FontStyle.Bold);
    private readonly Font _nameFont = new("Segoe UI", 9F, FontStyle.Bold);
    private readonly Font _subFont = new("Segoe UI", 8F);
    private VesperPalette _palette = VesperPalette.Light;
    private Density _density = Density.Compact;
    private int _rowHeight;
    private int _headerHeight;
    private int _hoverRow = -1;
    private int _focusRow;
    private int _hoverColumn = -1;
    private string _tipText = "";
    private (DownloadItem Item, Rectangle Threshold, bool Details)? _filePress;
    private readonly int[] _columnWidths = new int[6];
    private int _contentWidth;

    public event Action? CheckStateChanged;
    internal event Action<DownloadItem>? DetailsRequested;
    internal event Action<DownloadItem, Point>? MenuRequested;
    internal event Action<DownloadItem>? OpenRequested;

    /// <summary>Shown when the list is empty. Set by the owner: "No downloads yet" differs from "No matches".</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string EmptyTitle { get; set; } = "No matching downloads";
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string EmptyHint { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Image? EmptyIllustration { get; set; }

    private readonly Dictionary<string, Bitmap?> _posters = [];
    private readonly CancellationTokenSource _posterLifetime = new();
    private bool _loadingPosters;

    private readonly Font _emptyFont = new("Georgia", 19F, FontStyle.Italic);

    /// <summary>Repaints rows after an in-place change (progress tick). Only visible rows are drawn.</summary>
    internal void RefreshRows<T>(IReadOnlyList<T> changes, Func<T, Guid> idOf)
    {
        int first = Math.Clamp(-AutoScrollPosition.Y / Math.Max(1, _rowHeight), 0, _items.Count);
        int last = Math.Min(_items.Count, first + Math.Max(0, (ClientSize.Height - _headerHeight) / Math.Max(1, _rowHeight) + 2));
        for (int index = first; index < last; index++)
        {
            Guid id = _items[index].Id;
            foreach (T change in changes)
            {
                if (idOf(change) != id) continue;
                var row = Rectangle.Intersect(RowRectangle(index), BodyBounds);
                if (!row.IsEmpty) Invalidate(row);
                break;
            }
        }
    }

    public DownloadGrid()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.Selectable, true);
        AutoScroll = true;
        TabStop = true;
        BackColor = Color.White;
        AccessibleName = "Downloads";
        AccessibleDescription = "Use the arrow keys to browse downloads. Space selects a record, Enter opens details, and Shift+F10 opens actions.";
        if (LicenseManager.UsageMode == LicenseUsageMode.Designtime)
            _items = DownloadItem.DesignerSamples(); // Visual Studio designer preview only.
        ApplyAppearance(VesperPalette.Light, Density.Compact);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            SetStyle(ControlStyles.ApplyThemingImplicitly, true);
            return base.CreateParams;
        }
    }

    internal IReadOnlyList<DownloadItem> VisibleItems => _items;
    internal IReadOnlyList<DownloadItem> CheckedItems => _items.Where(item => _checked.Contains(item.Id)).ToList();
    internal bool IsChecked(DownloadItem item) => _checked.Contains(item.Id);
    public int CheckedCount => _checked.Count;

    internal void SetItems(IReadOnlyList<DownloadItem> items)
    {
        _items = items;
        var ids = items.Select(item => item.Id).ToHashSet();
        bool changed = _checked.RemoveWhere(id => !ids.Contains(id)) > 0;
        _focusRow = Math.Clamp(_focusRow, 0, Math.Max(0, _items.Count - 1));
        UpdateScrollArea();
        Invalidate();
        if (changed) CheckStateChanged?.Invoke();
        LoadVisiblePosters();
    }

    internal bool Reveal(Guid id)
    {
        int index = -1;
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Id == id) { index = i; break; }
        if (index < 0) return false;

        _focusRow = index;
        AutoScrollPosition = new Point(0, index * _rowHeight);
        Focus();
        Invalidate();
        return true;
    }

    public void PruneChecks(IEnumerable<Guid> allowed)
    {
        _checked.IntersectWith(allowed);
        Invalidate();
        CheckStateChanged?.Invoke();
    }

    public void ClearChecks()
    {
        if (_checked.Count == 0) return;
        _checked.Clear();
        Invalidate();
        CheckStateChanged?.Invoke();
    }

    public void CheckAllVisible()
    {
        bool all = _items.Count > 0 && _items.Count == _checked.Count;
        foreach (var item in _items)
        {
            if (all) _checked.Remove(item.Id);
            else _checked.Add(item.Id);
        }
        Invalidate();
        CheckStateChanged?.Invoke();
    }

    private void Toggle(DownloadItem item)
    {
        if (!_checked.Add(item.Id)) _checked.Remove(item.Id);
        int index = _focusRow;
        if (index < 0 || index >= _items.Count || _items[index].Id != item.Id)
        {
            index = -1;
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Id == item.Id) { index = i; break; }
        }
        if (index >= 0) Invalidate(RowRectangle(index));
        Invalidate(new Rectangle(0, 0, _columnWidths[0], _headerHeight));
        CheckStateChanged?.Invoke();
    }

    internal void ApplyAppearance(VesperPalette palette, Density density)
    {
        // WinForms applies the native scrollbar theme when the handle is created.
        bool darkScrollbarChanged = IsHandleCreated && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            && (palette.IsDark && !palette.HighContrast) != (_palette.IsDark && !_palette.HighContrast);
        int scrollOffset = darkScrollbarChanged ? -AutoScrollPosition.Y : 0;
        _palette = palette;
        _density = density;
        BackColor = palette.Surface;
        if (density != Density.Gallery) ClearPosters();
        _headerHeight = UiKit.Scale(this, 37);
        _rowHeight = UiKit.Scale(this, density switch
        {
            Density.ExtraCompact => 34,
            Density.Comfortable => 58,
            Density.Gallery => 84,
            _ => 46
        });
        UpdateScrollArea();
        if (darkScrollbarChanged)
        {
            RecreateHandle();
            AutoScrollPosition = new Point(0, scrollOffset);
        }
        Invalidate();
        LoadVisiblePosters();
    }

    private void UpdateScrollArea()
    {
        // Width zero prevents the horizontal scrollbar at every density.
        AutoScrollMinSize = new Size(0, _headerHeight + _items.Count * _rowHeight);
        CalculateColumns();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        CalculateColumns();
        Invalidate();
        LoadVisiblePosters();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        CalculateColumns();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
        LoadVisiblePosters();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // ScrollableControl raises OnScroll when the offset really changes.
        base.OnMouseWheel(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        LoadVisiblePosters();
    }

    private async void LoadVisiblePosters()
    {
        if (_loadingPosters || !IsHandleCreated || !Visible || _density != Density.Gallery || SystemInformation.HighContrast)
            return;
        _loadingPosters = true;
        SecureHttpClient? http = null;
        try
        {
            var token = _posterLifetime.Token;
            while (!token.IsCancellationRequested && _density == Density.Gallery && Visible)
            {
                int first = Math.Clamp(-AutoScrollPosition.Y / Math.Max(1, _rowHeight), 0, _items.Count);
                int last = Math.Min(_items.Count, first + Math.Clamp(ClientSize.Height / Math.Max(1, _rowHeight) + 1, 1, 20));
                Uri? next = null;
                string? key = null;
                for (int i = first; i < last; i++)
                {
                    string? url = _items[i].Snapshot?.MediaThumbnailUrl;
                    if (url is null || _posters.ContainsKey(url) || !MediaPoster.TryUri(url, out var uri)) continue;
                    next = uri;
                    key = url;
                    break;
                }
                if (next is null || key is null) break;

                http ??= new SecureHttpClient(new ResolverOptions { MaxConcurrentRequests = 1 });
                Bitmap? image = await MediaPoster.LoadAsync(http, next, 220, 124, token);
                if (token.IsCancellationRequested || _density != Density.Gallery) { image?.Dispose(); break; }
                if (_posters.Count == 32)
                {
                    string oldest = _posters.Keys.First();
                    _posters.Remove(oldest, out var retired);
                    retired?.Dispose();
                }
                _posters.Add(key, image);
                if (image is not null) Invalidate();
            }
        }
        finally { http?.Dispose(); _loadingPosters = false; }
    }

    private void ClearPosters()
    {
        foreach (var bitmap in _posters.Values) bitmap?.Dispose();
        _posters.Clear();
    }

    private void CalculateColumns()
    {
        // ClientSize already excludes the native scrollbar. Subtracting it again
        // left a dead strip beside the rightmost action buttons.
        int width = Math.Max(0, ClientSize.Width);
        if (width == 0) return;
        int select = UiKit.Scale(this, 48);
        int actions = UiKit.Scale(this, 44);
        int size = UiKit.Scale(this, 89);
        int activity = Math.Clamp((int)(width * .235), UiKit.Scale(this, 158), UiKit.Scale(this, 258));
        int transfer = Math.Clamp((int)(width * .165), UiKit.Scale(this, 122), UiKit.Scale(this, 225));
        int name = width - select - actions - size - activity - transfer;
        if (name < UiKit.Scale(this, 190))
        {
            int deficit = UiKit.Scale(this, 190) - name;
            transfer = Math.Max(UiKit.Scale(this, 80), transfer - deficit);
            name = width - select - actions - size - activity - transfer;
        }
        _columnWidths[0] = select;
        _columnWidths[1] = name;
        _columnWidths[2] = activity;
        _columnWidths[3] = transfer;
        _columnWidths[4] = size;
        _columnWidths[5] = actions;
        _contentWidth = width;
    }

    private Rectangle Cell(int column, int y, int height)
    {
        int x = 0;
        for (int i = 0; i < column; i++) x += _columnWidths[i];
        return new Rectangle(x, y, _columnWidths[column], height);
    }

    private Rectangle RowRectangle(int row)
        => new(0, _headerHeight + AutoScrollPosition.Y + row * _rowHeight,
            Math.Max(1, _contentWidth), _rowHeight);

    private Rectangle BodyBounds => new(0, _headerHeight, _contentWidth, Math.Max(0, ClientSize.Height - _headerHeight));

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // OnPaint fills the damaged area and draws the intersecting rows.
        // Do not erase it separately before every hover/scroll repaint.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        using (var background = new SolidBrush(_palette.Surface))
            g.FillRectangle(background, e.ClipRectangle);
        int scrolled = -AutoScrollPosition.Y;
        var dirtyRows = Rectangle.Intersect(e.ClipRectangle, BodyBounds);
        if (dirtyRows.Height > 0 && _items.Count > 0)
        {
            int rowHeight = Math.Max(1, _rowHeight);
            int first = Math.Clamp((scrolled + dirtyRows.Top - _headerHeight) / rowHeight, 0, _items.Count);
            int last = Math.Min(_items.Count, (scrolled + dirtyRows.Bottom - _headerHeight + rowHeight - 1) / rowHeight);
            var original = g.Save();
            g.SetClip(dirtyRows, CombineMode.Intersect);
            for (int i = first; i < last; i++) PaintRow(g, i);
            g.Restore(original);
        }
        if (e.ClipRectangle.Top < _headerHeight) PaintHeader(g);
        if (_items.Count == 0)
        {
            var text = new Rectangle(20, _headerHeight + Math.Max(40, (Height - _headerHeight) / 3), Width - 40, 50);
            if (EmptyIllustration is { } illustration && EmptyTitle == "No downloads yet")
            {
                int area = ClientSize.Height - _headerHeight;
                int width = Math.Min(UiKit.Scale(this, 345), Math.Min(Width / 2, Math.Max(1, area - 64)));
                int height = width * illustration.Height / illustration.Width;
                int top = _headerHeight + Math.Max(4, (area - height - UiKit.Scale(this, 70)) / 2);
                g.DrawImage(illustration, (Width - width) / 2, top, width, height);
                text.Y = top + height;
            }
            Draw(g, EmptyTitle, EmptyIllustration is not null && EmptyTitle == "No downloads yet" ? _emptyFont : _nameFont, _palette.Text, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (EmptyHint.Length > 0)
                Draw(g, EmptyHint, _subFont, _palette.Muted, new Rectangle(text.X, text.Bottom - 6, text.Width, 40),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
        }
    }

    private void PaintHeader(Graphics g)
    {
        using (var fill = new SolidBrush(_palette.Secondary))
            g.FillRectangle(fill, 0, 0, _contentWidth, _headerHeight);
        using (var pen = new Pen(_palette.Divider))
            g.DrawLine(pen, 0, _headerHeight - 1, _contentWidth, _headerHeight - 1);
        for (int col = 0; col < 6; col++)
        {
            Rectangle rect = Cell(col, 0, _headerHeight);
            if (col == 0)
            {
                PaintCheck(g, Center(rect, UiKit.Scale(this, 16)),
                    _items.Count > 0 && _items.Count == _checked.Count);
            }
            else if (col != 5)
            {
                (string heading, VesperIcon icon) = col switch
                {
                    1 => ("Name", VesperIcon.File),
                    2 => ("Activity", VesperIcon.Activity),
                    3 => ("Transfer", VesperIcon.Gauge),
                    _ => ("Size", VesperIcon.Storage)
                };
                int x = rect.X + UiKit.Scale(this, 8);
                if (_palette.Botanical)
                {
                    int size = UiKit.Scale(this, 15);
                    VesperDrawing.Icon(g, icon, new Rectangle(x, (_headerHeight - size) / 2, size, size),
                        VesperDrawing.Ink(icon, _palette));
                    x += size + UiKit.Scale(this, 7);
                }
                Draw(g, heading, _headerFont, _palette.Muted,
                    new Rectangle(x, 0, Math.Max(1, rect.Right - x), _headerHeight),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
    }

    private void PaintRow(Graphics g, int index)
    {
        DownloadItem item = _items[index];
        Rectangle row = RowRectangle(index);
        bool isChecked = _checked.Contains(item.Id);
        Color color = isChecked && !_palette.HighContrast
            ? _palette.Selection
            : index == _hoverRow ? _palette.Secondary : _palette.Surface;
        using (var brush = new SolidBrush(color)) g.FillRectangle(brush, row);
        using (var pen = new Pen(_palette.Divider))
            g.DrawLine(pen, row.Left + UiKit.Scale(this, 12), row.Bottom - 1, row.Right - UiKit.Scale(this, 12), row.Bottom - 1);

        int pad = UiKit.Scale(this, 8);
        PaintCheck(g, Center(Cell(0, row.Y, row.Height), UiKit.Scale(this, 16)), isChecked);
        PaintName(g, item, Cell(1, row.Y, row.Height), pad);
        PaintActivity(g, item, Cell(2, row.Y, row.Height), pad);
        PaintTransfer(g, item, Cell(3, row.Y, row.Height), pad);
        var size = Cell(4, row.Y, row.Height);
        Draw(g, item.Size, _textFont, _palette.Text, new Rectangle(size.Left, size.Top, size.Width - pad, size.Height),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        Rectangle menuCell = Cell(5, row.Y, row.Height);
        Rectangle dots = Center(menuCell, UiKit.Scale(this, 25));
        if (_hoverRow == index && _hoverColumn == 5)
        {
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var rounded = VesperDrawing.Rounded(dots, 5);
            using var hover = new SolidBrush(_palette.Secondary);
            using var edge = new Pen(_palette.Border);
            g.FillPath(hover, rounded);
            g.DrawPath(edge, rounded);
            g.Restore(state);
        }
        // Larger dots than the toolbar/menu glyph: legible at compact density.
        var ink = _palette.Muted;
        using (var brush = new SolidBrush(ink))
        {
            int radius = Math.Max(1, UiKit.Scale(this, 2));
            int gap = UiKit.Scale(this, 6);
            int centerX = dots.Left + dots.Width / 2;
            int centerY = dots.Top + dots.Height / 2;
            for (int delta = -1; delta <= 1; delta++)
                g.FillEllipse(brush, centerX - radius, centerY + delta * gap - radius, radius * 2, radius * 2);
        }
        if (Focused && ShowFocusCues && index == _focusRow)
            VesperDrawing.FocusOutline(g, Rectangle.Inflate(row, -3, -2), _palette.Text);
    }

    private void PaintName(Graphics g, DownloadItem item, Rectangle rect, int pad)
    {
        int iconSize = UiKit.Scale(this, _density == Density.ExtraCompact ? 24 : 31);
        var icon = new Rectangle(rect.Left + pad, rect.Top + (rect.Height - iconSize) / 2, iconSize, iconSize);
        var glyph = item.FileType switch
        {
            "Audio" => VesperIcon.Audio, "Video" => VesperIcon.Video,
            "Disc image" => VesperIcon.Disc, "Archive" => VesperIcon.Archive,
            "eBook" => VesperIcon.Book, "Document" => VesperIcon.Document,
            "Media" => VesperIcon.Layers, _ => VesperIcon.File
        };
        int x = icon.Right + UiKit.Scale(this, 10);
        if (_density == Density.Gallery && rect.Width >= UiKit.Scale(this, 200)
            && item.Snapshot?.MediaThumbnailUrl is { } url && _posters.TryGetValue(url, out var poster)
            && poster is not null)
        {
            var frame = new Rectangle(icon.Left, rect.Top + (rect.Height - UiKit.Scale(this, 54)) / 2,
                UiKit.Scale(this, 96), UiKit.Scale(this, 54));
            double scale = Math.Min(frame.Width / (double)poster.Width, frame.Height / (double)poster.Height);
            int imageWidth = Math.Max(1, (int)(poster.Width * scale));
            int imageHeight = Math.Max(1, (int)(poster.Height * scale));
            g.DrawImage(poster, new Rectangle(frame.X + (frame.Width - imageWidth) / 2,
                frame.Y + (frame.Height - imageHeight) / 2, imageWidth, imageHeight));
            x = frame.Right + UiKit.Scale(this, 10);
        }
        else
        {
            Color ink = item.Status == JobStatus.Missing ? _palette.Muted : VesperDrawing.Ink(glyph, _palette);
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var rounded = VesperDrawing.Rounded(icon, UiKit.Scale(this, 5)))
            using (var brush = new SolidBrush(_palette.HighContrast ? _palette.Surface : VesperDrawing.Mix(_palette.Surface, ink, 0.10f)))
                g.FillPath(brush, rounded);
            g.Restore(state);
            VesperDrawing.Icon(g, glyph, Center(icon, UiKit.Scale(this, _density == Density.ExtraCompact ? 17 : 20)), ink);
        }
        int nameY = _density == Density.ExtraCompact ? rect.Y : rect.Y + UiKit.Scale(this, _density == Density.Gallery ? 20 : 2);
        int nameH = _density == Density.ExtraCompact ? rect.Height : UiKit.Scale(this, 22);
        var bounds = new Rectangle(x, nameY, Math.Max(1, rect.Right - x - pad), nameH);
        Draw(g, item.Name, _nameFont, item.Status == JobStatus.Missing ? _palette.Muted : _palette.Text,
            bounds, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        if (item.Status == JobStatus.Missing)
        {
            int measured = TextRenderer.MeasureText(g, item.Name, _nameFont, new Size(1000, nameH),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            using var line = new Pen(_palette.Muted);
            g.DrawLine(line, x, bounds.Top + nameH / 2, Math.Min(bounds.Right, x + measured), bounds.Top + nameH / 2);
        }
        if (_density != Density.ExtraCompact)
        {
            string caption = item.FileType + " · " + item.Extension;
            if (_density == Density.Gallery && item.Snapshot is { MediaProvider.Length: > 0 } media)
                caption = media.MediaProvider + " · " + item.Extension
                    + (string.IsNullOrWhiteSpace(media.MediaQuality) ? "" : " · " + MediaTransferBridge.DisplayQuality(media.MediaQuality));
            Draw(g, caption, _subFont, _palette.Muted,
                new Rectangle(x, nameY + nameH - UiKit.Scale(this, 2), bounds.Width, UiKit.Scale(this, 18)),
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        }
    }

    private void PaintActivity(Graphics g, DownloadItem item, Rectangle rect, int pad)
    {
        var (label, icon, color) = item.Status switch
        {
            JobStatus.Downloading when item.Snapshot?.IsLiveRecording == true => ("Recording", VesperIcon.Download, _palette.MutedGreen),
            JobStatus.Completed when item.Snapshot?.Reason == "live-gaps" => ("Completed with gaps", VesperIcon.Warning, _palette.ProgressAmber),
            JobStatus.Downloading => (item.BrowserFile is null ? "Downloading" : "In Chrome", VesperIcon.Download, _palette.MutedGreen),
            JobStatus.Paused => ("Paused", VesperIcon.Pause, _palette.ProgressAmber),
            JobStatus.Queued => ("Queued", VesperIcon.Layers, _palette.Muted),
            JobStatus.Completed => ("Completed", VesperIcon.Check, _palette.MutedGreen),
            JobStatus.Missing => ("File missing", VesperIcon.Warning, _palette.ProgressAmber),
            JobStatus.NeedsLink => ("Needs link", VesperIcon.Warning, _palette.ProgressAmber),
            JobStatus.Waiting => ("Waiting for network", VesperIcon.Info, _palette.Muted),
            JobStatus.Verifying => ("Checking", VesperIcon.Shield, _palette.MutedGreen),
            JobStatus.Resolving => ("Resolving", VesperIcon.Info, _palette.Muted),
            JobStatus.WaitingRetry => ("Waiting to retry", VesperIcon.Clock, _palette.ProgressAmber),
            JobStatus.NeedsAttention => ("Needs attention", VesperIcon.Warning, _palette.ProgressAmber),
            JobStatus.Processing => ("Preparing file", VesperIcon.Info, _palette.MutedGreen),
            JobStatus.Converting => ("Converting", VesperIcon.Refresh, _palette.MutedGreen),
            JobStatus.Failed => ("Failed", VesperIcon.Warning, _palette.ProgressRed),
            JobStatus.Cancelled => ("Cancelled", VesperIcon.Info, _palette.Muted),
            _ => ("Suspect integrity", VesperIcon.Warning, _palette.ProgressRed)
        };
        if (_palette.HighContrast) color = _palette.Text;
        bool progress = item.Percent.HasValue && item.Status is JobStatus.Downloading or JobStatus.Paused or JobStatus.NeedsLink
            or JobStatus.NeedsAttention or JobStatus.WaitingRetry or JobStatus.Waiting or JobStatus.Failed;
        int iconSize = UiKit.Scale(this, 14);
        int statusHeight = UiKit.Scale(this, _density == Density.ExtraCompact ? 18 : 21);
        int statusY = progress ? rect.Y + UiKit.Scale(this, _density == Density.ExtraCompact ? 2 : 4)
                               : rect.Y + (rect.Height - statusHeight) / 2;
        VesperDrawing.Icon(g, icon, new Rectangle(rect.Left + pad, statusY + (statusHeight - iconSize) / 2, iconSize, iconSize), color);
        int labelX = rect.Left + pad + iconSize + UiKit.Scale(this, 5);
        int percentArea = progress ? UiKit.Scale(this, _density == Density.ExtraCompact ? 35 : 75) : 0;
        Draw(g, label, _textFont, color,
            new Rectangle(labelX, statusY, Math.Max(1, rect.Right - labelX - pad - percentArea), statusHeight),
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        if (!progress) return;
        int percent = Math.Clamp(item.Percent!.Value, 0, 100);
        string percentText = _density == Density.ExtraCompact ? $"{percent}%" : item.Status switch
        {
            JobStatus.Paused => $"{percent}% saved",
            JobStatus.NeedsLink or JobStatus.NeedsAttention or JobStatus.Failed => $"{percent}% retained",
            _ => $"{percent}%"
        };
        Draw(g, percentText, _subFont, _palette.Muted,
            new Rectangle(rect.Right - pad - percentArea, statusY, percentArea, statusHeight),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine);
        int barHeight = UiKit.Scale(this, _density == Density.ExtraCompact ? 2 : 4);
        int barTop = statusY + statusHeight + (_density == Density.ExtraCompact ? 0 : UiKit.Scale(this, 1));
        var bar = new Rectangle(rect.Left + pad, barTop, Math.Max(1, rect.Width - pad * 2), barHeight);
        using (var track = new SolidBrush(_palette.ProgressTrack)) g.FillRectangle(track, bar);
        Color ink = item.Status switch
        {
            JobStatus.Downloading => _palette.ProgressGreen,
            JobStatus.Failed => _palette.ProgressRed,
            _ => _palette.ProgressAmber
        };
        int fillWidth = bar.Width * percent / 100;
        using (var fill = new SolidBrush(ink)) g.FillRectangle(fill, bar.X, bar.Y, fillWidth, bar.Height);
        if (item.Status == JobStatus.Downloading && _palette.IsDark && !_palette.HighContrast && fillWidth > 0 && bar.Height > 1)
        {
            using var edge = new SolidBrush(VesperDrawing.Mix(ink, Color.White, 0.26f));
            g.FillRectangle(edge, bar.X, bar.Y, fillWidth, 1);
        }
    }

    private void PaintTransfer(Graphics g, DownloadItem item, Rectangle rect, int pad)
    {
        if (string.IsNullOrWhiteSpace(item.Transfer)) return;
        var content = new Rectangle(rect.Left + pad, rect.Top, Math.Max(1, rect.Width - pad * 2), rect.Height);
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        if (string.IsNullOrWhiteSpace(item.Eta))
        {
            Draw(g, item.Transfer, _textFont, _palette.Text, content, flags);
            return;
        }
        if (_density == Density.ExtraCompact)
        {
            // A short ETA is much more useful than dropping the time entirely.
            string shortEta = item.Eta.Replace(" min left", "m", StringComparison.OrdinalIgnoreCase)
                .Replace(" minutes left", "m", StringComparison.OrdinalIgnoreCase)
                .Replace(" left", "", StringComparison.OrdinalIgnoreCase);
            string combined = item.Transfer + "  ·  " + shortEta;
            int required = TextRenderer.MeasureText(g, combined, _subFont, new Size(1000, rect.Height),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            Draw(g, required <= content.Width ? combined : item.Transfer, _subFont, _palette.Text, content, flags);
            return;
        }
        int top = rect.Top + Math.Max(1, (rect.Height - UiKit.Scale(this, 36)) / 2);
        Draw(g, item.Transfer, _textFont, _palette.Text,
            new Rectangle(content.Left, top, content.Width, UiKit.Scale(this, 18)), flags);
        Draw(g, item.Eta, _subFont, _palette.Muted,
            new Rectangle(content.Left, top + UiKit.Scale(this, 18), content.Width, UiKit.Scale(this, 18)), flags);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_filePress is { } press)
        {
            if (e.Button != MouseButtons.Left) _filePress = null;
            else if (!press.Threshold.Contains(e.Location))
            {
                _filePress = null;
                if (CreateFileDrop(press.Item) is { } data) DoDragDrop(data, DragDropEffects.Copy);
                return;
            }
        }
        var (row, column) = Hit(e.Location);
        if (row != _hoverRow)
        {
            int old = _hoverRow;
            _hoverRow = row;
            if (old >= 0) Invalidate(RowRectangle(old));
            if (row >= 0) Invalidate(RowRectangle(row));
        }
        bool link = row >= 0 && (column is 5 or 0 || column == 1 && e.X < Cell(1, 0, 0).Left + UiKit.Scale(this, 48));
        Cursor desiredCursor = link ? Cursors.Hand : Cursors.Default;
        if (Cursor != desiredCursor) Cursor = desiredCursor;
        if (row == _hoverRow && column != _hoverColumn && row >= 0)
            Invalidate(Cell(5, RowRectangle(row).Top, _rowHeight));
        _hoverColumn = column;
        string tip = row >= 0 ? column switch
        {
            0 => "Select download",
            1 when CanDragFile(_items[row]) => link ? "Click for details or drag the file" : "Drag the file to another app",
            1 when link => "Open download details",
            5 => "More actions",
            _ => ""
        } : "";
        if (tip != _tipText) { _tipText = tip; _tips.SetToolTip(this, tip); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        int old = _hoverRow;
        _hoverRow = -1;
        _hoverColumn = -1;
        Cursor = Cursors.Default;
        if (old >= 0) Invalidate(RowRectangle(old));
        if (_tipText.Length > 0) { _tipText = ""; _tips.SetToolTip(this, ""); }
    }

    private (int row, int col) Hit(Point point)
    {
        if (point.Y < _headerHeight)
            return (-1, point.X >= 0 && point.X < _columnWidths[0] ? 0 : -1);
        int row = (point.Y - _headerHeight - AutoScrollPosition.Y) / Math.Max(1, _rowHeight);
        if (row < 0 || row >= _items.Count) return (-1, -1);
        for (int column = 0, x = 0; column < _columnWidths.Length; column++)
        {
            x += _columnWidths[column];
            if (point.X < x) return (row, column);
        }
        return (-1, -1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _filePress = null;
        var (row, col) = Hit(e.Location);
        if (e.Button == MouseButtons.Left) Focus();
        if (row < 0)
        {
            if (col == 0 && e.Button == MouseButtons.Left) CheckAllVisible();
            return;
        }
        int previous = _focusRow;
        _focusRow = row;
        if (previous != row)
        {
            Invalidate(RowRectangle(previous));
            Invalidate(RowRectangle(row));
        }
        DownloadItem item = _items[row];
        if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Left && col == 5)
            MenuRequested?.Invoke(item, PointToScreen(e.Location));
        else if (e.Button == MouseButtons.Left && col == 0) Toggle(item);
        else if (e.Button == MouseButtons.Left && col == 1)
        {
            bool details = e.X < Cell(1, 0, 0).Left + UiKit.Scale(this, 48);
            if (CanDragFile(item))
            {
                Size drag = SystemInformation.DragSize;
                _filePress = (item, new Rectangle(e.X - drag.Width / 2, e.Y - drag.Height / 2, drag.Width, drag.Height), details);
            }
            else if (details) DetailsRequested?.Invoke(item);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var press = _filePress;
        _filePress = null;
        if (e.Button == MouseButtons.Left && press is { Details: true } file && file.Threshold.Contains(e.Location)
            && ClientRectangle.Contains(e.Location) && Hit(e.Location) is (>= 0, 1) hit && _items[hit.row] == file.Item)
            DetailsRequested?.Invoke(file.Item);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) _filePress = null;
    }

    private static bool CanDragFile(DownloadItem item) =>
        item.Snapshot is { State: JobState.Completed, FileMissing: false, FinalPath.Length: > 0 };

    private static DataObject? CreateFileDrop(DownloadItem item)
    {
        if (!CanDragFile(item) || item.Snapshot?.FinalPath is not { } path) return null;
        FileCheck check = PathSafety.Inspect(path);
        if (!check.Exists || check.IsDirectory) return null;

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, autoConvert: false, new[] { path });
        return data;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        _filePress = null;
        var (row, column) = Hit(e.Location);
        // The owner revalidates the path and only opens a genuinely completed, present file.
        if (e.Button == MouseButtons.Left && row >= 0 && column is not (0 or 5)) OpenRequested?.Invoke(_items[row]);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) switch
    {
        Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown => true,
        _ => base.IsInputKey(keyData)
    };

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(RowRectangle(_focusRow)); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(RowRectangle(_focusRow)); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_items.Count > 0)
        {
            int old = _focusRow;
            int page = Math.Max(1, (ClientSize.Height - _headerHeight) / Math.Max(1, _rowHeight));
            _focusRow = e.KeyCode switch
            {
                Keys.Up => Math.Max(0, old - 1), Keys.Down => Math.Min(_items.Count - 1, old + 1),
                Keys.PageUp => Math.Max(0, old - page), Keys.PageDown => Math.Min(_items.Count - 1, old + page),
                Keys.Home => 0, Keys.End => _items.Count - 1, _ => old
            };
            if (_focusRow != old)
            {
                int desired = _focusRow * _rowHeight;
                int scroll = -AutoScrollPosition.Y;
                if (desired < scroll) AutoScrollPosition = new Point(0, desired);
                else if (desired + _rowHeight > scroll + ClientSize.Height - _headerHeight)
                    AutoScrollPosition = new Point(0, desired + _rowHeight - (ClientSize.Height - _headerHeight));
                if (scroll == -AutoScrollPosition.Y)
                {
                    Invalidate(RowRectangle(old));
                    Invalidate(RowRectangle(_focusRow));
                }
                else Invalidate();
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Space) { Toggle(_items[_focusRow]); e.Handled = true; e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Enter) { DetailsRequested?.Invoke(_items[_focusRow]); e.Handled = true; e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))
            {
                MenuRequested?.Invoke(_items[_focusRow], PointToScreen(RowRectangle(_focusRow).Location));
                e.Handled = true; e.SuppressKeyPress = true; return;
            }
        }
        base.OnKeyDown(e);
    }

    private static Rectangle Center(Rectangle rect, int size)
        => new(rect.X + (rect.Width - size) / 2, rect.Y + (rect.Height - size) / 2, size, size);

    private void PaintCheck(Graphics g, Rectangle rect, bool selected)
    {
        var original = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = VesperDrawing.Rounded(new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1), 3);
        using (var fill = new SolidBrush(selected ? _palette.Green : _palette.Surface)) g.FillPath(fill, path);
        using (var border = new Pen(selected ? _palette.Green : _palette.MutedGreen)) g.DrawPath(border, path);
        if (selected) VesperDrawing.Icon(g, VesperIcon.Check, Center(rect, Math.Max(8, rect.Width - 5)), _palette.ActionInk);
        g.Restore(original);
    }

    private static void Draw(Graphics g, string text, Font font, Color color, Rectangle rect, TextFormatFlags flags)
    {
        if (rect.Width > 0 && rect.Height > 0 && text.Length > 0)
            TextRenderer.DrawText(g, text, font, rect, color,
                flags | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _posterLifetime.Cancel();
            _posterLifetime.Dispose();
            ClearPosters();
            _emptyFont.Dispose();
            _tips.Dispose();
            _textFont.Dispose(); _headerFont.Dispose(); _nameFont.Dispose(); _subFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
