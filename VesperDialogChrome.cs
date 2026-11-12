using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace VESPER;

// Keep native window-manager shadows without rendering a large bitmap or running an animation.
internal class VesperPopupForm : Form
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal VesperPalette? DialogPalette { get; set; }
    public VesperPopupForm() => DoubleBuffered = true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return parameters;
        }
    }
}

// All secondary windows have one layout contract: an inset shell, a fixed caption,
// and a content viewport beneath it. Docking is resolved explicitly before reparenting.
internal static class VesperDialogChrome
{
    public const int HeaderHeight = 43; // logical pixels at 96 DPI
    public static int VerticalInset(Form form) => UiKit.Scale(form, HeaderHeight) + 2 * Math.Max(1, UiKit.Scale(form, 1));
    private const int WmNcLButtonDown = 0xA1;
    private static readonly IntPtr HtCaption = new(2);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    public static Panel Attach(Form form, string title, VesperPalette palette, VesperIcon icon = VesperIcon.Info)
    {
        if (form is VesperPopupForm popup) popup.DialogPalette = palette;
        form.SuspendLayout();
        form.FormBorderStyle = FormBorderStyle.None;
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.CenterParent;

        Color edge = palette.HighContrast ? SystemColors.WindowFrame : palette.Border;
        form.BackColor = edge; // A real perimeter remains visible even when child panels cover the form.

        // Capture the original layout before moving controls to a different parent.
        // WinForms can otherwise dock a Fill control across a Left sidebar (or under
        // the caption) when a zero-size intermediate parent is first created.
        var originals = form.Controls.Cast<Control>()
            .Select(control => (Control: control, Dock: control.Dock)).ToArray();

        var shell = new VesperPanel { BackColor = palette.Surface, TabStop = false };
        var content = new VesperPanel { BackColor = palette.Surface, TabStop = false };
        var header = new VesperPanel { BackColor = palette.Botanical ? palette.Back : palette.Secondary, TabStop = false };
        var art = BotanicalArt.Acquire(palette);
        if (art is not null) form.Disposed += (_, _) => art.Dispose();

        foreach (var (control, _) in originals)
        {
            form.Controls.Remove(control);
            control.Dock = DockStyle.None;
            // These were docked controls; their bounds are owned by LayoutContent below.
            control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            content.Controls.Add(control);
        }

        var captionFont = new Font("Segoe UI", 9F, FontStyle.Bold);
        form.Disposed += (_, _) => captionFont.Dispose();
        var caption = new VesperSurfaceLabel
        {
            Text = title,
            Font = captionFont,
            ForeColor = palette.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false
        };
        var close = new VesperButton
        {
            Symbol = VesperIcon.Close, Quiet = true,
            AccessibleName = "Close dialog", Size = new Size(30, 30),
            Cursor = Cursors.Hand, TabStop = true
        };
        close.ApplyPalette(palette);
        close.Click += (_, _) =>
        {
            form.DialogResult = DialogResult.Cancel;
            form.Close();
        };
        header.Controls.AddRange([caption, close]);
        header.Decoration = (graphics, clip) =>
        {
            if (art is not null)
            {
                int left = caption.Right + UiKit.Scale(form, 12);
                int right = close.Left - UiKit.Scale(form, 12);
                int width = Math.Clamp(right - left, 0, UiKit.Scale(form, 190));
                int inset = UiKit.Scale(form, 4);
                art.PaintHeader(graphics, header.ClientRectangle, clip,
                    new Rectangle(right - width, inset, width, Math.Max(0, header.Height - 2 * inset)), dialog: true);
            }
            int size = UiKit.Scale(form, 18);
            VesperDrawing.Icon(graphics, icon, new Rectangle(UiKit.Scale(form, 15), (header.Height - size) / 2, size, size),
                VesperDrawing.Ink(icon, palette));
            using var divider = new Pen(palette.Divider);
            graphics.DrawLine(divider, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        shell.Controls.AddRange([content, header]);
        form.Controls.Add(shell);

        void LayoutContent()
        {
            int left = 0, top = 0;
            int right = content.ClientSize.Width, bottom = content.ClientSize.Height;

            // Reserve entire bottom/top strips before the sidebars and fill region.
            // This avoids Settings' content appearing underneath its navigation.
            foreach (var (control, dock) in originals.Where(x => x.Dock == DockStyle.Bottom))
            {
                int height = control.Height;
                bottom = Math.Max(top, bottom - height);
                control.SetBounds(left, bottom, Math.Max(1, right - left), height);
            }
            foreach (var (control, dock) in originals.Where(x => x.Dock == DockStyle.Top))
            {
                int height = control.Height;
                control.SetBounds(left, top, Math.Max(1, right - left), height);
                top += height;
            }
            foreach (var (control, dock) in originals.Where(x => x.Dock == DockStyle.Left))
            {
                int width = control.Width;
                control.SetBounds(left, top, width, Math.Max(1, bottom - top));
                left += width;
            }
            foreach (var (control, dock) in originals.Where(x => x.Dock == DockStyle.Right))
            {
                int width = control.Width;
                right = Math.Max(left, right - width);
                control.SetBounds(right, top, width, Math.Max(1, bottom - top));
            }
            foreach (var (control, dock) in originals.Where(x => x.Dock == DockStyle.Fill))
                control.SetBounds(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));

            foreach (var (control, dock) in originals.Where(x => x.Dock == DockStyle.None))
                control.PerformLayout();
        }

        void LayoutChrome()
        {
            int inset = Math.Max(1, (int)Math.Round(form.DeviceDpi / 96d));
            int headerHeight = (int)Math.Round(HeaderHeight * form.DeviceDpi / 96d);
            shell.SetBounds(inset, inset,
                Math.Max(1, form.ClientSize.Width - 2 * inset),
                Math.Max(1, form.ClientSize.Height - 2 * inset));
            // Clip the inset surface too, or it hides the perimeter at the corners.
            using (var inside = VesperDrawing.Rounded(new Rectangle(0, 0,
                shell.ClientSize.Width, shell.ClientSize.Height),
                Math.Max(8, (int)Math.Round(11d * form.DeviceDpi / 96))))
            {
                Region? oldRegion = shell.Region;
                shell.Region = new Region(inside);
                oldRegion?.Dispose();
            }
            header.SetBounds(0, 0, shell.ClientSize.Width, headerHeight);
            content.SetBounds(0, headerHeight, shell.ClientSize.Width,
                Math.Max(1, shell.ClientSize.Height - headerHeight));
            int buttonSize = Math.Max(28, (int)Math.Round(30d * form.DeviceDpi / 96));
            close.SetBounds(Math.Max(0, header.Width - buttonSize - UiKit.Scale(form, 12)),
                (header.Height - buttonSize) / 2, buttonSize, buttonSize);
            int captionX = UiKit.Scale(form, 43);
            int captionWidth = Math.Min(close.Left - captionX - UiKit.Scale(form, 12),
                TextRenderer.MeasureText(title, caption.Font).Width + UiKit.Scale(form, 8));
            caption.SetBounds(captionX, 0, Math.Max(1, captionWidth), header.Height);
            header.Invalidate(true);
            LayoutContent();
        }

        void StartDrag(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !form.IsHandleCreated) return;
            ReleaseCapture();
            SendMessage(form.Handle, WmNcLButtonDown, HtCaption, IntPtr.Zero);
        }
        header.MouseDown += StartDrag;
        caption.MouseDown += StartDrag;

        content.Resize += (_, _) => LayoutContent();
        form.ClientSizeChanged += (_, _) =>
        {
            LayoutChrome();
            Shape(form);
        };
        form.Shown += (_, _) =>
        {
            LayoutChrome();
            Shape(form);
        };
        form.Paint += (_, e) =>
        {
            if (form.ClientSize.Width < 8 || form.ClientSize.Height < 8) return;
            GraphicsState previous = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = VesperDrawing.Rounded(new Rectangle(0, 0,
                form.ClientSize.Width - 1, form.ClientSize.Height - 1), 12);
            using var stroke = new Pen(edge);
            e.Graphics.DrawPath(stroke, path);
            e.Graphics.Restore(previous);
        };

        LayoutChrome();
        form.ResumeLayout(true);
        Shape(form);
        return header;
    }

    private static void Shape(Form form)
    {
        if (form.ClientSize.Width < 20 || form.ClientSize.Height < 20) return;
        int radius = Math.Max(10, (int)Math.Round(12d * form.DeviceDpi / 96));
        using var path = VesperDrawing.Rounded(new Rectangle(0, 0,
            form.ClientSize.Width, form.ClientSize.Height), radius);
        Region? previous = form.Region;
        form.Region = new Region(path);
        previous?.Dispose();
    }
}
