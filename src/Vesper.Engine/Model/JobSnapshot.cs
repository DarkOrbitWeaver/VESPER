namespace Vesper.Engine;

/// <summary>Immutable view of a job for the UI and the browser bridge.</summary>
public sealed class JobSnapshot
{
    public Guid Id { get; init; }
    public JobKind Kind { get; init; }
    public MediaOutput Output { get; init; }
    public string FileName { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public string? OriginalPageUrl { get; init; }
    public string? MediaProvider { get; init; }
    public string? MediaId { get; init; }
    public string? MediaQuality { get; init; }
    public string? MediaFormatId { get; init; }
    public string? MediaThumbnailUrl { get; init; }
    public DateTimeOffset? MediaExpiresAt { get; init; }
    public string Destination { get; init; } = "";
    public string PartialPath { get; init; } = "";
    public string? FinalPath { get; init; }
    public JobState State { get; init; }
    public string Reason { get; init; } = "";
    public string Message { get; init; } = "";
    // Candidate downloads are not VESPER jobs until Chrome has been safely paused and accepted.
    public bool IsUnacceptedBrowserCapture { get; init; }
    // Export needs to know when a plain link would lose selection details, without exposing the private plan.
    internal bool HasNonPortableMediaSelection { get; init; }
    public long? TotalBytes { get; init; }

    /// <summary>Bytes that survive a crash: validated, flushed, and recorded in the database.</summary>
    public long CommittedBytes { get; init; }
    /// <summary>Committed plus validated bytes waiting for a checkpoint.</summary>
    public long ProgressBytes { get; init; }

    public double SpeedBytesPerSecond { get; init; }
    public TimeSpan? Eta { get; init; }
    public bool EstimatingEta { get; init; }
    public bool WaitingForData { get; init; }
    public int Connections { get; init; }
    public JobPriority Priority { get; init; }
    public long SpeedLimitBytes { get; init; }
    public long QueueOrder { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public DateTime? NextRetryUtc { get; init; }
    public int RetryCount { get; init; }

    /// <summary>Completed job whose file is no longer where it was published. Not a failure.</summary>
    public bool FileMissing { get; init; }
    public bool IsLiveRecording { get; init; }
    public IdentityLevel Identity { get; init; }
    public bool AcceptsRanges { get; init; }
    public string? ContentType { get; init; }
    public string Category { get; init; } = "";
    public RescueMode Rescue { get; init; }
    public bool HasExpectedHash { get; init; }
    public bool HasPieceMap { get; init; }
    public bool Sha256Verified { get; init; }

    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)ProgressBytes / TotalBytes.Value, 0, 1) : null;
    public bool FinishingTransfer => State == JobState.Downloading && !IsLiveRecording && TotalBytes is > 0 && ProgressBytes >= TotalBytes;
    public bool ShowTransferProgress => !FinishingTransfer && State is not (JobState.Verifying or JobState.Finalizing or JobState.Converting or JobState.Completed);
    public bool IsActive => JobStates.IsActive(State);
    public bool IsCleanupPending => DownloadEngine.IsCleanupPending(State, Reason);
    public bool HasRetainedData => CommittedBytes > 0 && State != JobState.Completed;
}
