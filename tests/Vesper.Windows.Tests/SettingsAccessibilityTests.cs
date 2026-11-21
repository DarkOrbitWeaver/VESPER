using System.Reflection;
using System.Runtime.ExceptionServices;
using Vesper.Engine;
using VESPER;

namespace Vesper.Windows.Tests;

public sealed class SettingsAccessibilityTests
{
    [Fact]
    public void InternetShortcutDropReadsOnlyBoundedWebAddresses()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vesper-{Guid.NewGuid():N}.url");
        try
        {
            File.WriteAllText(path, "[InternetShortcut]\r\nURL=https://example.com/file.zip?token=short\r\n");
            Assert.Equal("https://example.com/file.zip?token=short", Form1.ReadInternetShortcut(path));

            File.WriteAllText(path, "[InternetShortcut]\r\nURL=javascript:alert(1)\r\n");
            Assert.Null(Form1.ReadInternetShortcut(path));

            File.WriteAllText(path, "URL=https://example.com/" + new string('a', 32 * 1024));
            Assert.Null(Form1.ReadInternetShortcut(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

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
            using var form = new SettingsForm(new VesperPreferences(), importDownloadList: _ => { }, exportDownloadList: _ => { });
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-form.Width - 80, -form.Height - 80);
            form.Show();
            form.ClientSize = new Size(UiKit.Scale(form, 700), UiKit.Scale(form, 560));
            Application.DoEvents();

            MethodInfo showPage = typeof(SettingsForm).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (string name in new[] { "Downloads", "Transfers", "Network", "Appearance", "Sites", "Browser", "About" })
            {
                showPage.Invoke(form, [name]);
                Application.DoEvents();
                VesperScrollPanel page = Descendants(form).OfType<VesperScrollPanel>().Single(panel => panel.Visible);
                Assert.False(page.HorizontalScroll.Visible, $"{name} extends beyond the minimum-width viewport.");
            }
        });
    }

    [Fact]
    public void DownloadListButtonsUseSettingsAsTheirDialogOwner()
    {
        OnSta(() =>
        {
            IWin32Window? importOwner = null;
            IWin32Window? exportOwner = null;
            using var form = new SettingsForm(new VesperPreferences(),
                importDownloadList: owner => importOwner = owner,
                exportDownloadList: owner => exportOwner = owner);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-form.Width - 80, -form.Height - 80);
            form.Show();

            var buttons = Descendants(form).OfType<VesperButton>().Where(button => button.Visible).ToArray();
            Assert.Single(buttons, button => button.Text == "Import list…").PerformClick();
            Assert.Single(buttons, button => button.Text == "Export list…").PerformClick();

            Assert.Same(form, importOwner);
            Assert.Same(form, exportOwner);
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
            form.ClientSize = new Size(UiKit.Scale(form, 700), UiKit.Scale(form, 560));
            Application.DoEvents();

            MethodInfo showPage = typeof(SettingsForm).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (string name in new[] { "Downloads", "Transfers", "Network", "Appearance", "Sites", "Browser", "About" })
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
                "Connections per download", "Proxy mode", "When Chrome sends a file",
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

    [Fact]
    public void About_exposes_diagnostics_export_only_with_a_running_engine()
    {
        OnSta(() =>
        {
            using (var preview = new SettingsForm())
            {
                _ = preview.Handle;
                VesperButton button = Descendants(preview).OfType<VesperButton>().Single(item => item.Name == "exportDiagnostics");
                Assert.Equal("Export diagnostics bundle", button.AccessibleName);
                Assert.False(button.Enabled);
            }

            string root = Path.Combine(Path.GetTempPath(), "vesper-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using var engine = DownloadEngine.Start(new EngineOptions
                {
                    DataDirectory = Path.Combine(root, "data"),
                    DefaultFolder = root,
                    MaxConcurrentJobs = 0
                });
                using var settings = new SettingsForm(new VesperPreferences(), engine);
                _ = settings.Handle;
                VesperButton button = Descendants(settings).OfType<VesperButton>().Single(item => item.Name == "exportDiagnostics");
                Assert.True(button.Enabled);
                Assert.Equal(AccessibleRole.PushButton, button.AccessibilityObject.Role);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    [Fact]
    public void Site_rules_can_be_added_and_removed_in_settings()
    {
        OnSta(() =>
        {
            using var form = new SettingsForm();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-form.Width - 80, -form.Height - 80);
            form.Show();
            var buttons = Descendants(form).OfType<VesperButton>().ToArray();
            Assert.DoesNotContain(buttons, button => button.Name == "navigationSites");
            Assert.Single(buttons, button => button.Name == "navigationBrowser").PerformClick();
            Assert.Single(buttons, button => button.Text == "Site rules…").PerformClick();
            Application.DoEvents();
            TextBox host = Descendants(form).OfType<TextBox>().Single(control => control.Name == "siteHostInput");
            CheckBox audio = Descendants(form).OfType<CheckBox>().Single(control => control.Name == "siteAlwaysAudio");
            VesperButton save = Descendants(form).OfType<VesperButton>().Single(control => control.Name == "saveSiteRule");
            VesperButton remove = Descendants(form).OfType<VesperButton>().Single(control => control.Name == "removeSiteRule");

            host.Text = "BÜCHER.Example.";
            audio.Checked = true;
            save.PerformClick();

            SiteRule saved = Assert.Single(form.Result.SiteRules);
            Assert.Equal("xn--bcher-kva.example", saved.Host);
            Assert.True(saved.AlwaysAudio);
            Assert.True(remove.Enabled);

            remove.PerformClick();
            Assert.Empty(form.Result.SiteRules);
            Assert.Single(buttons, button => button.Text == "Back to Browser").PerformClick();
            Assert.False(host.Visible);
        });
    }

    [Fact]
    public void Site_audio_rule_selects_audio_in_the_native_media_picker()
    {
        OnSta(() =>
        {
            var preferences = new VesperPreferences
            {
                SiteRules = [new SiteRule { Host = "video.example", AlwaysAudio = true }]
            };
            using var picker = new MediaDiscoveryForm(preferences, new Uri("https://video.example/watch"));
            FieldInfo selectedPreset = typeof(MediaDiscoveryForm).GetField("_presetIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;

            Assert.Equal(3, (int)selectedPreset.GetValue(picker)!);
        });
    }

    [Fact]
    public void Add_download_suggests_site_folder_for_single_or_same_host_urls_but_not_mixed_hosts()
    {
        string globalFolder = Path.Combine(Path.GetTempPath(), "vesper-global-downloads");
        string siteFolder = Path.Combine(Path.GetTempPath(), "vesper-media-downloads");
        var preferences = new VesperPreferences
        {
            DefaultFolder = globalFolder,
            SiteRules = [new SiteRule { Host = "media.example", Folder = siteFolder }]
        };

        OnSta(() =>
        {
            using var single = new AddDownloadForm(preferences, "https://media.example/a.zip");
            Assert.Equal(siteFolder, DestinationInput(single).Text);

            using var sameHostBatch = new AddDownloadForm(preferences,
                "https://media.example/a.zip\nhttps://media.example/b.zip");
            Assert.Equal(siteFolder, DestinationInput(sameHostBatch).Text);

            using var mixedHostBatch = new AddDownloadForm(preferences,
                "https://media.example/a.zip\nhttps://other.example/b.zip");
            Assert.Equal(globalFolder, DestinationInput(mixedHostBatch).Text);

            TextBox destination = DestinationInput(single);
            TextBox url = Descendants(single).OfType<TextBox>().Single(control => control.Name == "downloadUrl");
            string chosen = Path.Combine(Path.GetTempPath(), "vesper-one-time-folder");
            destination.Text = chosen;
            url.Text = "https://other.example/next.zip";
            Assert.Equal(chosen, destination.Text);
        });

        static TextBox DestinationInput(AddDownloadForm form) =>
            Descendants(form).OfType<TextBox>().Single(control => control.Name == "destinationFolder");
    }

    [Fact]
    public void Filename_template_setting_reaches_the_running_engine()
    {
        string root = Path.Combine(Path.GetTempPath(), "vesper-template-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var engine = DownloadEngine.Start(new EngineOptions
            {
                DataDirectory = Path.Combine(root, "data"),
                DefaultFolder = root
            });
            var current = new VesperPreferences();
            Form1.CopyPreferences(new VesperPreferences { FileNameTemplate = "{name} - {host}" }, current);

            EngineHost.Apply(engine, current);

            Assert.Equal("{name} - {host}", engine.Options.FileNameTemplate);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
