using System.Drawing;
using Vesper.Engine;

namespace VESPER;

internal enum JobStatus
{
    Downloading, Paused, Queued, Completed, Missing, NeedsLink, Waiting, Verifying, Suspect,
    Resolving, WaitingRetry, NeedsAttention, Processing, Failed, Cancelled, Converting
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
    public BrowserActiveFile? BrowserFile { get; private set; }
    public string? BrowserProfile { get; private set; }
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
    private string _searchAddress = "";

    public static DownloadItem From(JobSnapshot s) => From(s, PublicSearchAddress(s));

    private static DownloadItem From(JobSnapshot s, string searchAddress)
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
            Transfer = TransferText(s), Eta = s.Eta is { } eta ? Formatting.Eta(eta)
                : s.EstimatingEta && !s.WaitingForData ? "Estimating…" : "",
            Detail = s.Message, RateCap = Formatting.RateLimit(s.SpeedLimitBytes), _searchAddress = searchAddress
        };
    }

    /// <summary>Refreshes this row in place so the grid keeps its references and only repaints.</summary>
    public void UpdateFrom(JobSnapshot s)
    {
        string searchAddress = Snapshot is not null && Snapshot.SourceUrl == s.SourceUrl
            && Snapshot.OriginalPageUrl == s.OriginalPageUrl ? _searchAddress : PublicSearchAddress(s);
        DownloadItem n = From(s, searchAddress);
        (Snapshot, Name, Extension, FileType, Size, SizeBytes, SourceUrl, Destination, Status, Percent, Transfer, Eta, Detail, RateCap)
            = (n.Snapshot, n.Name, n.Extension, n.FileType, n.Size, n.SizeBytes, n.SourceUrl, n.Destination, n.Status, n.Percent, n.Transfer, n.Eta, n.Detail, n.RateCap);
        _searchAddress = n._searchAddress;
    }

    public bool MatchesSearch(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return true;
        if (Contains(Name) || Contains(Extension) || Contains(FileType) || Contains(Status.ToString())) return true;

        JobSnapshot? snapshot = Snapshot;
        if (snapshot is null) return false;
        if (Contains(snapshot.Category) || Contains(snapshot.MediaProvider) || Contains(snapshot.MediaId)
            || Contains(snapshot.MediaQuality) || Contains(snapshot.MediaFormatId) || Contains(snapshot.ContentType)
            || Contains(snapshot.Reason))
            return true;

        return Contains(_searchAddress);

        bool Contains(string? value) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string PublicSearchAddress(JobSnapshot snapshot)
    {
        string page = snapshot.OriginalPageUrl ?? snapshot.SourceUrl;
        if (!Uri.TryCreate(page, UriKind.Absolute, out Uri? uri)) return "";

        // Signed query strings can contain credentials. Search only the public host and path.
        return uri.Host + uri.AbsolutePath;
    }

    public void UpdateFromBrowser(string profile, BrowserActiveFile file)
    {
        BrowserProfile = profile;
        BrowserFile = file;
        Name = file.Name;
        Extension = Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
        FileType = TypeOf(Extension, null, null);
        CreatedUtc = file.StartedUtc;
        SizeBytes = file.Length ?? file.Received;
        Size = file.Length is long length ? Formatting.Bytes(length) : "Unknown";
        Status = file.Paused ? JobStatus.Paused : JobStatus.Downloading;
        Percent = file.Length is > 0 ? (int)Math.Clamp(100d * file.Received / file.Length.Value, 0, 99) : null;
        Transfer = file.Received > 0 ? Formatting.Bytes(file.Received) + " received" : "Waiting for data…";
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
        JobState.Verifying when s.Reason == "prepare-media" => JobStatus.Processing,
        JobState.Verifying => JobStatus.Verifying,
        JobState.Finalizing => JobStatus.Processing,
        JobState.Converting => JobStatus.Converting,
        JobState.Completed => s.FileMissing ? JobStatus.Missing : JobStatus.Completed,
        JobState.Failed => JobStatus.Failed,
        JobState.Suspect => JobStatus.Suspect,
        _ => JobStatus.Cancelled
    };

    private static string TransferText(JobSnapshot s) => s.State switch
    {
        JobState.Downloading when s.IsLiveRecording => s.Message,
        JobState.Downloading when s.WaitingForData => "Waiting for data…",
        JobState.Downloading when s.SpeedBytesPerSecond > 0 => Formatting.Rate(s.SpeedBytesPerSecond),
        JobState.Downloading => s.Message,
        JobState.Completed when s.FileMissing => "File not found",
        JobState.Completed when s.Kind == JobKind.BrowserFile => "Saved by Chrome",
        JobState.Completed when s.Reason == "saved-original" => "Saved original format",
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
    public bool PlayCompletionSound { get; set; } = true;
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

internal static partial class UiKit
{
    public static bool IsDark(VesperPreferences p) => p.Theme == ThemeChoice.Dark ||
        (p.Theme == ThemeChoice.System && !SystemInformation.HighContrast && IsSystemDark());

    public static VesperPalette Palette(VesperPreferences p) => Palette(IsDark(p), p.Botanical);
}
