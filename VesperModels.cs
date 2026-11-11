using System.Drawing;
using Vesper.Engine;

namespace VESPER;

internal enum JobStatus
{
    Downloading, Paused, Queued, Completed, Missing, NeedsLink, Waiting, Verifying, Suspect,
    Resolving, WaitingRetry, NeedsAttention, Processing, Failed, Cancelled
}
internal enum Density { ExtraCompact, Compact, Comfortable, Gallery }
internal enum ThemeChoice { System, Light, Dark }
internal enum MotionChoice { Off, Subtle, Botanical }
internal enum BrowserAction { Ask, Download, Queue }

/// <summary>
/// What one list row shows. Built from an engine snapshot on the UI thread; every value is either taken from
/// the engine or left empty. Nothing here is persisted and nothing is estimated.
/// </summary>
internal sealed class DownloadItem
{
    public Guid Id { get; init; }
    public JobSnapshot? Snapshot { get; private set; }
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
    public DateTime CreatedUtc { get; private set; } = DateTime.UtcNow;
    public long SizeBytes { get; private set; }

    public static DownloadItem From(JobSnapshot s)
    {
        string extension = Path.GetExtension(s.FileName).TrimStart('.').ToUpperInvariant();
        return new DownloadItem
        {
            Id = s.Id, Snapshot = s, Name = s.FileName, Extension = extension, FileType = TypeOf(extension, s.MediaQuality, s.ContentType),
            Size = s.TotalBytes is long total ? Formatting.Bytes(total)
                : s.State == JobState.Completed ? Formatting.Bytes(s.CommittedBytes) : "Unknown",
            SizeBytes = s.TotalBytes ?? s.CommittedBytes,
            SourceUrl = s.SourceUrl, Destination = s.Destination, Status = Map(s), CreatedUtc = s.CreatedUtc,
            Percent = s.State == JobState.Completed || s.ProgressBytes == 0 ? null : s.Fraction is double f ? (int)Math.Floor(f * 100) : null,
            Transfer = TransferText(s), Eta = s.Eta is { } eta ? Formatting.Eta(eta) : "",
            Detail = s.Message, RateCap = Formatting.RateLimit(s.SpeedLimitBytes)
        };
    }

    /// <summary>Refreshes this row in place so the grid keeps its references and only repaints.</summary>
    public void UpdateFrom(JobSnapshot s)
    {
        DownloadItem n = From(s);
        (Snapshot, Name, Extension, FileType, Size, SizeBytes, SourceUrl, Destination, Status, Percent, Transfer, Eta, Detail, RateCap)
            = (n.Snapshot, n.Name, n.Extension, n.FileType, n.Size, n.SizeBytes, n.SourceUrl, n.Destination, n.Status, n.Percent, n.Transfer, n.Eta, n.Detail, n.RateCap);
    }

    private static JobStatus Map(JobSnapshot s) => s.State switch
    {
        JobState.Queued => JobStatus.Queued,
        JobState.Resolving => JobStatus.Resolving,
        JobState.Downloading => JobStatus.Downloading,
        JobState.Paused => JobStatus.Paused,
        JobState.WaitingForNetwork => JobStatus.Waiting,
        JobState.WaitingForRetry => JobStatus.WaitingRetry,
        JobState.NeedsLink => JobStatus.NeedsLink,
        JobState.NeedsAttention => JobStatus.NeedsAttention,
        JobState.Verifying => JobStatus.Verifying,
        JobState.Finalizing => JobStatus.Processing,
        JobState.Completed => s.FileMissing ? JobStatus.Missing : JobStatus.Completed,
        JobState.Failed => JobStatus.Failed,
        JobState.Suspect => JobStatus.Suspect,
        _ => JobStatus.Cancelled
    };

