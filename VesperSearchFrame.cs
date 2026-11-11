using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace VESPER;

/// <summary>A lightweight, designer-visible native WinForms search field surround.</summary>
public sealed class VesperSearchFrame : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color OutlineColor { get; set; } = Color.FromArgb(217, 222, 214);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SearchIconColor { get; set; } = Color.FromArgb(124, 142, 127);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = Color.White;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FocusColor { get; set; } = Color.FromArgb(115, 148, 124);

    public VesperSearchFrame()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
    }

    protected override void OnEnter(EventArgs e) { base.OnEnter(e); Invalidate(); }
    protected override void OnLeave(EventArgs e) { base.OnLeave(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        VesperPanel.PaintBackdrop(this, e);
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        using var border = new Pen(ContainsFocus ? FocusColor : OutlineColor);
        using var fill = new SolidBrush(FillColor);
        using var outline = VesperDrawing.Rounded(bounds, UiKit.Scale(this, 5));
        g.FillPath(fill, outline);
        g.DrawPath(border, outline);
        float cx = 17f * DeviceDpi / 96f, cy = Height / 2f - 2f;
        float r = 4f * DeviceDpi / 96f;
        using var searchPen = new Pen(SearchIconColor, Math.Max(1f, DeviceDpi / 96f));
        g.DrawEllipse(searchPen, cx-r, cy-r, 2*r, 2*r);
        g.DrawLine(searchPen, cx+r*.7f, cy+r*.7f, cx+r*1.75f, cy+r*1.75f);
    }
}
