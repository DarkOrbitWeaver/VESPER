using System.Runtime.InteropServices;

namespace VESPER;

/// <summary>A small themed scroll track for a multiline link editor.</summary>
internal sealed class VesperTextScroll : Control
{
    private const int GetLineCount = 0x00BA;
    private const int GetFirstVisibleLine = 0x00CE;
    private const int LineScroll = 0x00B6;
    private readonly TextBox _editor;
    private readonly VesperPalette _palette;
    private bool _dragging;
    private int _dragOffset;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    public VesperTextScroll(TextBox editor, VesperPalette palette)
    {
        _editor = editor;
        _palette = palette;
        DoubleBuffered = true;
        TabStop = false;
        AccessibleName = "Scroll links";
        BackColor = palette.Surface;
        editor.TextChanged += RefreshTrack;
        editor.SizeChanged += RefreshTrack;
        editor.KeyUp += RefreshTrack;
        editor.MouseUp += RefreshTrack;
        editor.MouseWheel += RefreshTrack;
    }

    private int Message(int message, int value = 0) => _editor.IsHandleCreated
        ? (int)SendMessage(_editor.Handle, message, IntPtr.Zero, (IntPtr)value) : 0;

    private void RefreshTrack(object? sender, EventArgs e)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Invalidate);
    }

    private (Rectangle Thumb, int First, int Max) Track()
    {
        int total = Math.Max(1, Message(GetLineCount));
        int visible = Math.Max(1, _editor.ClientSize.Height / Math.Max(1, _editor.Font.Height));
        int max = Math.Max(0, total - visible);
        int first = Math.Clamp(Message(GetFirstVisibleLine), 0, max);
        int height = max == 0 ? Height : Math.Clamp((int)((long)Height * visible / total), Math.Min(20, Height), Height);
        int top = max == 0 ? 0 : (int)((long)(Height - height) * first / max);
        return (new Rectangle(Math.Max(1, Width / 3), top, Math.Max(3, Width / 3), height), first, max);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var (thumb, _, max) = Track();
        if (max == 0) return;
        using var brush = new SolidBrush(_palette.MutedGreen);
        e.Graphics.FillRectangle(brush, thumb);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var (thumb, _, max) = Track();
        if (max == 0) return;
        _dragging = true;
        _dragOffset = thumb.Contains(e.Location) ? e.Y - thumb.Top : thumb.Height / 2;
        Capture = true;
        ScrollTo(e.Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) ScrollTo(e.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    private void ScrollTo(int y)
    {
        var (thumb, first, max) = Track();
        int available = Height - thumb.Height;
        if (available <= 0) return;
        int target = Math.Clamp((int)Math.Round((double)(y - _dragOffset) * max / available), 0, max);
        Message(LineScroll, target - first);
        Invalidate();
    }
}