    private static string TransferText(JobSnapshot s) => s.State switch
    {
        JobState.Downloading when s.SpeedBytesPerSecond > 0 => Formatting.Rate(s.SpeedBytesPerSecond),
        JobState.Downloading => s.Message,
        JobState.Completed when s.FileMissing => "File not found",
        JobState.Completed => s.CompletedUtc is { } done ? "Finished · " + done.ToLocalTime().ToString("MMM d, HH:mm") : "",
        JobState.WaitingForRetry or JobState.WaitingForNetwork => s.Message,
        _ => ""
    };

    private static string TypeOf(string extension, string? quality, string? contentType)
    {
        if (quality?.StartsWith("audio", StringComparison.OrdinalIgnoreCase) == true) return "Audio";
        if (contentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true) return "Audio";
        if (contentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true) return "Video";
        if (extension is "WEBM" or "MKV" or "MP4")
            return quality is not null && System.Text.RegularExpressions.Regex.IsMatch(quality, @"\b\d{3,4}p\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "Video" : "Media";
        return extension switch
        {
            "MP3" or "WAV" or "FLAC" or "M4A" or "OGG" or "AAC" or "WEBA" => "Audio",
            "AVI" or "MOV" or "TS" or "M4V" => "Video",
            "ZIP" or "RAR" or "7Z" or "GZ" or "TAR" or "XZ" => "Archive",
            "ISO" or "IMG" => "Disc image",
            "EPUB" => "eBook",
            "PDF" or "DOC" or "DOCX" or "ODT" or "RTF" or "TXT" => "Document",
            _ => "File"
        };
    }

    /// <summary>Shown only inside the Visual Studio designer so the list is not empty there.</summary>
    public static List<DownloadItem> DesignerSamples() =>
    [
        new() { Name = "Sample video.mp4", FileType = "Video", Extension = "MP4", Size = "1.26 GB", Status = JobStatus.Downloading, Percent = 64, Transfer = "7.28 MB/s", Eta = "1 min left" },
        new() { Name = "Sample archive.zip", FileType = "Archive", Extension = "ZIP", Size = "40.7 MB", Status = JobStatus.Completed }
    ];
}

internal sealed class VesperPreferences
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.Light;
    public Density Density { get; set; } = Density.Compact;
    public MotionChoice Motion { get; set; } = MotionChoice.Subtle;
    public bool Botanical { get; set; } = true; // New installs start with the illustrated header.
    public bool HideMissingCompleted { get; set; }
    public bool CategoryFolders { get; set; }
    public string DefaultFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int ConcurrentTransfers { get; set; } = 3;
    public int GlobalRateLimitKbs { get; set; }
    public int PerTransferRateLimitKbs { get; set; }
    public List<int> BandwidthPresetsKbs { get; set; } = [128, 512, 2000];
    public int MaxConnectionsPerJob { get; set; } = 4;
    public bool NotifyOnFinish { get; set; } = true;
    public bool WatchClipboard { get; set; }
    public bool PreventSleep { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool AllowPrivateNetworks { get; set; }
    public string ProxyMode { get; set; } = "Direct";
    public string ProxyAddress { get; set; } = "";
    public bool ScheduleEnabled { get; set; }
    public string ScheduleStart { get; set; } = "01:00";
    public string ScheduleEnd { get; set; } = "07:00";
    public BrowserAction BrowserAction { get; set; } = BrowserAction.Ask;
    public VesperPreferences Clone()
    {
        var copy = (VesperPreferences)MemberwiseClone();
        copy.BandwidthPresetsKbs = [.. BandwidthPresetsKbs];
        return copy;
    }
}

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
    public static VesperPalette Palette(VesperPreferences p)
    {
        if (SystemInformation.HighContrast)
            return new VesperPalette(SystemColors.Window, SystemColors.Window, SystemColors.Control, SystemColors.WindowFrame,
                SystemColors.WindowText, SystemColors.GrayText, SystemColors.Highlight, SystemColors.Highlight,
                SystemColors.GrayText, SystemColors.WindowText, SystemColors.WindowText) { HighContrast = true };
        bool dark = IsDark(p);
        if (p.Botanical) return dark ? VesperPalette.BotanicalDark : VesperPalette.BotanicalLight;
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
