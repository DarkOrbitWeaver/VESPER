using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace VESPER;

// Simple border for editable fields. The TextBox inside remains the real Windows control.
public sealed class VesperInputFrame : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.FromArgb(217, 222, 214);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FocusColor { get; set; } = Color.FromArgb(115, 148, 124);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active { get; set; }

    public VesperInputFrame()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var edge = new Pen(Active ? FocusColor : BorderColor);
        using var border = VesperDrawing.Rounded(new Rectangle(1, 1,
            Math.Max(1, Width - 3), Math.Max(1, Height - 3)), 5);
        e.Graphics.DrawPath(edge, border);
        base.OnPaint(e);
    }
}
