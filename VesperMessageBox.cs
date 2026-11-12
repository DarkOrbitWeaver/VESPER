namespace VESPER;

/// <summary>Application messages using the same palette and window chrome as other VESPER dialogs.</summary>
internal static class VesperMessageBox
{
    internal static VesperPalette StartupPalette { get; set; } = UiKit.Palette(new VesperPreferences());

    public static DialogResult Show(string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon) =>
        Show(null, text, title, buttons, icon);

    public static DialogResult Show(IWin32Window? owner, string text, string title, MessageBoxButtons buttons,
        MessageBoxIcon icon, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1,
        string? actionText = null, VesperIcon? symbol = null)
    {
        VesperPalette palette = owner switch
        {
            VesperPopupForm { DialogPalette: { } colors } => colors,
            Form1 main => main.CurrentPalette,
            _ => StartupPalette
        };
        if (palette.HighContrast)
            return MessageBox.Show(owner, text, title, buttons, icon, defaultButton);

        using var font = new Font("Segoe UI", 9F);
        using var dialog = new VesperPopupForm
        {
            Text = title, ClientSize = new Size(470, 230),
            AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi,
            Font = font, BackColor = palette.Surface, ForeColor = palette.Text,
            ShowInTaskbar = false, StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent
        };
        VesperWindowChrome.Brand(dialog);
        var body = new VesperScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = palette.Surface };
        VesperIcon glyph = symbol ?? (icon is MessageBoxIcon.Warning or MessageBoxIcon.Error ? VesperIcon.Warning : VesperIcon.Info);
        const int textWidth = 420;
        var message = new Label { Text = text, Location = new Point(24, 22), MaximumSize = new Size(textWidth, 0),
            AutoSize = true, ForeColor = palette.Text, BackColor = palette.Surface, Font = font, UseMnemonic = false };
        body.Controls.Add(message);
        int measured = message.GetPreferredSize(new Size(textWidth, 0)).Height;
        dialog.ClientSize = new Size(470, Math.Clamp(measured + 46 + 60 + VesperDialogChrome.HeaderHeight, 190, 450));

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = palette.Surface };
        footer.Paint += (_, e) => { using var pen = new Pen(palette.Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        var accept = new VesperButton { Text = actionText ?? (buttons == MessageBoxButtons.YesNo ? "Yes" : "OK"),
            DialogResult = buttons == MessageBoxButtons.YesNo ? DialogResult.Yes : DialogResult.OK,
            Primary = actionText is not null || defaultButton == MessageBoxDefaultButton.Button1 };
        accept.ApplyPalette(palette);
        if (buttons == MessageBoxButtons.YesNo)
        {
            var decline = new VesperButton { Text = actionText is null ? "No" : "Cancel", DialogResult = DialogResult.No,
                Primary = actionText is null && defaultButton == MessageBoxDefaultButton.Button2 };
            decline.ApplyPalette(palette);
            if (actionText is null)
            {
                decline.SetBounds(354, 13, 92, 34);
                accept.SetBounds(252, 13, 92, 34);
            }
            else
            {
                int width = Math.Max(92, TextRenderer.MeasureText(actionText, font).Width + 26);
                accept.SetBounds(446 - width, 13, width, 34);
                decline.SetBounds(accept.Left - 102, 13, 92, 34);
            }
            footer.Controls.Add(decline);
            dialog.CancelButton = decline;
            dialog.AcceptButton = defaultButton == MessageBoxDefaultButton.Button2 ? decline : accept;
            if (defaultButton == MessageBoxDefaultButton.Button2) dialog.Shown += (_, _) => decline.Focus();
        }
        else
        {
            accept.SetBounds(354, 13, 92, 34);
            dialog.AcceptButton = accept;
            dialog.CancelButton = accept;
        }
        footer.Controls.Add(accept);
        dialog.Controls.Add(body);
        dialog.Controls.Add(footer);
        VesperDialogChrome.Attach(dialog, "VESPER  /  " + title, palette, glyph);
        dialog.StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        return owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }
}
