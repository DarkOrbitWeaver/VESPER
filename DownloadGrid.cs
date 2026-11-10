using System.ComponentModel;
using System.Drawing.Drawing2D;

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
    private readonly int[] _columnWidths = new int[6];
    private int _contentWidth;

    public event Action? CheckStateChanged;
    internal event Action<DownloadItem>? DetailsRequested;
    internal event Action<DownloadItem, Point>? MenuRequested;

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
            _items = DownloadItem.Examples(); // Visual Studio preview data only.
        ApplyAppearance(VesperPalette.Light, Density.Compact);
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
        bool all = _items.Count > 0 && _items.All(item => _checked.Contains(item.Id));
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
        CheckStateChanged?.Invoke();
    }

    internal void ApplyAppearance(VesperPalette palette, Density density)
    {
        _palette = palette;
        _density = density;
        BackColor = palette.Surface;
        _headerHeight = UiKit.Scale(this, 37);
        _rowHeight = UiKit.Scale(this, density switch
        {
            Density.ExtraCompact => 34,
            Density.Comfortable => 58,
            _ => 46
        });
        UpdateScrollArea();
        Invalidate();
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
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // ScrollableControl raises OnScroll when the offset really changes.
        base.OnMouseWheel(e);
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

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // OnPaint clears the buffered surface and draws the visible rows.
        // Do not erase it separately before every hover/scroll repaint.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.Clear(_palette.Surface);
        CalculateColumns();
        int scrolled = -AutoScrollPosition.Y;
        int first = Math.Clamp(scrolled / Math.Max(1, _rowHeight), 0, _items.Count);
        int visible = (ClientSize.Height - _headerHeight) / Math.Max(1, _rowHeight) + 3;
        int last = Math.Min(_items.Count, first + Math.Max(0, visible));
        var original = g.Save();
        g.SetClip(new Rectangle(0, _headerHeight, _contentWidth, Math.Max(0, ClientSize.Height - _headerHeight)));
        for (int i = first; i < last; i++) PaintRow(g, i);
        g.Restore(original);
        PaintHeader(g);
        if (_items.Count == 0)
        {
            var text = new Rectangle(20, _headerHeight + Math.Max(40, (Height - _headerHeight) / 3), Width - 40, 50);
            Draw(g, "No matching downloads", _nameFont, _palette.Muted, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    private void PaintHeader(Graphics g)
    {
        using (var fill = new SolidBrush(_palette.Secondary))
            g.FillRectangle(fill, 0, 0, _contentWidth, _headerHeight);
        using (var pen = new Pen(_palette.Border))
            g.DrawLine(pen, 0, _headerHeight - 1, _contentWidth, _headerHeight - 1);
        for (int col = 0; col < 6; col++)
        {
            Rectangle rect = Cell(col, 0, _headerHeight);
            if (col == 0)
            {
                PaintCheck(g, Center(rect, UiKit.Scale(this, 16)),
                    _items.Count > 0 && _items.All(item => _checked.Contains(item.Id)));
            }
            else if (col != 5)
            {
                string heading = col switch { 1 => "Name", 2 => "Activity", 3 => "Transfer", 4 => "Size", _ => "" };
                Draw(g, heading, _headerFont, _palette.Muted,
                    new Rectangle(rect.X + UiKit.Scale(this, 8), 0, rect.Width - UiKit.Scale(this, 16), _headerHeight),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
    }

    private void PaintRow(Graphics g, int index)
    {
        DownloadItem item = _items[index];
        Rectangle row = RowRectangle(index);
        Color color = _checked.Contains(item.Id)
            ? (_palette.Surface.R > 150 ? Color.FromArgb(233, 242, 230) : Color.FromArgb(41, 68, 49))
            : index == _hoverRow ? _palette.Secondary : _palette.Surface;
        using (var brush = new SolidBrush(color)) g.FillRectangle(brush, row);
        using (var pen = new Pen(_palette.Surface.R > 150
            ? Color.FromArgb(232, 236, 230) : Color.FromArgb(52, 63, 55)))
            g.DrawLine(pen, row.Left + UiKit.Scale(this, 12), row.Bottom - 1, row.Right - UiKit.Scale(this, 12), row.Bottom - 1);

        int pad = UiKit.Scale(this, 8);
        PaintCheck(g, Center(Cell(0, row.Y, row.Height), UiKit.Scale(this, 16)), _checked.Contains(item.Id));
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
    }

    private void PaintName(Graphics g, DownloadItem item, Rectangle rect, int pad)
    {
        int iconSize = UiKit.Scale(this, _density == Density.ExtraCompact ? 24 : 31);
        var icon = new Rectangle(rect.Left + pad, rect.Top + (rect.Height - iconSize) / 2, iconSize, iconSize);
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var rounded = VesperDrawing.Rounded(new Rectangle(icon.X, icon.Y, icon.Width - 1, icon.Height - 1), 4))
        {
            using var brush = new SolidBrush(_palette.Secondary);
            using var edge = new Pen(_palette.Border);
            g.FillPath(brush, rounded);
            g.DrawPath(edge, rounded);
        }
        g.Restore(state);
        var glyph = item.FileType switch
        {
            "Audio" => VesperIcon.Audio, "Video" => VesperIcon.Video,
            "Disc image" => VesperIcon.Disc, "Archive" => VesperIcon.Archive,
            "eBook" => VesperIcon.Book, _ => VesperIcon.File
        };
        VesperDrawing.Icon(g, glyph, Center(icon, UiKit.Scale(this, _density == Density.ExtraCompact ? 17 : 20)),
            item.Status == JobStatus.Missing ? _palette.Muted : _palette.MutedGreen);
        int x = icon.Right + UiKit.Scale(this, 10);
        int nameY = _density == Density.ExtraCompact ? rect.Y : rect.Y + UiKit.Scale(this, 2);
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
            Draw(g, item.FileType + " · " + item.Extension, _subFont, _palette.Muted,
                new Rectangle(x, nameY + nameH - UiKit.Scale(this, 2), bounds.Width, UiKit.Scale(this, 18)),
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
    }

    private void PaintActivity(Graphics g, DownloadItem item, Rectangle rect, int pad)
    {
        var (label, icon, color) = item.Status switch
        {
            JobStatus.Downloading => ("Downloading", VesperIcon.Download, _palette.Green),
            JobStatus.Paused => ("Paused", VesperIcon.Pause, _palette.Amber),
            JobStatus.Queued => ("Queued", VesperIcon.Info, _palette.Muted),
            JobStatus.Completed => ("Completed", VesperIcon.Check, _palette.Green),
            JobStatus.Missing => ("File missing", VesperIcon.Warning, _palette.Amber),
            JobStatus.NeedsLink => ("Needs link", VesperIcon.Warning, _palette.Red),
            JobStatus.Waiting => ("Waiting for network", VesperIcon.Info, _palette.Muted),
            JobStatus.Verifying => ("Verifying", VesperIcon.Info, _palette.Green),
            _ => ("Suspect", VesperIcon.Warning, _palette.Red)
        };
        bool progress = item.Percent.HasValue && item.Status is JobStatus.Downloading or JobStatus.Paused or JobStatus.NeedsLink;
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
            JobStatus.NeedsLink => $"{percent}% retained",
            _ => $"{percent}%"
        };
        Draw(g, percentText, _subFont, _palette.Muted,
            new Rectangle(rect.Right - pad - percentArea, statusY, percentArea, statusHeight),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine);
        int barHeight = UiKit.Scale(this, _density == Density.ExtraCompact ? 2 : 4);
        int barTop = statusY + statusHeight + (_density == Density.ExtraCompact ? 0 : UiKit.Scale(this, 1));
        var bar = new Rectangle(rect.Left + pad, barTop, Math.Max(1, rect.Width - pad * 2), barHeight);
        using (var track = new SolidBrush(_palette.Border)) g.FillRectangle(track, bar);
        using (var fill = new SolidBrush(item.Status == JobStatus.Paused ? _palette.Amber
            : item.Status == JobStatus.NeedsLink ? _palette.MutedGreen : _palette.Green))
            g.FillRectangle(fill, bar.X, bar.Y, (int)(bar.Width * percent / 100d), bar.Height);
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
        var (row, col) = Hit(e.Location);
        if (e.Button == MouseButtons.Left) Focus();
        if (row < 0)
        {
            if (col == 0 && e.Button == MouseButtons.Left) CheckAllVisible();
            return;
        }
        _focusRow = row;
        DownloadItem item = _items[row];
        if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Left && col == 5)
            MenuRequested?.Invoke(item, PointToScreen(e.Location));
        else if (e.Button == MouseButtons.Left && col == 0) Toggle(item);
        else if (e.Button == MouseButtons.Left && col == 1 && e.X < Cell(1, 0, 0).Left + UiKit.Scale(this, 48))
            DetailsRequested?.Invoke(item);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) switch
    {
        Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown => true,
        _ => base.IsInputKey(keyData)
    };

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
        if (selected) VesperDrawing.Icon(g, VesperIcon.Check, Center(rect, Math.Max(8, rect.Width - 5)), Color.White);
        g.Restore(original);
    }

    private static void Draw(Graphics g, string text, Font font, Color color, Rectangle rect, TextFormatFlags flags)
    {
        if (rect.Width > 0 && rect.Height > 0 && text.Length > 0)
            TextRenderer.DrawText(g, text, font, rect, color, flags | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
            _textFont.Dispose(); _headerFont.Dispose(); _nameFont.Dispose(); _subFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
