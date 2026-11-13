using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using VESPER;

namespace Vesper.Setup.Tests;

public sealed class FirstFlightUiTests
{
    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    public void MotionPreferenceRequiresClientAreaAnimationsAndNoHighContrast(
        bool highContrast, bool settingRead, bool animationsEnabled, bool expected) =>
        Assert.Equal(expected, Program.MotionEnabledFor(highContrast, settingRead, animationsEnabled));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeFramesKeepThePublisherRimAndClearTheLastButterflies(bool dark) => OnSta(() =>
    {
        VesperPalette palette = dark ? VesperPalette.BotanicalDark : VesperPalette.BotanicalLight;
        using var canvas = new FirstFlightCanvas(palette);
        using var image = new Bitmap(800, 484);
        using var graphics = Graphics.FromImage(image);
        string theme = dark ? "dark" : "light";
        canvas.Render(graphics, image.Size, null);
        Save(image, "first-flight-" + theme);
        Save(image, "first-flight-" + theme + "-vesper-welcome");

        foreach ((string name, double time) in new (string, double)[]
        {
            ("logo-flash-first", .05),
            ("logo-flash-last", 1.08),
            ("butterfly-swarm", 2.10),
            ("publisher-reveal", 3.30),
            ("publisher-wordmark", 4.25),
            ("transition", 5.50),
        })
        {
            canvas.Render(graphics, image.Size, time);
            Save(image, $"first-flight-{theme}-{name}");
        }

        canvas.Render(graphics, image.Size, 4.98);
        Color band = image.GetPixel(400, 199);
        Color disk = image.GetPixel(400, 175);
        Assert.True(band.GetBrightness() > disk.GetBrightness() + .3f, $"Rim {band}, disk {disk}");
        Save(image, "publisher-" + theme);
        canvas.Render(graphics, image.Size, 2.90);
        AssertBlank(image, palette.Back);
        canvas.Render(graphics, image.Size, 5.90);
        AssertBlank(image, palette.Back);
        Save(image, "first-flight-" + theme + "-blank-transition");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FastInstallOutcomeDoesNotDelayOrCutTheIntro(bool success) => OnSta(() =>
    {
        var paths = new InstallPaths(Path.GetTempPath(), Path.GetTempPath(), Path.GetTempPath());
        using var window = new SetupWindow(SetupMode.Install, paths, VesperPalette.BotanicalDark, (progress, _) =>
        {
            progress.Report(new("Verifying…", 1, 2));
            if (!success) throw new IOException("The test package is damaged.");
        });
        _ = window.Handle;
        Assert.Contains(window.Controls.OfType<Label>(), label => label.Text == "For this Windows account only.");
        var stopwatch = Stopwatch.StartNew();
        Task install = window.RunAttemptAsync(animate: true);
        while (!install.IsCompleted && stopwatch.Elapsed < TimeSpan.FromSeconds(3))
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Assert.True(install.IsCompleted, "The installation outcome was delayed by the intro.");
        install.GetAwaiter().GetResult();
        FirstFlightCanvas canvas = window.Controls.OfType<FirstFlightCanvas>().Single();
        if (success)
        {
            Assert.True(canvas.Playing, "A fast successful install cut off the First Flight sequence.");
            Assert.Equal("Open VESPER", Assert.IsType<VesperButton>(window.AcceptButton).Text);
        }
        else
        {
            Assert.False(canvas.Playing, "A failed install should reveal its error immediately.");
            Assert.Contains(window.Controls.OfType<Label>(), label => label.Text == "The test package is damaged.");
        }
    });

    [Fact]
    public void VisibleSetupWindowCanBeCapturedDuringAndAfterTheIntro()
    {
        if (Environment.GetEnvironmentVariable("VESPER_SETUP_QA") is not { Length: > 0 } folder) return;

        Directory.CreateDirectory(folder);
        OnSta(() =>
        {
            var paths = new InstallPaths(Path.GetTempPath(), Path.GetTempPath(), Path.GetTempPath());
            using var window = new SetupWindow(SetupMode.Install, paths, VesperPalette.BotanicalDark,
                (progress, _) => progress.Report(new("Verifying setup payload", 1, 1)));
            FirstFlightCanvas canvas = window.Controls.OfType<FirstFlightCanvas>().Single();
            using var captureTimer = new System.Windows.Forms.Timer { Interval = 25 };
            var introClock = new Stopwatch();
            bool installCompleted = false;
            bool introCaptured = false;
            Exception? failure = null;

            captureTimer.Tick += (_, _) =>
            {
                try
                {
                    if (introClock.Elapsed > TimeSpan.FromSeconds(9))
                        throw new TimeoutException("The setup intro did not finish within nine seconds.");

                    if (!installCompleted) return;
                    if (!introCaptured && introClock.Elapsed.TotalSeconds >= 2.1)
                    {
                        Assert.True(canvas.Playing, "The completed install did not keep the intro visible.");
                        VesperButton openButton = Assert.IsType<VesperButton>(window.AcceptButton);
                        VesperButton skipButton = window.Controls.OfType<VesperButton>().Single(button => button.Text == "Skip intro");
                        Assert.Equal("Open VESPER", openButton.Text);
                        Assert.True(openButton.Visible, "Open VESPER should be available while the intro continues.");
                        Assert.True(skipButton.Visible, "Skip intro should remain available while the intro continues.");
                        Assert.True(window.Controls.GetChildIndex(canvas) > window.Controls.GetChildIndex(openButton));
                        Assert.True(window.Controls.GetChildIndex(canvas) > window.Controls.GetChildIndex(skipButton));
                        SaveVisibleWindow(window, Path.Combine(folder, "setup-window-fast-complete-intro.png"));
                        introCaptured = true;
                    }

                    if (!introCaptured || canvas.Playing) return;
                    Label completeLabel = window.Controls.OfType<Label>().Single(label => label.Text == "Installation complete");
                    VesperButton completedAction = Assert.IsType<VesperButton>(window.AcceptButton);
                    Assert.Equal("Open VESPER", completedAction.Text);
                    Assert.True(completeLabel.Visible, "The completion status should be visible after the intro.");
                    Assert.True(window.Controls.GetChildIndex(canvas) > window.Controls.GetChildIndex(completeLabel));
                    window.Refresh();
                    completedAction.Refresh();
                    SaveVisibleWindow(window, Path.Combine(folder, "setup-window-post-intro-completion.png"));
                    captureTimer.Stop();
                    window.Close();
                }
                catch (Exception ex)
                {
                    failure = ex;
                    captureTimer.Stop();
                    window.Close();
                }
            };

            window.Shown += (_, _) =>
            {
                introClock.Start();
                captureTimer.Start();
                _ = CompleteInstallAsync();
            };

            async Task CompleteInstallAsync()
            {
                try
                {
                    await window.RunAttemptAsync(animate: true);
                    installCompleted = true;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    window.Close();
                }
            }

            Application.Run(window);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.True(introCaptured, "The intro screenshot was not captured.");
        }, TimeSpan.FromSeconds(15));
    }

    private static void AssertBlank(Bitmap image, Color color)
    {
        for (int y = 0; y < image.Height; y += 7)
            for (int x = 0; x < image.Width; x += 7)
                Assert.Equal(color.ToArgb(), image.GetPixel(x, y).ToArgb());
    }

    private static void Save(Bitmap image, string name)
    {
        if (Environment.GetEnvironmentVariable("VESPER_SETUP_QA") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        image.Save(Path.Combine(folder, name + ".png"), ImageFormat.Png);
    }

    private static void SaveVisibleWindow(Form window, string path)
    {
        using var image = new Bitmap(window.Width, window.Height);
        using Graphics graphics = Graphics.FromImage(image);
        IntPtr hdc = graphics.GetHdc();
        bool captured;
        try { captured = PrintWindow(window.Handle, hdc, 0); }
        finally { graphics.ReleaseHdc(hdc); }
        Assert.True(captured, "Windows could not capture the visible setup window.");
        image.Save(path, ImageFormat.Png);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);

    private static void OnSta(Action action, TimeSpan? timeout = null)
    {
        ExceptionDispatchInfo? error = null;
        var ui = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        Assert.True(ui.Join(timeout ?? TimeSpan.FromSeconds(8)), "The setup UI did not finish.");
        error?.Throw();
    }
}
