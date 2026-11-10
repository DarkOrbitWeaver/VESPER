using System.Drawing;

namespace VESPER;

// UI-only data. Nothing here represents a verified file or persisted transfer.
internal enum JobStatus { Downloading, Paused, Queued, Completed, Missing, NeedsLink, Waiting, Verifying, Suspect }
internal enum Density { ExtraCompact, Compact, Comfortable }
internal enum ThemeChoice { System, Light, Dark }
internal enum MotionChoice { Off, Subtle, Botanical }

internal sealed class DownloadItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string FileType { get; set; } = "File";
    public string Extension { get; set; } = "";
    public string Size { get; set; } = "—";
    public string SourceUrl { get; set; } = "";
    public string Destination { get; set; } = "";
    public JobStatus Status { get; set; }
    public int? Percent { get; set; }
    public string Transfer { get; set; } = "";
    public string Eta { get; set; } = "";
    public string Detail { get; set; } = "";
    public string RateCap { get; set; } = "Unlimited";
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;

    // There is deliberately NO HasVerifiedFile, file delete/open, or transfer engine here.
    public static List<DownloadItem> Examples() =>
    [
        new() { Name="Rain in the Forest.mp3", FileType="Audio", Extension="MP3", Size="12.8 MB", Status=JobStatus.Missing, Detail="Sample: previously completed item marked missing" },
        new() { Name="Windows 11 24H2.iso", FileType="Disc image", Extension="ISO", Size="5.41 GB", Status=JobStatus.NeedsLink, Percent=88, Detail="Sample: replacement source would require validation" },
        new() { Name="The Last Man on Earth (1964).mp4", FileType="Video", Extension="MP4", Size="1.57 GB", Status=JobStatus.Queued },
        new() { Name="Landscape_Collection.zip", FileType="Archive", Extension="ZIP", Size="40.7 MB", Status=JobStatus.Downloading, Percent=12, Transfer="128 KB/s", Eta="≈5 min left" },
        new() { Name="Blender-4.0-win64.zip", FileType="Archive", Extension="ZIP", Size="1.91 GB", Status=JobStatus.Completed },
        new() { Name="Moby Dick — Illustrated Edition.epub", FileType="eBook", Extension="EPUB", Size="93.1 MB", Status=JobStatus.Paused, Percent=79 },
        new() { Name="Nature Documentary — Episode 01.mp4", FileType="Video", Extension="MP4", Size="1.26 GB", Status=JobStatus.Downloading, Percent=64, Transfer="7.28 MB/s", Eta="≈1 min left" },
    ];
}

internal sealed class VesperPreferences
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.Light;
    public Density Density { get; set; } = Density.Compact;
    public MotionChoice Motion { get; set; } = MotionChoice.Subtle;
    public bool Botanical { get; set; } = false; // v9 is opt-in only.
    public bool HideMissingCompleted { get; set; }
    public bool CategoryFolders { get; set; }
    public string DefaultFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int ConcurrentTransfers { get; set; } = 3;
    public int GlobalRateLimitKbs { get; set; }
    public int PerTransferRateLimitKbs { get; set; }
    public List<int> BandwidthPresetsKbs { get; set; } = [128, 512, 2000];
    public VesperPreferences Clone()
    {
        var copy = (VesperPreferences)MemberwiseClone();
        copy.BandwidthPresetsKbs = [.. BandwidthPresetsKbs];
        return copy;
    }
}

internal readonly record struct VesperPalette(Color Back, Color Surface, Color Secondary, Color Border, Color Text, Color Muted, Color Green, Color GreenHover, Color MutedGreen, Color Amber, Color Red)
{
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
}

internal static class UiKit
{
    // In the VS WinForms Designer the execution base can be the design host,
    // not the app's bin folder. Resolve once at UI startup; never in Paint.
    public static string Asset(string name)
    {
        string[] roots =
        [
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(UiKit).Assembly.Location) ?? AppContext.BaseDirectory,
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
    public static bool IsDark(VesperPreferences p) => p.Theme == ThemeChoice.Dark ||
        (p.Theme == ThemeChoice.System && SystemInformation.HighContrast == false && IsSystemDark());

    // No registry hacks or dependency; optional OS preference read only.
    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i && i == 0;
        }
        catch { return false; }
    }
    public static VesperPalette Palette(VesperPreferences p) => SystemInformation.HighContrast
        ? new VesperPalette(SystemColors.Window,SystemColors.Window,SystemColors.Control,SystemColors.WindowFrame,
            SystemColors.WindowText,SystemColors.GrayText,SystemColors.Highlight,SystemColors.HotTrack,
            SystemColors.GrayText,SystemColors.Highlight,SystemColors.HotTrack)
        : IsDark(p) ? VesperPalette.Dark : VesperPalette.Light;
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
