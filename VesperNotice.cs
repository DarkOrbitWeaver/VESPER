using System.Drawing.Drawing2D;

namespace VESPER;

internal sealed class VesperNotice : Form
{
    private readonly System.Windows.Forms.Timer _dismiss = new();
    private VesperPalette _palette;
    private int _count;
    private string _title = "";
    private string _detail = "";
    private VesperIcon _symbol;
    private Action? _activate;
    private Rectangle _workArea;

    public VesperNotice()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.Manual;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Alert;
        _dismiss.Tick += Dismiss;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return parameters;
        }
    }

    public void ApplyPalette(VesperPalette palette)
    {
        _palette = palette;
        BackColor = palette.Surface;
        Invalidate();
    }

    public bool PresentCompletion(string fileName, VesperPalette palette, Rectangle workArea, Action activate)
    {
        if (Visible && _count > 0)
        {
            _count++;
            _title = $"{_count} downloads complete";
            _detail = fileName;
            AccessibleName = _title + ". " + _detail;
            ApplyPalette(palette);
            return false;
        }

        Present("Download complete", fileName, VesperIcon.Check, palette, workArea, activate, duration: 3000, count: 1);
        return true;
    }

    public void Present(string title, string detail, VesperIcon symbol, VesperPalette palette, Rectangle workArea,
        Action activate, int duration = 5000, int count = 0)
    {
        _dismiss.Stop();
        _dismiss.Interval = duration;
        _title = title;
        _detail = detail;
        _symbol = symbol;
        _activate = activate;
        _count = count;
        _workArea = workArea;
        AccessibleName = _title + ". " + _detail;
        ApplyPalette(palette);
        if (!Visible) Location = new Point(workArea.Right - 100, workArea.Bottom - 80);
        PositionNotice();
        if (!Visible)
        {
            Show();
            PositionNotice();
        }
        _dismiss.Start();
    }

    private void PositionNotice()
    {
        int S(int value) => UiKit.Scale(this, value);
        int inset = S(18);
        Size = new Size(Math.Min(S(350), Math.Max(1, _workArea.Width - inset * 2)), S(65));
        Location = new Point(_workArea.Right - Width - inset, _workArea.Bottom - Height - inset);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (!_workArea.IsEmpty) PositionNotice();
    }

    private void Dismiss(object? sender, EventArgs e)
    {
        _dismiss.Stop();
        _activate = null;
        Hide();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        Action? activate = _activate;
        Dismiss(null, EventArgs.Empty);
        activate?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int S(int value) => UiKit.Scale(this, value);
        using var border = new Pen(_palette.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        var circle = new Rectangle(S(13), (Height - S(32)) / 2, S(32), S(32));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var backing = new SolidBrush(_palette.Selection)) e.Graphics.FillEllipse(backing, circle);
        VesperDrawing.Icon(e.Graphics, _symbol, Rectangle.Inflate(circle, -S(7), -S(7)),
            _palette.HighContrast ? _palette.ActionInk : VesperDrawing.Ink(_symbol, _palette));

        int left = circle.Right + S(12);
        int width = Math.Max(1, Width - left - S(12));
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        using var heading = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, _title, heading, new Rectangle(left, S(10), width, S(21)), _palette.Text, flags);
        TextRenderer.DrawText(e.Graphics, _detail, Font, new Rectangle(left, S(33), width, S(20)), _palette.Muted, flags);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _dismiss.Dispose();
        base.Dispose(disposing);
    }
}
