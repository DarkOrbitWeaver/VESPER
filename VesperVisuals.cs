using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace VESPER;

// All icons are drawn at the requested size. Font glyphs were not reliable at Windows text scaling.
public enum VesperIcon
{
    None, Plus, Gear, Search, More, Audio, Video, Disc, Archive, Book, File, Check, Warning, Info, Link, Pause,
    Folder, Download, Queue, Clipboard, Close, ChevronDown, Play, Pencil, Trash, Gauge, Refresh, Clock, RemoveList,
    Storage, Quality, Provider, Crown, Leaf, Layers, Sort, Document, Activity, Shield
}

internal static class VesperDrawing
{
    internal const TextFormatFlags PaintTextFlags = TextFormatFlags.PreserveGraphicsClipping |
        TextFormatFlags.PreserveGraphicsTranslateTransform;

    internal static void FocusOutline(Graphics graphics, Rectangle bounds, Color ink)
    {
        if (bounds.Width < 2 || bounds.Height < 2) return;
        using var pen = new Pen(ink) { DashStyle = DashStyle.Dot };
        graphics.DrawRectangle(pen, bounds);
    }

    internal static void CheckBox(Graphics graphics, Rectangle bounds, VesperPalette palette, bool isChecked, bool enabled)
    {
        Color fill = isChecked && enabled ? palette.Green : enabled ? palette.Surface : palette.Secondary;
        using var face = Rounded(Rectangle.Inflate(bounds, -1, -1), Math.Max(1, bounds.Height / 4));
        using var brush = new SolidBrush(fill);
        using var border = new Pen(enabled ? palette.MutedGreen : palette.Border);
        graphics.FillPath(brush, face);
        graphics.DrawPath(border, face);
        if (isChecked)
            Icon(graphics, VesperIcon.Check, Rectangle.Inflate(bounds, -1, -1), enabled ? palette.ActionInk : palette.Muted);
    }

