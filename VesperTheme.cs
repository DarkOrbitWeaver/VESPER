using System.Drawing;

namespace VESPER;

internal readonly record struct VesperPalette(Color Back, Color Surface, Color Secondary, Color Border, Color Text, Color Muted, Color Green, Color GreenHover, Color MutedGreen, Color Amber, Color Red)
{
    public bool Botanical { get; init; }
    public bool HighContrast { get; init; }
    public bool IsDark => Surface.GetBrightness() < 0.5f;
    public Color Link => HighContrast ? SystemColors.HotTrack
        : IsDark ? Color.FromArgb(153, 194, 221) : Color.FromArgb(43, 91, 130);
    public Color ActionInk => HighContrast ? SystemColors.HighlightText
        : Green.GetBrightness() >= 0.55f ? Color.FromArgb(25, 39, 27) : Color.FromArgb(255, 251, 240);
    public Color Divider => Botanical ? VesperDrawing.Mix(Surface, Border, 0.45f) : Border;
    public Color ProgressTrack => HighContrast ? SystemColors.WindowText : IsDark ? Color.FromArgb(67, 82, 65) : Border;
    public Color ProgressGreen => HighContrast ? SystemColors.Highlight : IsDark ? Color.FromArgb(177, 220, 97) : Green;
    public Color ProgressAmber => HighContrast ? SystemColors.Highlight : IsDark ? Color.FromArgb(240, 198, 120) : Amber;
    public Color ProgressRed => HighContrast ? SystemColors.Highlight : IsDark ? Color.FromArgb(249, 157, 148) : Red;
    public Color Selection => HighContrast ? SystemColors.Highlight
        : Botanical ? VesperDrawing.Mix(Surface, MutedGreen, IsDark ? 0.17f : 0.16f) : Secondary;

    public static VesperPalette Light => new(
        ColorTranslator.FromHtml("#F8F9F6"), Color.White, ColorTranslator.FromHtml("#F2F4EF"),
        ColorTranslator.FromHtml("#D9DED6"), ColorTranslator.FromHtml("#313934"),
        ColorTranslator.FromHtml("#68746B"), ColorTranslator.FromHtml("#3D694B"),
        ColorTranslator.FromHtml("#28533A"), ColorTranslator.FromHtml("#819888"),
        ColorTranslator.FromHtml("#A9741C"), ColorTranslator.FromHtml("#B25247"));
    public static VesperPalette Dark => new(
        ColorTranslator.FromHtml("#171D19"), ColorTranslator.FromHtml("#1E2520"),
        ColorTranslator.FromHtml("#1B231D"), ColorTranslator.FromHtml("#38453C"),
        ColorTranslator.FromHtml("#DCE3D8"), ColorTranslator.FromHtml("#A3B3A5"),
        ColorTranslator.FromHtml("#84B68A"), ColorTranslator.FromHtml("#A5CFA8"),
        ColorTranslator.FromHtml("#75887A"), ColorTranslator.FromHtml("#DBAE61"),
        ColorTranslator.FromHtml("#DC847B"));

    public static VesperPalette BotanicalLight { get; } = new(
        C("#FAF6ED"), C("#FCF8F0"), C("#F0EDE1"), C("#CDC5B3"), C("#262E22"),
        C("#646557"), C("#3C5736"), C("#2F482B"), C("#62784C"), C("#8B5C17"), C("#A53C34"))
        { Botanical = true };

    public static VesperPalette BotanicalDark { get; } = new(
        C("#1D251E"), C("#222A23"), C("#29332A"), C("#4E5D4B"), C("#F3EDDF"),
        C("#B9C4B3"), C("#416244"), C("#365539"), C("#A7BE91"), C("#E4B96E"), C("#F2A299"))
        { Botanical = true };

    private static Color C(string hex) => ColorTranslator.FromHtml(hex);
}

internal static partial class UiKit
{
    // In the VS WinForms Designer the execution base can be the design host,
    // not the app's bin folder. Resolve once at UI startup; never in Paint.
    public static string Asset(string name)
    {
        string[] roots =
        [
            AppContext.BaseDirectory,
#if !VESPER_SETUP
            Path.GetDirectoryName(typeof(UiKit).Assembly.Location) ?? AppContext.BaseDirectory,
#endif
            Environment.CurrentDirectory
        ];
        foreach (string root in roots)
        {
            DirectoryInfo? dir;
            try { dir = new DirectoryInfo(root); }
            catch { continue; }
            for (int depth = 0; dir is not null && depth < 7; depth++, dir = dir.Parent)
            {
                string direct = Path.Combine(dir.FullName, "Assets", name);
                if (File.Exists(direct)) return direct;
                string project = Path.Combine(dir.FullName, "VESPER", "Assets", name);
                if (File.Exists(project)) return project;
            }
        }
        return Path.Combine(AppContext.BaseDirectory, "Assets", name);
    }
    public static int Scale(Control control, int px) => (int)Math.Round(px * control.DeviceDpi / 96d);
    internal static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i && i == 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    internal static VesperPalette Palette(bool dark, bool botanical)
    {
        if (SystemInformation.HighContrast)
            return new VesperPalette(SystemColors.Window, SystemColors.Window, SystemColors.Control, SystemColors.WindowFrame,
                SystemColors.WindowText, SystemColors.GrayText, SystemColors.Highlight, SystemColors.Highlight,
                SystemColors.GrayText, SystemColors.WindowText, SystemColors.WindowText) { HighContrast = true };
        if (botanical) return dark ? VesperPalette.BotanicalDark : VesperPalette.BotanicalLight;
        return dark ? VesperPalette.Dark : VesperPalette.Light;
    }

    public static Font Heading(VesperPalette palette, float size) => palette.Botanical
        ? new Font("Georgia", size)
        : new Font("Segoe UI", size, FontStyle.Bold);
    public static Image? LoadImage(string name)
    {
        string path = Asset(name);
        if (!File.Exists(path)) return null;
        using var image = Image.FromFile(path);
        return new Bitmap(image); // avoids locking files during app lifetime.
    }
    public static Button Button(string text, VesperPalette palette, bool primary = false)
    {
        var b = new Button { Text=text, AutoSize=false, FlatStyle=FlatStyle.Flat, BackColor=primary ? palette.Green : palette.Surface,
            ForeColor=primary ? Color.White : palette.Text, Font=new Font("Segoe UI", 9F, FontStyle.Regular), Cursor=Cursors.Hand,
            Margin=new Padding(4,0,4,0), UseVisualStyleBackColor=false, TabStop=true };
        b.FlatAppearance.BorderColor=primary ? palette.Green : palette.Border;
        b.FlatAppearance.MouseOverBackColor=primary ? palette.GreenHover : palette.Secondary;
        return b;
    }
    public static Label Label(string text, VesperPalette p, float size = 9, bool bold = false) =>
        new() { Text=text, ForeColor=p.Text, BackColor=Color.Transparent, Font=new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), AutoSize=true };
}
