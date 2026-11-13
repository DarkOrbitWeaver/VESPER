using System.Reflection;
using System.Runtime.ExceptionServices;
using VESPER;

namespace Vesper.Windows.Tests;

public sealed class SettingsAccessibilityTests
{
    [Fact]
    public void CheckboxPaintClearsTextLeftByAnEarlierCaption()
    {
        OnSta(() =>
        {
            VesperPalette palette = VesperPalette.Light;
            using var parent = new Panel { BackColor = palette.Surface };
            using var checkbox = new VesperCheckBox(palette)
            {
                Text = "Short label",
                Font = new Font("Segoe UI", 9F),
                Size = new Size(480, 25),
                AutoSize = false,
                BackColor = palette.Surface,
                ForeColor = palette.Text
            };
            parent.Controls.Add(checkbox);

            using var bitmap = new Bitmap(checkbox.Width, checkbox.Height);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.Clear(palette.Surface);
            TextRenderer.DrawText(graphics, "A much longer checkbox caption that occupied this area before",
                checkbox.Font, new Rectangle(22, 0, checkbox.Width - 22, checkbox.Height), palette.Text,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | VesperDrawing.PaintTextFlags);

            var paint = new PaintEventArgs(graphics, checkbox.ClientRectangle);
            MethodInfo onPaint = typeof(VesperCheckBox).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!;
            onPaint.Invoke(checkbox, [paint]);

            bool staleTextRemains = false;
            for (int y = 0; y < bitmap.Height && !staleTextRemains; y++)
            for (int x = 120; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).ToArgb() != palette.Surface.ToArgb())
                {
                    staleTextRemains = true;
                    break;
                }
            }

            Assert.False(staleTextRemains, "The checkbox left pixels from the previous caption in its opaque paint area.");
        });
    }

    [Fact]
    public void SettingsPagesDoNotScrollHorizontallyAtMinimumWidth()
    {
        OnSta(() =>
        {
            using var form = new SettingsForm();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-form.Width - 80, -form.Height - 80);
            form.Show();
            form.ClientSize = new Size(700, 560);
            Application.DoEvents();

            MethodInfo showPage = typeof(SettingsForm).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (string name in new[] { "Downloads", "Transfers", "Network", "Appearance", "Browser" })
            {
                showPage.Invoke(form, [name]);
                Application.DoEvents();
                VesperScrollPanel page = Descendants(form).OfType<VesperScrollPanel>().Single(panel => panel.Visible);
                Assert.False(page.HorizontalScroll.Visible, $"{name} extends beyond the minimum-width viewport.");
            }
        });
    }

    [Fact]
    public void VisibleSettingsControlsDoNotOverlapAtMinimumWidth()
    {
        OnSta(() =>
        {
            using var form = new SettingsForm();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-form.Width - 80, -form.Height - 80);
            form.Show();
            form.ClientSize = new Size(700, 560);
            Application.DoEvents();

            MethodInfo showPage = typeof(SettingsForm).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (string name in new[] { "Downloads", "Transfers", "Network", "Appearance", "Browser" })
            {
                showPage.Invoke(form, [name]);
                Application.DoEvents();
                VesperScrollPanel page = Descendants(form).OfType<VesperScrollPanel>().Single(panel => panel.Visible);
                Control[] controls = page.Controls.Cast<Control>()
                    .Where(control => control.Visible && control.Width > 0 && control.Height > 0).ToArray();

                for (int i = 0; i < controls.Length; i++)
                {
                    for (int j = i + 1; j < controls.Length; j++)
                    {
                        Assert.False(controls[i].Bounds.IntersectsWith(controls[j].Bounds),
                            $"{name}: '{controls[i].Text}' overlaps '{controls[j].Text}'.");
                    }
                }

                if (name == "Network")
                {
                    string[] noteText =
                    [
                        "Downloads use parts when supported; four is a good default.",
                        "Downloads wait outside these hours; active downloads pause.",
                        "Direct is fine for most people. Use a proxy if needed."
                    ];
                    foreach (string text in noteText)
                    {
                        Label note = page.Controls.OfType<Label>().Single(label => label.Text == text);
                        int textWidth = TextRenderer.MeasureText(text, note.Font, new Size(1000, 100),
                            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                        Assert.True(textWidth <= note.Width, $"Network note is clipped: '{text}'.");
                    }
                }
            }
        });
    }

    [Fact]
    public void SettingsChoicesExposeTheirNamesRoleAndCurrentValue()
    {
        OnSta(() =>
        {
            using var form = new SettingsForm();
            _ = form.Handle;
            VesperChoice[] choices = Descendants(form).OfType<VesperChoice>().ToArray();

            string[] expectedNames =
            [
                "Simultaneous downloads", "Preset 1 unit", "Preset 2 unit", "Preset 3 unit",
                "Download connections", "Proxy mode", "When the browser captures a download",
                "Color theme", "Download row height"
            ];
            Assert.Equal(expectedNames.Length, choices.Length);
            foreach (string name in expectedNames)
                Assert.Contains(choices, choice => choice.AccessibilityObject.Name == name);

            foreach (VesperChoice choice in choices)
            {
                Assert.Equal(AccessibleRole.ComboBox, choice.AccessibilityObject.Role);
                Assert.Equal(choice.AccessibleName, choice.AccessibilityObject.Name);
                Assert.Equal(choice.SelectedText, choice.AccessibilityObject.Value);
            }

            VesperChoice theme = choices.Single(choice => choice.AccessibleName == "Color theme");
            theme.AccessibilityObject.Value = "Dark";
            Assert.Equal("Dark", theme.SelectedText);
        });
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)), "The settings UI test did not finish.");
        error?.Throw();
    }
}
