using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace VESPER;

// A small OS-managed shadow, where Windows supports it, without custom shadow bitmaps.
internal class VesperPopupForm : Form
{
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

// Secondary windows share a dedicated header and an inset content host.
// Explicit bounds avoid Dock.Fill painting underneath the caption.
internal static class VesperDialogChrome
{
    public const int HeaderHeight = 43;
    private const int WmNcLButtonDown = 0xA1;
    private static readonly IntPtr HtCaption = new(2);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    public static Panel Attach(Form form, string title, VesperPalette palette, VesperIcon icon = VesperIcon.Info)
    {
        form.SuspendLayout();
        form.FormBorderStyle = FormBorderStyle.None;
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.CenterParent;
        form.BackColor = palette.Border;

        // Keep each dialog's existing docking order, but contain it entirely below
        // the header. This also leaves a one-pixel perimeter visible for contrast.
        Control[] original = form.Controls.Cast<Control>().ToArray();
        var body = new VesperPanel { BackColor = palette.Surface, TabStop = false };
        body.SuspendLayout();
        for (int i = original.Length - 1; i >= 0; i--)
        {
            form.Controls.Remove(original[i]);
            body.Controls.Add(original[i]);
        }
        body.ResumeLayout();

        var header = new VesperPanel { BackColor = palette.Secondary, TabStop = false };
        var label = new Label
        {
            Text = title,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = palette.Text,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(45, 0),
            Height = HeaderHeight,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        var close = new VesperButton
        {
            Symbol = VesperIcon.Close,
            Quiet = true,
            AccessibleName = "Close dialog",
            Size = new Size(30, 30),
            Cursor = Cursors.Hand,
            TabStop = true
        };
        close.ApplyPalette(palette);
        close.Click += (_, _) =>
        {
            form.DialogResult = DialogResult.Cancel;
            form.Close();
        };
        header.Controls.AddRange([label, close]);
        header.Paint += (_, e) =>
        {
            VesperDrawing.Icon(e.Graphics, icon, new Rectangle(16, 12, 19, 19), palette.Green);
            using var pen = new Pen(palette.Border);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        void LayoutChrome()
        {
            int width = form.ClientSize.Width;
            int height = form.ClientSize.Height;
            header.SetBounds(1, 1, Math.Max(1, width - 2), HeaderHeight);
            body.SetBounds(1, HeaderHeight + 1, Math.Max(1, width - 2),
                Math.Max(1, height - HeaderHeight - 2));
            label.Width = Math.Max(50, header.Width - 105);
            close.Location = new Point(Math.Max(0, header.Width - 44), 6);
        }

        void StartDrag(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !form.IsHandleCreated) return;
            ReleaseCapture();
            SendMessage(form.Handle, WmNcLButtonDown, HtCaption, IntPtr.Zero);
        }
        header.MouseDown += StartDrag;
        label.MouseDown += StartDrag;

        form.Controls.Add(body);
        form.Controls.Add(header);
        form.ClientSizeChanged += (_, _) =>
        {
            LayoutChrome();
            Shape(form);
        };
        form.Shown += (_, _) => Shape(form);
        form.Paint += (_, e) =>
        {
            if (form.ClientSize.Width < 8 || form.ClientSize.Height < 8) return;
            GraphicsState old = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var outline = VesperDrawing.Rounded(new Rectangle(0, 0,
                form.ClientSize.Width - 1, form.ClientSize.Height - 1), 12);
            using var pen = new Pen(palette.Border);
            e.Graphics.DrawPath(pen, outline);
            e.Graphics.Restore(old);
        };
        LayoutChrome();
        form.ResumeLayout(true);
        Shape(form);
        return header;
    }

    private static void Shape(Form form)
    {
        if (form.Width < 20 || form.Height < 20) return;
        using var path = VesperDrawing.Rounded(new Rectangle(0, 0, form.Width, form.Height), 12);
        Region? old = form.Region;
        form.Region = new Region(path);
        old?.Dispose();
    }
}