    internal static Color Mix(Color from, Color to, float amount) => Color.FromArgb(
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

    internal static Color Ink(VesperIcon icon, VesperPalette palette)
    {
        if (palette.HighContrast) return palette.Text;
        return icon switch
        {
            VesperIcon.Trash or VesperIcon.Warning => palette.Red,
            VesperIcon.Crown or VesperIcon.Archive or VesperIcon.Pause or VesperIcon.Clock or VesperIcon.Folder => palette.Amber,
            VesperIcon.Link or VesperIcon.Quality or VesperIcon.Provider or VesperIcon.Document or VesperIcon.Info => palette.Link,
            VesperIcon.Audio => palette.IsDark ? Color.FromArgb(200, 175, 222) : Color.FromArgb(117, 77, 141),
            VesperIcon.None or VesperIcon.Close or VesperIcon.File or VesperIcon.More => palette.Muted,
            _ => palette.MutedGreen
        };
    }

    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) return p;
        int d = Math.Min(Math.Min(r.Width, r.Height), radius * 2);
        if (d < 2) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Icon(Graphics g, VesperIcon icon, Rectangle bounds, Color color)
    {
        if (icon == VesperIcon.None || bounds.Width < 4 || bounds.Height < 4) return;
        var state = g.Save();
        try
        {
            float size = Math.Min(bounds.Width, bounds.Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(bounds.Left + (bounds.Width - size) / 2f, bounds.Top + (bounds.Height - size) / 2f);
            g.ScaleTransform(size / 24f, size / 24f);
            using var pen = new Pen(color, 2.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var brush = new SolidBrush(color);
            switch (icon)
            {
                case VesperIcon.Plus:
                    g.DrawLine(pen, 12, 5, 12, 19); g.DrawLine(pen, 5, 12, 19, 12); break;
                case VesperIcon.Search:
                    g.DrawEllipse(pen, 4, 4, 13, 13); g.DrawLine(pen, 15.5f, 15.5f, 21, 21); break;
                case VesperIcon.Gear:
                    using (var path = new GraphicsPath())
                    {
                        var pts = new PointF[32];
                        for (int i = 0; i < pts.Length; i++)
                        {
                            double a = -Math.PI / 2 + i * Math.PI / 16;
                            float r = i % 4 is 1 or 2 ? 10f : 7.7f;
                            pts[i] = new PointF(12 + r * (float)Math.Cos(a), 12 + r * (float)Math.Sin(a));
                        }
                        path.AddPolygon(pts); g.DrawPath(pen, path);
                    }
                    g.DrawEllipse(pen, 9, 9, 6, 6); break;
                case VesperIcon.More:
                    g.FillEllipse(brush, 10.7f, 4, 2.6f, 2.6f); g.FillEllipse(brush, 10.7f, 10.7f, 2.6f, 2.6f); g.FillEllipse(brush, 10.7f, 17.4f, 2.6f, 2.6f); break;
                case VesperIcon.Audio:
                    g.DrawLine(pen, 10, 6, 19, 4); g.DrawLine(pen, 10, 6, 10, 17); g.DrawLine(pen, 19, 4, 19, 15);
                    g.DrawEllipse(pen, 4, 16, 6, 4); g.DrawEllipse(pen, 13, 14, 6, 4); break;
                case VesperIcon.Video:
                    g.DrawRectangle(pen, 3.5f, 5.5f, 17, 13);
                    g.DrawLine(pen, 6.5f, 5.5f, 6.5f, 18.5f); g.DrawLine(pen, 17.5f, 5.5f, 17.5f, 18.5f);
                    g.DrawLines(pen, [new PointF(10, 9), new PointF(15, 12), new PointF(10, 15)]); break;
                case VesperIcon.Disc:
                    g.DrawEllipse(pen, 3.5f, 3.5f, 17, 17); g.DrawEllipse(pen, 9, 9, 6, 6);
                    g.DrawArc(pen, 6.5f, 6.5f, 11, 11, 200, 100); break;
                case VesperIcon.Archive:
                    g.DrawRectangle(pen, 5, 4, 14, 16);
                    g.DrawLine(pen, 10, 4, 10, 20);
                    for (int i = 0; i < 4; i++) g.DrawLine(pen, 10, 6 + i * 3.4f, 12.5f, 6 + i * 3.4f);
                    break;
                case VesperIcon.Book:
                    g.DrawLines(pen, [new PointF(6, 3), new PointF(15, 3), new PointF(19, 7), new PointF(19, 21), new PointF(6, 21), new PointF(6, 3)]);
                    g.DrawLine(pen, 14.5f, 3, 14.5f, 7.5f); g.DrawLine(pen, 14.5f, 7.5f, 19, 7.5f);
                    g.DrawLine(pen, 9, 12, 16, 12); g.DrawLine(pen, 9, 16, 16, 16); break;
                case VesperIcon.File:
                    g.DrawLines(pen, [new PointF(6, 2), new PointF(15, 2), new PointF(20, 7), new PointF(20, 22), new PointF(6, 22), new PointF(6, 2)]);
                    g.DrawLines(pen, [new PointF(15, 2), new PointF(15, 7), new PointF(20, 7)]); break;
                case VesperIcon.Check:
                    g.DrawLines(pen, [new PointF(4, 12), new PointF(10, 18), new PointF(20, 6)]); break;
                case VesperIcon.Warning:
                    g.DrawLines(pen, [new PointF(12, 3), new PointF(22, 21), new PointF(2, 21), new PointF(12, 3)]);
                    g.DrawLine(pen, 12, 9, 12, 14); g.FillEllipse(brush, 10.9f, 16.3f, 2.2f, 2.2f); break;
                case VesperIcon.Info:
                    g.DrawEllipse(pen, 3, 3, 18, 18); g.DrawLine(pen, 12, 11, 12, 17);
                    g.FillEllipse(brush, 10.8f, 7, 2.4f, 2.4f); break;
                case VesperIcon.Link:
                    g.DrawArc(pen, 2, 8, 13, 12, 50, 215); g.DrawArc(pen, 9, 4, 13, 12, 225, 215);
                    g.DrawLine(pen, 9, 15, 15, 9); break;
                case VesperIcon.Pause:
                    g.FillRectangle(brush, 6, 5, 3, 14); g.FillRectangle(brush, 15, 5, 3, 14); break;
                case VesperIcon.Folder:
                    g.DrawLines(pen, [new PointF(2, 7), new PointF(9, 7), new PointF(11, 9), new PointF(22, 9), new PointF(22, 20), new PointF(2, 20), new PointF(2, 7)]);
                    break;
                case VesperIcon.Download:
                    g.DrawLine(pen, 12, 3, 12, 16); g.DrawLines(pen, [new PointF(7, 11), new PointF(12, 16), new PointF(17, 11)]);
                    g.DrawLine(pen, 4, 20, 20, 20); break;
                case VesperIcon.Queue:
                    g.DrawLine(pen, 5, 6, 17, 6); g.DrawLine(pen, 5, 11, 15, 11);
                    g.DrawLine(pen, 5, 16, 11, 16);
                    g.DrawLine(pen, 18, 13, 18, 21); g.DrawLine(pen, 14, 17, 22, 17); break;
                case VesperIcon.Clipboard:
                    g.DrawRectangle(pen, 5, 5, 15, 16); g.DrawRectangle(pen, 9, 2, 7, 5); break;
                case VesperIcon.Close:
                    g.DrawLine(pen, 5, 5, 19, 19); g.DrawLine(pen, 19, 5, 5, 19); break;
                case VesperIcon.ChevronDown:
                    g.DrawLines(pen, [new PointF(6, 9), new PointF(12, 15), new PointF(18, 9)]); break;
                case VesperIcon.Play:
                    g.DrawLines(pen, [new PointF(8, 4), new PointF(19, 12), new PointF(8, 20), new PointF(8, 4)]); break;
                case VesperIcon.Pencil:
                    g.DrawLines(pen, [new PointF(5, 18), new PointF(7, 13), new PointF(17, 3),
                        new PointF(21, 7), new PointF(11, 17), new PointF(5, 18)]);
                    g.DrawLine(pen, 14, 6, 18, 10); break;
                case VesperIcon.Trash:
                    g.DrawLine(pen, 4, 7, 20, 7); g.DrawLine(pen, 9, 4, 15, 4);
                    g.DrawLines(pen, [new PointF(6, 8), new PointF(8, 21), new PointF(16, 21), new PointF(18, 8)]);
                    g.DrawLine(pen, 10, 11, 10, 18); g.DrawLine(pen, 14, 11, 14, 18); break;
                case VesperIcon.RemoveList:
                    g.DrawLine(pen, 4, 6, 17, 6); g.DrawLine(pen, 4, 12, 14, 12); g.DrawLine(pen, 4, 18, 12, 18);
                    g.DrawLine(pen, 16, 17, 22, 17); break;
                case VesperIcon.Storage:
                    g.DrawEllipse(pen, 4, 3, 16, 6); g.DrawLine(pen, 4, 6, 4, 18); g.DrawLine(pen, 20, 6, 20, 18);
                    g.DrawArc(pen, 4, 9, 16, 6, 0, 180); g.DrawArc(pen, 4, 15, 16, 6, 0, 180); break;
                case VesperIcon.Quality:
                    g.DrawRectangle(pen, 3, 4, 18, 13); g.DrawLine(pen, 8, 21, 16, 21); g.DrawLine(pen, 12, 17, 12, 21); break;
                case VesperIcon.Provider:
                    g.DrawEllipse(pen, 3, 3, 18, 18); g.DrawEllipse(pen, 8, 3, 8, 18); g.DrawLine(pen, 4, 12, 20, 12); break;
                case VesperIcon.Crown:
                    g.DrawPolygon(pen, [new Point(3, 8), new Point(8, 12), new Point(12, 5), new Point(16, 12),
                        new Point(21, 8), new Point(18, 18), new Point(6, 18)]);
                    g.DrawLine(pen, 6, 21, 18, 21); break;
                case VesperIcon.Leaf:
                    using (var leaf = new GraphicsPath())
                    {
                        leaf.AddBezier(5, 18, 0, 8, 10, 4, 21, 3);
                        leaf.AddBezier(21, 3, 20, 15, 13, 22, 5, 18);
                        g.DrawPath(pen, leaf);
                    }
                    g.DrawLine(pen, 3, 21, 14, 10); break;
                case VesperIcon.Layers:
                    g.DrawPolygon(pen, [new Point(2, 8), new Point(12, 3), new Point(22, 8), new Point(12, 13)]);
                    g.DrawLines(pen, [new Point(2, 12), new Point(12, 17), new Point(22, 12)]);
                    g.DrawLines(pen, [new Point(2, 16), new Point(12, 21), new Point(22, 16)]); break;
                case VesperIcon.Sort:
                    g.DrawLine(pen, 7, 20, 7, 4); g.DrawLines(pen, [new Point(3, 8), new Point(7, 4), new Point(11, 8)]);
                    g.DrawLine(pen, 17, 4, 17, 20); g.DrawLines(pen, [new Point(13, 16), new Point(17, 20), new Point(21, 16)]); break;
                case VesperIcon.Document:
                    g.DrawLines(pen, [new Point(5, 3), new Point(14, 3), new Point(19, 8), new Point(19, 21),
                        new Point(5, 21), new Point(5, 3)]);
                    g.DrawLines(pen, [new Point(14, 3), new Point(14, 8), new Point(19, 8)]);
                    g.DrawLine(pen, 9, 12, 15, 12); g.DrawLine(pen, 9, 16, 15, 16); break;
                case VesperIcon.Activity:
                    g.DrawLines(pen, [new Point(2, 12), new Point(7, 12), new Point(10, 5), new Point(14, 20),
                        new Point(17, 12), new Point(22, 12)]); break;
                case VesperIcon.Shield:
                    g.DrawLines(pen, [new Point(12, 3), new Point(20, 6), new Point(19, 15), new Point(12, 21),
                        new Point(5, 15), new Point(4, 6), new Point(12, 3)]);
                    g.DrawLines(pen, [new Point(8, 12), new Point(11, 15), new Point(16, 9)]); break;
                case VesperIcon.Gauge:
                    g.DrawArc(pen, 3, 4, 18, 18, 195, 150);
                    g.DrawLine(pen, 12, 15, 18, 9); g.FillEllipse(brush, 10, 13, 4, 4); break;
                case VesperIcon.Clock:
                    g.DrawEllipse(pen, 3, 3, 18, 18);
                    g.DrawLine(pen, 12, 6, 12, 12);
                    g.DrawLine(pen, 12, 12, 16, 14); break;
                case VesperIcon.Refresh:
                    g.DrawArc(pen, 4, 4, 16, 16, 30, 295);
                    g.DrawLines(pen, [new PointF(18, 3), new PointF(20, 10), new PointF(13, 9)]); break;
            }
        }
        finally { g.Restore(state); }
    }
}

// Keep native button input and accessibility. The face is painted on the actual parent surface.
public sealed class VesperButton : Button
{
    private bool _hovered;
    private bool _pressed;
    private VesperPalette _palette = VesperPalette.Light;

    [DefaultValue(false)] public bool Primary { get; set; }
    [DefaultValue(false)] internal bool PreviewPressed { get; set; }
    [DefaultValue(false)] public bool NavigationActive { get; set; }
    [DefaultValue(false)] public bool NavigationItem { get; set; }
    [DefaultValue(false)] public bool Quiet { get; set; }
    [DefaultValue(false)] public bool TrailingChevron { get; set; }
    [DefaultValue(VesperIcon.None)] public VesperIcon Symbol { get; set; }
    public VesperButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        Font = new Font("Segoe UI", 9F);
        Height = 33;
    }
    internal void ApplyPalette(VesperPalette palette) { _palette = palette; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { _pressed = false; base.OnLostFocus(e); Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { _pressed = true; Invalidate(); }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { _pressed = false; Invalidate(); }
        base.OnKeyUp(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        VesperPanel.PaintBackdrop(this, e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        bool pressed = (_pressed || PreviewPressed) && Enabled;
        bool active = (_hovered || pressed) && Enabled;
        bool material = _palette.Botanical;
        Color fill = _palette.Surface;
        Color edge = _palette.Border;
        Color ink = Enabled ? _palette.Text : _palette.Muted;
        if (Quiet)
        {
            fill = active ? _palette.Secondary : Color.Transparent;
            edge = Color.Transparent;
            if (Enabled) ink = Symbol == VesperIcon.Link ? _palette.Link : _palette.HighContrast ? _palette.Text : _palette.Muted;
        }
        else if (NavigationItem)
        {
            fill = NavigationActive ? _palette.Selection : active ? _palette.Back : Color.Transparent;
            edge = NavigationActive ? _palette.Border : Color.Transparent;
            if (NavigationActive && _palette.HighContrast) ink = SystemColors.HighlightText;
        }
        else if (Primary)
        {
            fill = Enabled ? (active ? _palette.GreenHover : _palette.Green) : _palette.Secondary;
            edge = Enabled && !_palette.HighContrast ? Color.Transparent : _palette.Border;
            ink = Enabled ? _palette.ActionInk : _palette.Muted;
        }
        else
        {
            fill = active ? _palette.Secondary : _palette.Surface;
        }
        var face = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        if (fill.A > 0)
        {
            int radius = UiKit.Scale(this, 5);
            using var shape = VesperDrawing.Rounded(face, radius);
            if (Primary && material && Enabled && !pressed)
            {
                using var brush = new LinearGradientBrush(face, VesperDrawing.Mix(fill, Color.White, 0.075f),
                    fill, LinearGradientMode.Vertical);
                g.FillPath(brush, shape);
            }
            else
            {
                using var brush = new SolidBrush(fill);
                g.FillPath(brush, shape);
            }
            if (edge.A > 0)
            {
                using var pen = new Pen(edge);
                g.DrawPath(pen, shape);
            }
            if (Enabled && !pressed && !Quiet && !NavigationItem && !_palette.HighContrast && (!Primary || material))
            {
                int x = face.Left + radius + 1;
                int y = face.Top + UiKit.Scale(this, 2);
                int alpha = Primary ? 45 : _palette.IsDark ? 25 : 100;
                if (face.Right - radius > x)
                {
                    using var highlight = new Pen(Color.FromArgb(alpha, Color.White));
                    g.DrawLine(highlight, x, y, face.Right - radius, y);
                }
            }
        }
        if (NavigationItem && NavigationActive)
        {
            using var accent = new SolidBrush(_palette.HighContrast ? SystemColors.HighlightText : _palette.MutedGreen);
            g.FillRectangle(accent, 2, 8, 3, Math.Max(1, Height - 16));
        }

        Color iconInk = Enabled && !Primary && !_palette.HighContrast ? VesperDrawing.Ink(Symbol, _palette) : ink;
        int iconSize = Math.Min(UiKit.Scale(this, 18), Height - UiKit.Scale(this, 10));
        if (string.IsNullOrEmpty(Text) && Symbol != VesperIcon.None)
        {
            VesperDrawing.Icon(g, Symbol, new Rectangle((Width - iconSize) / 2, (Height - iconSize) / 2, iconSize, iconSize), iconInk);
        }
        else
        {
            var measure = TextRenderer.MeasureText(g, Text, Font,
                new Size(2000, Height), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int gap = Symbol == VesperIcon.None ? 0 : UiKit.Scale(this, 7);
            int groupWidth = measure.Width + (Symbol == VesperIcon.None ? 0 : iconSize + gap);
            int left = NavigationItem || TextAlign == ContentAlignment.MiddleLeft ? UiKit.Scale(this, 13) : Math.Max(5, (Width - groupWidth) / 2);
            if (Symbol != VesperIcon.None)
            {
                VesperDrawing.Icon(g, Symbol, new Rectangle(left, (Height - iconSize) / 2, iconSize, iconSize), iconInk);
                left += iconSize + gap;
            }
            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(left, 0, Math.Max(1, Width - left - UiKit.Scale(this, TrailingChevron ? 27 : 5)), Height), ink,
                VesperDrawing.PaintTextFlags | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
        if (TrailingChevron)
            VesperDrawing.Icon(g, VesperIcon.ChevronDown, new Rectangle(Width - UiKit.Scale(this, 25),
                (Height - UiKit.Scale(this, 16)) / 2, UiKit.Scale(this, 16), UiKit.Scale(this, 16)), ink);
        if (Focused && ShowFocusCues)
            VesperDrawing.FocusOutline(g, Rectangle.Inflate(face, -4, -4), ink);
    }

}

// Drawing the closed dropdown ourselves avoids the tiny native WinForms arrow/button seam.
// The open list remains a normal keyboard-accessible Windows menu.
public sealed class VesperChoice : Control
{
    private readonly ContextMenuStrip _menu = new() { ShowImageMargin = false };
    private string[] _items = [];
    private int _index = -1;
    private VesperPalette _palette = VesperPalette.Light;
    public event EventHandler? SelectedIndexChanged;
    [DefaultValue(VesperIcon.None)] public VesperIcon Symbol { get; set; }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _index;
        set
        {
            if (value < -1 || value >= _items.Length || _index == value) return;
            _index = value;
            UpdateSelection();
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SelectedText => _index < 0 ? Text : _items[_index];

    public void SetItems(params string[] values)
    {
        _items = values;
        _index = values.Length > 0 ? 0 : -1;
        foreach (ToolStripItem old in _menu.Items.Cast<ToolStripItem>().ToArray()) old.Dispose();
        for (int i = 0; i < _items.Length; i++)
        {
            int index = i;
            var option = new ToolStripMenuItem(_items[i]);
            option.Click += (_, _) => SelectedIndex = index;
            _menu.Items.Add(option);
        }
        VesperMenuTheme.Apply(_menu, _palette);
        SizeMenuItems();
        UpdateSelection();
        Invalidate();
    }

    internal void ApplyPalette(VesperPalette palette)
    {
        _palette = palette;
        VesperMenuTheme.Apply(_menu, palette);
        SizeMenuItems();
        Invalidate();
    }

    public VesperChoice()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable |
            ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.ComboBox;
        Height = 32;
        Width = 145;
        Font = new Font("Segoe UI", 9F);
        TabStop = true;
        Cursor = Cursors.Hand;
        VesperMenuTheme.Apply(_menu, _palette);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ChoiceAccessibleObject(this);

    private sealed class ChoiceAccessibleObject(VesperChoice owner) : Control.ControlAccessibleObject(owner)
    {
        public override string? Value
        {
            get => owner.SelectedText;
            set
            {
                int index = Array.IndexOf(owner._items, value);
                if (index >= 0) owner.SelectedIndex = index;
            }
        }
    }

    private void UpdateSelection()
    {
        for (int i = 0; i < _menu.Items.Count; i++)
            _menu.Items[i].Tag = i == _index;
    }

    private void SizeMenuItems()
    {
        if (_menu.Visible || _menu.Items.Count == 0) return;
        // Give the popup its width through its items. Setting MinimumSize on a
        // wider popup left a blank, non-highlighted strip at the right edge.
        int scale = Math.Max(96, DeviceDpi);
        int desired = Math.Max(Width, (int)Math.Round(148 * scale / 96d));
        int optionWidth = Math.Max(100, desired - _menu.Padding.Horizontal - 2);
        int itemHeight = Math.Max(28, (int)Math.Round(33 * scale / 96d));
        foreach (ToolStripItem item in _menu.Items)
        {
            item.AutoSize = false;
            item.Margin = Padding.Empty;
            item.Padding = Padding.Empty;
            item.Size = new Size(optionWidth, itemHeight);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SizeMenuItems();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        VesperMenuTheme.Apply(_menu, _palette);
        SizeMenuItems();
        Invalidate();
    }

    protected override void OnClick(EventArgs e) { base.OnClick(e); ShowMenu(); }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down
        || base.IsInputKey(keyData);
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space or Keys.F4)
        {
            ShowMenu();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Down && _index + 1 < _items.Length)
        {
            SelectedIndex++;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Up && _index > 0)
        {
            SelectedIndex--;
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void ShowMenu()
    {
        if (_items.Length == 0 || _menu.Visible) return;
        Focus();
        SizeMenuItems();
        _menu.Show(this, new Point(0, Height + 1));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        VesperPanel.PaintBackdrop(this, e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var face = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        using var path = VesperDrawing.Rounded(face, UiKit.Scale(this, 5));
        Color fill = Enabled ? _palette.Surface : _palette.Secondary;
        using (var bg = new SolidBrush(fill)) e.Graphics.FillPath(bg, path);
        using (var pen = new Pen(Focused ? _palette.MutedGreen : _palette.Border))
            e.Graphics.DrawPath(pen, path);
        int x = UiKit.Scale(this, 11);
        int iconSize = UiKit.Scale(this, 16);
        if (Symbol != VesperIcon.None)
        {
            VesperDrawing.Icon(e.Graphics, Symbol, new Rectangle(x, (Height - iconSize) / 2, iconSize, iconSize),
                Enabled ? VesperDrawing.Ink(Symbol, _palette) : _palette.Muted);
            x += iconSize + UiKit.Scale(this, 7);
        }
        TextRenderer.DrawText(e.Graphics, SelectedText, Font,
            new Rectangle(x, 0, Math.Max(1, Width - x - UiKit.Scale(this, 27)), Height), Enabled ? _palette.Text : _palette.Muted,
            VesperDrawing.PaintTextFlags | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        VesperDrawing.Icon(e.Graphics, VesperIcon.ChevronDown,
            new Rectangle(Width - UiKit.Scale(this, 25), (Height - iconSize) / 2, iconSize, iconSize), _palette.Muted);
        if (Focused && ShowFocusCues)
            VesperDrawing.FocusOutline(e.Graphics, Rectangle.Inflate(face, -4, -4), _palette.Text);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }
}
