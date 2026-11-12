using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using VESPER;

namespace Vesper.Setup.Tests;

public sealed class FirstFlightUiTests
{
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
        canvas.Render(graphics, image.Size, 4.98);
        Color band = image.GetPixel(400, 199);
        Color disk = image.GetPixel(400, 175);
        Assert.True(band.GetBrightness() > disk.GetBrightness() + .3f, $"Rim {band}, disk {disk}");
        Save(image, "publisher-" + theme);
        canvas.Render(graphics, image.Size, 2.90);
        AssertBlank(image, palette.Back);
        canvas.Render(graphics, image.Size, 5.90);
        AssertBlank(image, palette.Back);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealOutcomeInterruptsTheIntro(bool success) => OnSta(() =>
    {
        var paths = new InstallPaths(Path.GetTempPath(), Path.GetTempPath(), Path.GetTempPath());
        using var window = new SetupWindow(SetupMode.Install, paths, VesperPalette.BotanicalDark, (progress, _) =>
        {
            progress.Report(new("Verifying…", 1, 2));
            if (!success) throw new IOException("The test package is damaged.");
        });
        _ = window.Handle;
        var stopwatch = Stopwatch.StartNew();
        Task install = window.RunAttemptAsync(animate: true);
        while (!install.IsCompleted && stopwatch.Elapsed < TimeSpan.FromSeconds(3))
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Assert.True(install.IsCompleted, "The installation outcome was delayed by the intro.");
        install.GetAwaiter().GetResult();
        Assert.False(window.Controls.OfType<FirstFlightCanvas>().Single().Playing);
        Assert.Contains(window.Controls.OfType<Label>(), label => label.Text ==
            (success ? "Installation complete" : "The test package is damaged."));
    });

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

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var ui = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        Assert.True(ui.Join(TimeSpan.FromSeconds(8)), "The setup UI did not finish.");
        error?.Throw();
    }
}
