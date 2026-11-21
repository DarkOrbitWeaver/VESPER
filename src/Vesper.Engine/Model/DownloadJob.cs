namespace Vesper.Engine;

public enum JobState
{
    Queued = 0,
    Resolving = 1,
    Downloading = 2,
    Paused = 3,
    WaitingForNetwork = 4,
    WaitingForRetry = 5,
    NeedsLink = 6,
    NeedsAttention = 7,
    Verifying = 8,
    Finalizing = 9,
    Completed = 10,
    Failed = 11,
    Suspect = 12,
    Cancelled = 13,
    Converting = 14
}

public enum JobPriority { Low = -1, Normal = 0, High = 1 }
public enum JobKind { Http = 0, Hls = 1, Dash = 2, Merge = 3, BrowserFile = 4 }
public enum MediaOutput { Original = 0, Mp4 = 1, M4a = 2, Opus = 3, Wav = 4, Mp3 = 5 }

public static class DirectMediaPlan
{
    public const string AudioIfPresent = """{"audio":"if-present"}""";
}

/// <summary>How strongly the remote representation can be identified across requests.</summary>
public enum IdentityLevel
{
    /// <summary>No validator at all. Resuming mixes bytes on faith, so it needs explicit approval.</summary>
    None = 0,
    /// <summary>Last-Modified only: acceptable for resuming the same resource, never for a different URL.</summary>
    LastModified = 1,
    /// <summary>A strong ETag (no W/ prefix).</summary>
    StrongEtag = 2
}

public enum RescueMode { None = 0, R0 = 1, R1 = 2, R1b = 3, R4 = 4 }

public static class JobStates
{
    /// <summary>States in which a runner may exist or is about to exist.</summary>
    public static bool IsActive(JobState s) => s is JobState.Resolving or JobState.Downloading
        or JobState.WaitingForNetwork or JobState.WaitingForRetry or JobState.Verifying or JobState.Finalizing or JobState.Converting;

    /// <summary>States from which nothing happens until the user acts.</summary>
    public static bool NeedsUser(JobState s) => s is JobState.Paused or JobState.NeedsLink or JobState.NeedsAttention
        or JobState.Failed or JobState.Suspect or JobState.Cancelled;
}

