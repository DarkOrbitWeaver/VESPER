using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace VESPER;

// A compact time bar with two handles. The Start and End text fields stay the precise way to enter a time;
// this is the quick way. The owner form keeps both in step through SetRange and RangeChanged.
public sealed class VesperRangeBar : Control
{
    private VesperPalette _palette = VesperPalette.Light;
    private double _duration;
    private double _start;
    private double _end;
    private bool _startActive = true;
    private bool _dragging;

    // Raised only when the user moves a handle, never by SetRange.
    public event EventHandler? RangeChanged;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double StartSeconds => _start;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double EndSeconds => _end;

    public VesperRangeBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable |
            ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.Slider;
        AccessibleName = "Trim range";
        AccessibleDescription = "Left and Right move a handle by one second. Up and Down switch between the Start and End handles.";
        TabStop = true;
        Height = 26;
    }

    internal void ApplyPalette(VesperPalette palette)
    {
        _palette = palette;
        Invalidate();
    }

    public void SetDuration(double seconds)
    {
        _duration = Math.Max(0, seconds);
        _start = Math.Clamp(_start, 0, _duration);
        _end = Math.Clamp(_end, _start, _duration);
        Invalidate();
    }

    public void SetRange(double start, double end)
    {
        start = Math.Clamp(start, 0, _duration);
        end = Math.Clamp(end, start, _duration);
        if (start == _start && end == _end) return;
        _start = start;
        _end = end;
        Invalidate();
    }

    private int HandleSize => UiKit.Scale(this, 14);
    private int TrackLeft => HandleSize / 2 + 1;
    private int TrackWidth => Math.Max(1, Width - HandleSize - 2);

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        using (var back = new SolidBrush(_palette.Surface)) g.FillRectangle(back, ClientRectangle);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int middle = Height / 2;
        int thickness = Math.Max(2, UiKit.Scale(this, 4));
        int startX = ClipSelection.PixelAt(_start, TrackLeft, TrackWidth, _duration);
        int endX = ClipSelection.PixelAt(_end, TrackLeft, TrackWidth, _duration);
        Color selected = Enabled ? _palette.ProgressGreen : _palette.Muted;

        using (var track = new SolidBrush(_palette.ProgressTrack))
            g.FillRectangle(track, TrackLeft, middle - thickness / 2, TrackWidth, thickness);
        using (var range = new SolidBrush(selected))
            g.FillRectangle(range, startX, middle - thickness / 2, Math.Max(1, endX - startX), thickness);

        PaintHandle(g, startX, middle, selected, Focused && _startActive);
        PaintHandle(g, endX, middle, selected, Focused && !_startActive);
    }

    private void PaintHandle(Graphics g, int x, int middle, Color color, bool active)
    {
        int size = HandleSize;
        var bounds = new Rectangle(x - size / 2, middle - size / 2, size, size);
        using (var fill = new SolidBrush(_palette.Surface)) g.FillEllipse(fill, bounds);
        using (var edge = new Pen(color, active ? 3f : 2f)) g.DrawEllipse(edge, bounds);
        if (!active) return;
        // The focused handle gets a filled centre so it is clear which one the keyboard moves.
        var centre = Rectangle.Inflate(bounds, -size / 4, -size / 4);
        using var dot = new SolidBrush(color);
        g.FillEllipse(dot, centre);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _duration <= 0) return;
        Focus();
        int startX = ClipSelection.PixelAt(_start, TrackLeft, TrackWidth, _duration);
        int endX = ClipSelection.PixelAt(_end, TrackLeft, TrackWidth, _duration);
        // Take the nearer handle. When both sit on the same spot, the side of the click decides.
        int toStart = Math.Abs(e.X - startX), toEnd = Math.Abs(e.X - endX);
        _startActive = toStart < toEnd || toStart == toEnd && e.X < startX;
        _dragging = true;
        Capture = true;
        MoveActiveHandle(ClipSelection.SecondsAt(e.X, TrackLeft, TrackWidth, _duration));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) MoveActiveHandle(ClipSelection.SecondsAt(e.X, TrackLeft, TrackWidth, _duration));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        _dragging = false;
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_duration <= 0) return;
        double current = _startActive ? _start : _end;
        double step = e.Shift ? 10 : 1;
        switch (e.KeyCode)
        {
            case Keys.Left: MoveActiveHandle(current - step); break;
            case Keys.Right: MoveActiveHandle(current + step); break;
            case Keys.Home: MoveActiveHandle(0); break;
            case Keys.End: MoveActiveHandle(_duration); break;
            case Keys.Up:
            case Keys.Down:
                _startActive = !_startActive;
                Invalidate();
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void MoveActiveHandle(double seconds)
    {
        (double start, double end) = ClipSelection.MoveHandle(_startActive, seconds, _start, _end, _duration);
        if (start == _start && end == _end) return;
        _start = start;
        _end = end;
        Invalidate();
        RangeChanged?.Invoke(this, EventArgs.Empty);
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}
