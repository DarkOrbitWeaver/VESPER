namespace VESPER;

internal sealed class VesperCheckBox : CheckBox
{
    private readonly VesperPalette _palette;

    public VesperCheckBox(VesperPalette palette)
    {
        _palette = palette;
        FlatStyle = FlatStyle.Flat;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_palette.HighContrast) { base.OnPaint(e); return; }

        int size = UiKit.Scale(this, 14);
        var mark = new Rectangle(1, (Height - size) / 2, size, size);
        VesperDrawing.CheckBox(e.Graphics, mark, _palette, Checked, Enabled);
        int left = mark.Right + UiKit.Scale(this, 7);
        var caption = new Rectangle(left, 0, Math.Max(1, Width - left), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, caption, Enabled ? _palette.Text : _palette.Muted,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | VesperDrawing.PaintTextFlags);
        if (Focused && ShowFocusCues)
            VesperDrawing.FocusOutline(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1), _palette.Text);
    }
}

internal sealed class VesperCheckedList(VesperPalette palette) : CheckedListBox
{
    protected override CreateParams CreateParams
    {
        get
        {
            SetStyle(ControlStyles.ApplyThemingImplicitly, true);
            return base.CreateParams;
        }
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (palette.HighContrast || e.Index < 0 || e.Index >= Items.Count)
        {
            base.OnDrawItem(e);
            return;
        }

        Color background = (e.State & DrawItemState.Selected) != 0 ? palette.Selection : palette.Surface;
        using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
        int size = Math.Min(UiKit.Scale(this, 13), Math.Max(1, e.Bounds.Height - 4));
        var mark = new Rectangle(e.Bounds.Left + UiKit.Scale(this, 5), e.Bounds.Top + (e.Bounds.Height - size) / 2, size, size);
        VesperDrawing.CheckBox(e.Graphics, mark, palette, GetItemChecked(e.Index), Enabled);
        int left = mark.Right + UiKit.Scale(this, 8);
        var caption = new Rectangle(left, e.Bounds.Top, Math.Max(1, e.Bounds.Right - left), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, caption, Enabled ? palette.Text : palette.Muted,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | VesperDrawing.PaintTextFlags);
        if ((e.State & (DrawItemState.Focus | DrawItemState.NoFocusRect)) == DrawItemState.Focus)
            VesperDrawing.FocusOutline(e.Graphics, Rectangle.Inflate(e.Bounds, -1, -1), palette.Text);
    }
}