/// <summary>
/// The durable record of one download. Only the engine mutates it and only the engine
/// writes it to the <see cref="JobStore"/>. The UI sees immutable <see cref="JobSnapshot"/>s.
/// </summary>
public sealed class DownloadJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public JobKind Kind { get; set; }

    public string SourceUrl { get; set; } = "";
    /// <summary>Hash of the final HTTP origin and path that supplied the saved bytes.</summary>
    public string? FinalResourceIdentity { get; set; }
    /// <summary>Links this job used before a Replace Link. Newline separated, newest last.</summary>
    public string PreviousUrls { get; set; } = "";

    public string FileName { get; set; } = "download";
    public bool NameLocked { get; set; }
    public string DestinationFolder { get; set; } = "";
    public string PartialPath { get; set; } = "";
    public string? FinalPath { get; set; }
    /// <summary>Recorded before publishing so a crash during rename can be reconciled.</summary>
    public string? PendingFinalPath { get; set; }
    public long? PendingConversionBytes { get; set; }
    // The existing digest also records publication proof for original files.
    public string? PendingConversionDigest { get; set; }
    public string? PendingConversionIdentity { get; set; }
    public string? PublishedSha256 { get; set; }
    /// <summary>Windows file identity captured when the published output was verified.</summary>
    public string? PublishedFileIdentity { get; set; }
    public string? PartialIdentity { get; set; }

    public long? TotalBytes { get; set; }
    public ExtentSet Extents { get; set; } = new();

    public JobState State { get; set; }
    public string Reason { get; set; } = "";
    public string Message { get; set; } = "";
    public JobPriority Priority { get; set; }
    public long QueueOrder { get; set; }
    public long SpeedLimitBytes { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }

    public int RetryCount { get; set; }
    public DateTime? NextRetryUtc { get; set; }

    // Source identity (see IdentityLevel).
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public bool AcceptsRanges { get; set; }
    public IdentityLevel Identity { get; set; }
    public string? ContentType { get; set; }

    public string Category { get; set; } = "";
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }

    // Non-secret media provenance. Never use this URL for byte-range resume without
    // the existing Rescue identity checks. Nullable for old ordinary downloads.
    public string? OriginalPageUrl { get; set; }
    public string? MediaProvider { get; set; }
    public string? MediaId { get; set; }
    public string? MediaQuality { get; set; }
    public string? MediaFormatId { get; set; }
    public string? MediaThumbnailUrl { get; set; }
    public DateTimeOffset? MediaExpiresAt { get; set; }
    public MediaOutput Output { get; set; }
    public BrowserTransferProof? BrowserProof { get; set; }

    // Integrity and Rescue.
    public string? ExpectedSha256 { get; set; }
    public string? PieceMapJson { get; set; }
    public bool RescueCandidate { get; set; }
    public RescueMode RescueMode { get; set; }
    public bool Sha256Verified { get; set; }
    public bool UnverifiedResumeApproved { get; set; }
    /// <summary>The server transformed the body (Content-Encoding), so byte ranges are meaningless.</summary>
    public bool NonResumable { get; set; }

    /// <summary>HLS cursor etc. Committed in the same transaction as the extents.</summary>
    public string? MediaState { get; set; }

    /// <summary>Versioned media plan or selected clip range. No cookies, keys or authorization headers.</summary>
    public string? MediaPlanJson { get; set; }

    /// <summary>Bounded source-provided audio tags used when finalizing a directly downloaded MP3.</summary>
    public string? MediaMetadataJson { get; set; }

    /// <summary>Incremented by every durable checkpoint; also written to the recovery manifest.</summary>
    public long Generation { get; set; }

    /// <summary>
    /// Stage scalar UI changes without replacing the live job or copying its concurrently updated extents.
    /// Only side-effect-free scalar mutations are allowed; checkpoints exclusively own Extents.
    /// </summary>
    internal DownloadJob CopyForUiMutation() => (DownloadJob)MemberwiseClone();

    /// <summary>Prepare a detached candidate so failed durable updates leave the live job untouched.</summary>
    internal DownloadJob CopyForUpdate()
    {
        var copy = (DownloadJob)MemberwiseClone();
        copy.Extents = Extents.Clone();
        return copy;
    }

    /// <summary>
    /// Publish exactly the staged state after a successful database commit, without replacing
    /// the DownloadJob referenced by a running session or invoking a mutation twice.
    /// Keep this list synchronized with public mutable DownloadJob fields.
    /// </summary>
    internal void ApplyPersistedState(DownloadJob saved)
    {
        if (saved.Id != Id) throw new InvalidOperationException("Cannot copy a different download's state.");
        Kind = saved.Kind;
        SourceUrl = saved.SourceUrl;
        FinalResourceIdentity = saved.FinalResourceIdentity;
        PreviousUrls = saved.PreviousUrls;
        FileName = saved.FileName;
        NameLocked = saved.NameLocked;
        DestinationFolder = saved.DestinationFolder;
        PartialPath = saved.PartialPath;
        FinalPath = saved.FinalPath;
        PendingFinalPath = saved.PendingFinalPath;
        PendingConversionBytes = saved.PendingConversionBytes;
        PendingConversionDigest = saved.PendingConversionDigest;
        PendingConversionIdentity = saved.PendingConversionIdentity;
        PublishedSha256 = saved.PublishedSha256;
        PublishedFileIdentity = saved.PublishedFileIdentity;
        PartialIdentity = saved.PartialIdentity;
        TotalBytes = saved.TotalBytes;
        Extents = saved.Extents;
        State = saved.State;
        Reason = saved.Reason;
        Message = saved.Message;
        Priority = saved.Priority;
        QueueOrder = saved.QueueOrder;
        SpeedLimitBytes = saved.SpeedLimitBytes;
        CreatedUtc = saved.CreatedUtc;
        UpdatedUtc = saved.UpdatedUtc;
        CompletedUtc = saved.CompletedUtc;
        RetryCount = saved.RetryCount;
        NextRetryUtc = saved.NextRetryUtc;
        ETag = saved.ETag;
        LastModified = saved.LastModified;
        AcceptsRanges = saved.AcceptsRanges;
        Identity = saved.Identity;
        ContentType = saved.ContentType;
        Category = saved.Category;
        Referer = saved.Referer;
        UserAgent = saved.UserAgent;
        OriginalPageUrl = saved.OriginalPageUrl;
        MediaProvider = saved.MediaProvider;
        MediaId = saved.MediaId;
        MediaQuality = saved.MediaQuality;
        MediaFormatId = saved.MediaFormatId;
        MediaThumbnailUrl = saved.MediaThumbnailUrl;
        MediaExpiresAt = saved.MediaExpiresAt;
        Output = saved.Output;
        BrowserProof = saved.BrowserProof;
        ExpectedSha256 = saved.ExpectedSha256;
        PieceMapJson = saved.PieceMapJson;
        RescueCandidate = saved.RescueCandidate;
        RescueMode = saved.RescueMode;
        Sha256Verified = saved.Sha256Verified;
        UnverifiedResumeApproved = saved.UnverifiedResumeApproved;
        NonResumable = saved.NonResumable;
        MediaState = saved.MediaState;
        MediaPlanJson = saved.MediaPlanJson;
        MediaMetadataJson = saved.MediaMetadataJson;
        Generation = saved.Generation;
    }

    public DownloadJob CloneForCommit(ExtentSet extents, string? mediaState)
    {
        var copy = (DownloadJob)MemberwiseClone();
        copy.Extents = extents;
        copy.MediaState = mediaState;
        copy.Generation = Generation + 1;
        return copy;
    }
}
