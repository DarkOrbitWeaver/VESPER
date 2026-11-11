using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace VESPER;

// Used for the few surfaces that paint their own decoration or border.
// Ordinary layout panels do not need a separate backing bitmap.
public sealed class VesperPanel : Panel
{
    // This callback draws only the panel's surface. A child can sample its part
    // without invoking WinForms transparent painting or drawing any other HWND.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action<Graphics, Rectangle>? Decoration { get; set; }

    public VesperPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        Decoration?.Invoke(e.Graphics, e.ClipRectangle);
    }

    internal static void PaintBackdrop(Control child, PaintEventArgs e)
    {
        using (var fill = new SolidBrush(child.Parent?.BackColor ?? child.BackColor))
            e.Graphics.FillRectangle(fill, e.ClipRectangle);
        if (child.Parent is not VesperPanel { Decoration: { } draw }) return;

        Rectangle clip = e.ClipRectangle;
        clip.Offset(child.Location);
        GraphicsState saved = e.Graphics.Save();
        try
        {
            e.Graphics.TranslateTransform(-child.Left, -child.Top);
            e.Graphics.SetClip(clip, CombineMode.Intersect);
            draw(e.Graphics, clip);
        }
        finally { e.Graphics.Restore(saved); }
    }
}

internal sealed class VesperSurfaceLabel : Label
{
    protected override void OnPaintBackground(PaintEventArgs e) => VesperPanel.PaintBackdrop(this, e);
}

internal sealed class VesperSurfacePicture : PictureBox
{
    protected override void OnPaintBackground(PaintEventArgs e) => VesperPanel.PaintBackdrop(this, e);
}
