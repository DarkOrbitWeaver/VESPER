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

    public VesperSearchFrame()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using (var border = new Pen(OutlineColor))
        using (var outline = new GraphicsPath())
        {
            int radius = Math.Min(6, Math.Max(2, bounds.Height / 4));
            int d = radius * 2;
            outline.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            outline.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            outline.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            outline.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            outline.CloseFigure();
            g.DrawPath(border, outline);
        }
        float cx = 17f * DeviceDpi / 96f, cy = Height / 2f - 2f;
        float r = 4f * DeviceDpi / 96f;
        using var searchPen = new Pen(SearchIconColor, Math.Max(1f, DeviceDpi / 96f));
        g.DrawEllipse(searchPen, cx-r, cy-r, 2*r, 2*r);
        g.DrawLine(searchPen, cx+r*.7f, cy+r*.7f, cx+r*1.75f, cy+r*1.75f);
    }
}
