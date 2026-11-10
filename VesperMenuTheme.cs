using System.Drawing.Drawing2D;

namespace VESPER;

// Keep WinForms' menu keyboard and submenu behavior. Paint the same surface at every level.
internal static class VesperMenuTheme
{
    private static readonly Dictionary<(VesperIcon Symbol, int Color, int Dpi), Image> IconCache = [];
    private static readonly Font MenuFont = new("Segoe UI", 9F);

    public static Image IconImage(VesperIcon symbol, VesperPalette palette, int dpi = 96)
    {
        var key = (symbol, palette.Muted.ToArgb(), dpi);
        if (IconCache.TryGetValue(key, out Image? cached)) return cached;
        int size = Math.Max(16, (int)Math.Round(20d * dpi / 96));
        var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
            VesperDrawing.Icon(graphics, symbol, new Rectangle(2, 2, size - 4, size - 4), palette.Muted);
        return IconCache[key] = bitmap;
    }

    public static void Apply(ContextMenuStrip menu, VesperPalette palette, bool withIcons = false)
    {
        Style(menu, palette, withIcons);
        StyleChildren(menu.Items, palette);
    }

    public static void FormatItems(ToolStripItemCollection items, VesperPalette palette,
        int itemWidth = 224, int dpi = 96)
    {
        int width = Math.Max(100, (int)Math.Round(itemWidth * dpi / 96d));
        int height = Math.Max(28, (int)Math.Round(33d * dpi / 96d));
        foreach (ToolStripItem item in items)
        {
            item.AutoSize = false;
            item.Margin = Padding.Empty;
            item.Padding = Padding.Empty;
            item.Size = new Size(width, item is ToolStripSeparator ? Math.Max(7, height / 3) : height);
            WireHandCursor(item);

            if (item is not ToolStripMenuItem { HasDropDownItems: true } submenu) continue;
            Style(submenu.DropDown, palette, withIcons: true);
            submenu.DropDownOpened -= AlignSubmenu;
            submenu.DropDownOpened += AlignSubmenu;
            FormatItems(submenu.DropDownItems, palette, itemWidth: 194, dpi: dpi);
        }
    }

    private static void StyleChildren(ToolStripItemCollection items, VesperPalette palette)
    {
        foreach (ToolStripItem item in items)
        {
            WireHandCursor(item);
            if (item is not ToolStripMenuItem { HasDropDownItems: true } submenu) continue;
            Style(submenu.DropDown, palette, withIcons: true);
            submenu.DropDownOpened -= AlignSubmenu;
            submenu.DropDownOpened += AlignSubmenu;
            StyleChildren(submenu.DropDownItems, palette);
        }
    }

    private static void Style(ToolStripDropDown menu, VesperPalette palette, bool withIcons)
    {
        menu.Renderer = new VesperMenuRenderer(palette);
        menu.BackColor = palette.Surface;
        menu.ForeColor = palette.Text;
        menu.Font = MenuFont;
        menu.Padding = new Padding(4, 5, 4, 5);
        menu.DropShadowEnabled = true;
        menu.Cursor = Cursors.Default;
        if (menu is ToolStripDropDownMenu dropdown)
        {
            dropdown.ShowImageMargin = withIcons;
            dropdown.ShowCheckMargin = false;
        }
        menu.Opened -= RoundOpenedMenu;
        menu.Opened += RoundOpenedMenu;
        menu.SizeChanged -= RoundResizedMenu;
        menu.SizeChanged += RoundResizedMenu;
    }

    // ToolStripItem isn't a Control and has no Cursor property. The owning popup is.
    private static void WireHandCursor(ToolStripItem item)
    {
        item.MouseEnter -= SetHandCursor;
        item.MouseEnter += SetHandCursor;
        item.MouseMove -= SetHandCursor;
        item.MouseMove += SetHandCursor;
        item.MouseLeave -= ResetCursor;
        item.MouseLeave += ResetCursor;
    }

