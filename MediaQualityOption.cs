using System.Drawing.Drawing2D;

namespace VESPER;

// Keep RadioButton's selection, keyboard group and accessibility. Only the visible face is custom.
internal sealed class MediaQualityOption : RadioButton
{
    private readonly VesperPalette _palette;
    private readonly VesperIcon _symbol;
    private readonly string _title;
    private Font? _titleFont;
    private string _summary = "";
    private string _container = "";
    private bool _available = true;
    private bool _hovered;

    internal MediaQualityOption(string title, VesperIcon symbol, VesperPalette palette)
    {
        _title = Text = title;
        _symbol = symbol;
        _palette = palette;
        Cursor = Cursors.Hand;
        ForeColor = palette.Text;
        BackColor = palette.Surface;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        if (!palette.HighContrast) SetStyle(ControlStyles.Opaque, true);
    }

    internal void SetFormat(string summary, string container, bool available)
    {
        _summary = summary;
        _container = container;
        _available = available;
        Text = string.Join(" · ", new[] { _title, summary, container }.Where(part => part.Length != 0));
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = false; Invalidate(); }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnFontChanged(EventArgs e)
    {
        _titleFont?.Dispose();
        _titleFont = null;
        base.OnFontChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_palette.HighContrast) { base.OnPaint(e); return; }

        int dpi = FindForm()?.DeviceDpi ?? DeviceDpi;
        int S(int value) => (int)Math.Round(value * dpi / 96d);
        var graphics = e.Graphics;
        VesperPanel.PaintBackdrop(this, e);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color surface = _palette.Surface;
        if (Checked || _hovered && Enabled)
        {
            surface = Checked ? _palette.Selection : _palette.Secondary;
            using var shape = VesperDrawing.Rounded(new Rectangle(1, 1, Width - 3, Height - 3), S(7));
            using var brush = new SolidBrush(surface);
            graphics.FillPath(brush, shape);
            if (Checked)
            {
                using var border = new Pen(VesperDrawing.Mix(surface, _palette.MutedGreen, 0.40f));
                graphics.DrawPath(border, shape);
            }
        }
        else if (_symbol != VesperIcon.Layers)
        {
            using var rule = new Pen(_palette.Divider);
            graphics.DrawLine(rule, S(12), Height - 1, Width - S(12), Height - 1);
        }

        int iconSize = S(18);
        int titleLeft = S(41);
        Color accent = Enabled && _available ? VesperDrawing.Ink(_symbol, _palette) : _palette.Muted;
        VesperDrawing.Icon(graphics, _symbol, new Rectangle(S(13), (Height - iconSize) / 2, iconSize, iconSize), accent);

        int markSize = S(15);
        int markX = Width - S(13) - markSize;
        var mark = new Rectangle(markX, (Height - markSize) / 2, markSize, markSize);
        if (Checked)
        {
            using var dot = new SolidBrush(_palette.Green);
            graphics.FillEllipse(dot, mark);
            VesperDrawing.Icon(graphics, VesperIcon.Check, Rectangle.Inflate(mark, -S(2), -S(2)), _palette.ActionInk);
        }
        else if (_symbol == VesperIcon.Layers)
        {
            VesperDrawing.Icon(graphics, VesperIcon.ChevronDown, mark, accent);
        }
        else
        {
            using var stroke = new Pen(_palette.Border);
            graphics.DrawEllipse(stroke, mark);
        }

        int end = markX - S(11);
        if (_container.Length > 0 && Width >= S(360))
        {
            int badgeWidth = Math.Max(S(38), TextRenderer.MeasureText(graphics, _container, Font).Width + S(10));
            int badgeHeight = Math.Min(Height - 2, Math.Max(S(21), Font.Height + S(3)));
            var badge = new Rectangle(end - badgeWidth, (Height - badgeHeight) / 2, badgeWidth, badgeHeight);
            using var shape = VesperDrawing.Rounded(badge, S(5));
            using var fill = new SolidBrush(VesperDrawing.Mix(surface, accent, 0.11f));
            graphics.FillPath(fill, shape);
            TextRenderer.DrawText(graphics, _container, Font, badge, accent,
                VesperDrawing.PaintTextFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            end = badge.Left - S(10);
        }

        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | VesperDrawing.PaintTextFlags;
        bool stacked = Width < S(430) && _summary.Length > 0;
        int titleWidth = stacked || _summary.Length == 0 ? end - titleLeft : S(114);
        int titleHeight = stacked ? Height / 2 : Height;
        _titleFont ??= new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(graphics, _title, _titleFont,
            new Rectangle(titleLeft, 0, Math.Max(1, titleWidth), titleHeight),
            Enabled && _available ? _palette.Text : _palette.Muted, flags);
        if (_summary.Length > 0)
        {
            int summaryX = stacked ? titleLeft : titleLeft + titleWidth + S(6);
            TextRenderer.DrawText(graphics, _summary, Font,
                new Rectangle(summaryX, stacked ? titleHeight : 0, Math.Max(1, end - summaryX), titleHeight),
                _palette.Muted, flags);
        }
        if (Focused && ShowFocusCues)
            VesperDrawing.FocusOutline(graphics, Rectangle.Inflate(ClientRectangle, -S(4), -S(3)), _palette.Text);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _titleFont?.Dispose();
        base.Dispose(disposing);
    }
}