    private static void SetHandCursor(object? sender, EventArgs e)
    {
        if (sender is ToolStripItem item && item.Owner is ToolStrip owner)
            owner.Cursor = item.Enabled && item is ToolStripMenuItem ? Cursors.Hand : Cursors.Default;
    }

    private static void SetHandCursor(object? sender, MouseEventArgs e) => SetHandCursor(sender, EventArgs.Empty);

    private static void ResetCursor(object? sender, EventArgs e)
    {
        if (sender is ToolStripItem { Owner: ToolStrip owner }) owner.Cursor = Cursors.Default;
    }

    // WinForms may position a child menu using the item's allocated width rather
    // than the visible parent's edge. Anchor to that edge and flip near the screen.
    private static void AlignSubmenu(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem item || item.Owner is not ToolStripDropDown parent) return;
        ToolStripDropDown child = item.DropDown;
        if (!child.Visible || parent.IsDisposed) return;

        Rectangle screen = Screen.FromControl(parent).WorkingArea;
        int right = parent.Right - 2;
        int left = parent.Left - child.Width + 2;
        int x = right + child.Width <= screen.Right ? right : left;
        x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - child.Width));
        int y = parent.Top + item.Bounds.Top;
        y = Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - child.Height));
        child.Location = new Point(x, y);
        RoundMenu(child);
    }

    private static void RoundOpenedMenu(object? sender, EventArgs e)
    {
        if (sender is ToolStripDropDown menu) RoundMenu(menu);
    }

    private static void RoundResizedMenu(object? sender, EventArgs e)
    {
        if (sender is ToolStripDropDown { Visible: true } menu) RoundMenu(menu);
    }

    private static void RoundMenu(ToolStripDropDown menu)
    {
        if (menu.Width < 12 || menu.Height < 12 || SystemInformation.HighContrast) return;
        using var outline = VesperDrawing.Rounded(new Rectangle(0, 0, menu.Width, menu.Height), 9);
        Region? old = menu.Region;
        menu.Region = new Region(outline);
        old?.Dispose();
        menu.Invalidate();
    }
}

internal sealed class VesperMenuRenderer(VesperPalette colors) : ToolStripProfessionalRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(colors.Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(colors.Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown) return;
        Rectangle bounds = e.ToolStrip.ClientRectangle;
        if (bounds.Width < 8 || bounds.Height < 8) return;
        var old = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(colors.Border);
        using var path = VesperDrawing.Rounded(new Rectangle(1, 1, bounds.Width - 3, bounds.Height - 3), 9);
        e.Graphics.DrawPath(pen, path);
        e.Graphics.Restore(old);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripMenuItem item || !item.Enabled ||
            (!item.Selected && item.Tag is not true)) return;
        var old = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Account for WinForms' reserved arrow/margin column, not just text width.
        int paintWidth = item.Width;
        if (item.Owner is ToolStripDropDown parent)
            paintWidth = Math.Max(paintWidth, parent.ClientSize.Width - item.Bounds.Left - parent.Padding.Right);
        using var path = VesperDrawing.Rounded(new Rectangle(1, 1,
            Math.Max(1, paintWidth - 2), Math.Max(1, item.Height - 2)), 5);
        using var brush = new SolidBrush(colors.Secondary);
        e.Graphics.FillPath(brush, path);
        e.Graphics.Restore(old);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is ToolStripMenuItem)
        {
            Rectangle bounds = e.TextRectangle;
            bounds.Y = 0;
            bounds.Height = e.Item.Height;
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, bounds,
                e.Item.Enabled ? colors.Text : colors.Muted,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            return;
        }
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(colors.Border);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 10, y, Math.Max(10, e.Item.Width - 10), y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        VesperDrawing.Icon(e.Graphics, VesperIcon.Check,
            new Rectangle(5, (e.Item.Height - 14) / 2, 14, 14), colors.Green);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = colors.Muted;
        base.OnRenderArrow(e);
    }
}
