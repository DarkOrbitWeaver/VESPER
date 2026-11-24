using System.Security.Cryptography;
using System.Text;
using Vesper.Engine;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;

namespace VESPER;

/// <summary>
/// Gets a fresh set of links for a media download whose signed URLs expired, without losing the saved bytes.
/// Re-resolves the original page, rebuilds the plan for exactly the formats the job was started with, and
/// lets the engine decide whether the new plan continues the old data (same ids, sizes and identities).
/// Used by the Rescue window and automatically when a supported media link expires.
/// </summary>
internal static class MediaSourceRescue
{
    private sealed record AttemptWindow(DateTime StartedUtc, HashSet<string> Sources);
    private static readonly Dictionary<Guid, AttemptWindow> AutomaticAttempts = [];
    private const int MaxTrackedJobs = 1024;
    private const int MaxSourcesPerHour = 3;
    private static readonly SemaphoreSlim AutomaticSlots = new(3, 3);

    public static bool CanRefresh(JobSnapshot job) =>
        (job.Kind is JobKind.Http or JobKind.Merge or JobKind.Dash or JobKind.Hls)
        && !(job.Kind == JobKind.Hls && job.MediaFormatId?.StartsWith("subtitle:", StringComparison.Ordinal) == true)
        && job.OriginalPageUrl is not null && job.MediaId is not null && job.MediaFormatId is not null;

    /// <summary>At most three distinct failed delivery routes per hour; never loop on the same address.</summary>
    public static bool ShouldTryAutomatically(JobSnapshot job)
    {
        if (!CanRefresh(job)) return false;
        if (job.State != JobState.NeedsLink || job.Reason is not ("expired" or "forbidden")
            || job.IsUnacceptedBrowserCapture || string.IsNullOrWhiteSpace(job.MediaProvider)
            || job.MediaProvider == "generic" || string.IsNullOrWhiteSpace(job.MediaId)
            || string.IsNullOrWhiteSpace(job.MediaFormatId)) return false;
        lock (AutomaticAttempts)
        {
            DateTime now = DateTime.UtcNow;
            foreach (Guid stale in AutomaticAttempts
                .Where(pair => now - pair.Value.StartedUtc >= TimeSpan.FromHours(1))
                .Select(pair => pair.Key).ToArray())
                AutomaticAttempts.Remove(stale);
            if (!AutomaticAttempts.TryGetValue(job.Id, out AttemptWindow? window))
            {
                if (AutomaticAttempts.Count >= MaxTrackedJobs) return false;
                AutomaticAttempts[job.Id] = window = new AttemptWindow(now, new HashSet<string>(StringComparer.Ordinal));
            }
            if (window.Sources.Count >= MaxSourcesPerHour) return false;
            return window.Sources.Add(SourceKey(job.Kind, job.SourceUrl));
        }
    }

    internal static void ForgetAutomaticAttempt(Guid id)
    {
        lock (AutomaticAttempts) AutomaticAttempts.Remove(id);
    }

    private static string SourceKey(JobKind kind, string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{url}")));

    private static bool AlreadyFailed(Guid id, JobKind kind, string url)
    {
        lock (AutomaticAttempts)
            return AutomaticAttempts.TryGetValue(id, out AttemptWindow? window)
                && window.Sources.Contains(SourceKey(kind, url));
    }

    internal static async Task<RescueResult> RefreshAutomaticallyAsync(DownloadEngine engine, JobSnapshot job, ResolverOptions network,
        CancellationToken ct)
    {
        await AutomaticSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!StillAwaitingSource(engine, job))
                return new(false, RescueMode.None, "The download changed before automatic recovery started.", 0);
            return await RefreshAsync(engine, job, network, ct, automatic: true).ConfigureAwait(false);
        }
        finally { AutomaticSlots.Release(); }
    }

    public static async Task<RescueResult> RefreshAsync(DownloadEngine engine, JobSnapshot job, ResolverOptions network,
        CancellationToken ct, bool automatic = false)
    {
        if (!Uri.TryCreate(job.OriginalPageUrl, UriKind.Absolute, out Uri? page))
            return new RescueResult(false, RescueMode.None, "The original page address is missing. Saved data was kept.", 0);

        using var resolver = new DefaultMediaResolver(network);
        var discovery = await Task.Run(() => resolver.ResolveAsync(new MediaDiscoveryRequest
        { Url = page, Options = new DiscoveryOptions { CollectionIntent = CollectionIntent.ItemOnly } }, ct), ct);
        if (discovery.Item is not { } item)
            return new RescueResult(false, RescueMode.None,
                discovery.Error?.UserMessage ?? "The original page could not be resolved. Saved data was kept.", 0);
        if (item.MediaId is null)
            return new RescueResult(false, RescueMode.None, "The source does not expose a verifiable media identity. Saved data was kept.", 0);

        if (item.Provider != job.MediaProvider || item.MediaId != job.MediaId)
            return new RescueResult(false, RescueMode.None, "The refreshed media identity differs. Saved data was kept.", 0);
        if (job.Kind == JobKind.Merge) return await RefreshMergeAsync(engine, job, item, ct, automatic);
        return await RefreshSelectedAsync(engine, job, item, ct, automatic);
    }

    internal static async Task<RescueResult> RefreshSelectedAsync(DownloadEngine engine, JobSnapshot job, MediaItem item,
        CancellationToken ct, bool automatic)
    {
        StreamProtocol protocol = job.Kind switch
        {
            JobKind.Dash => StreamProtocol.Dash,
            JobKind.Hls => StreamProtocol.Hls,
            _ => StreamProtocol.Http
        };
        var exact = item.Representations.Where(r => r.Id == job.MediaFormatId && (r.ExpiresAt is null || r.ExpiresAt > DateTimeOffset.UtcNow))
            .GroupBy(r => (r.Protocol, r.Url.AbsoluteUri)).Select(group => group.First()).ToArray();
        RescueResult last = new(false, RescueMode.None, "The exact selected format is unavailable. Saved data was kept.", 0);
        // Try the original delivery first. Different URLs with the exact format ID are not
        // assumed equivalent: each goes through the engine's independent identity checks.
        foreach (MediaRepresentation format in exact.Where(r => r.Protocol == protocol).Take(3))
        {
            ct.ThrowIfCancellationRequested();
            if (automatic && job.Kind == JobKind.Http && AlreadyFailed(job.Id, job.Kind, format.Url.AbsoluteUri)
                && !format.Context.HasCredentials) continue;
            MediaItem narrowed = item with { Representations = [format] };
            last = job.Kind switch
            {
                JobKind.Http => await RefreshDirectAsync(engine, job, narrowed, ct, automatic ? job.SourceUrl : null),
                JobKind.Dash => await RefreshDashAsync(engine, job, narrowed, ct, automatic),
                _ => await RefreshLiveHlsAsync(engine, job, narrowed, ct, automatic ? job.SourceUrl : null)
            };
            if (last.Accepted) return last;
            if (automatic && !StillAwaitingSource(engine, job)) return last;
        }
        if (job.State != JobState.NeedsLink || job.Reason is not ("expired" or "forbidden")
            || job.Output != MediaOutput.Original || job.IsLiveRecording || !StillAwaitingSource(engine, job))
            return last;
        foreach (MediaRepresentation format in exact.Where(r => !r.IsLive
            && (r.Protocol != protocol && r.Protocol is (StreamProtocol.Http or StreamProtocol.Dash or StreamProtocol.Hls)
                || protocol == StreamProtocol.Http && r.Protocol == protocol && r.Url.AbsoluteUri != job.SourceUrl)).Take(3))
        {
            ct.ThrowIfCancellationRequested();
            if (!TryRefreshCredentials(format, out var secrets, out _)) continue;
            var prefs = format.HasVideo ? FormatPreferences.BestQuality : FormatPreferences.AudioOnly with { AudioCodecs = [] };
            (MediaDownloadChoice? choice, _) = MediaTransferBridge.Prepare(item with { Representations = [format] }, prefs);
            if (choice is null || choice.Kind is not (JobKind.Http or JobKind.Dash or JobKind.Hls)
                || choice.Kind == job.Kind && choice.Kind != JobKind.Http
                || choice.Output != job.Output || choice.FormatId != job.MediaFormatId || choice.Quality != job.MediaQuality
                || choice.SubtitleTracks is { Count: > 0 } || choice.MediaMetadataJson is not null
                || automatic && AlreadyFailed(job.Id, choice.Kind, choice.Url)) continue;
            // HLS choices intentionally omit a filename extension until the engine parses the media playlist.
            // Preserve the existing container; the import independently checks the playlist's actual container.
            string fileName = choice.Kind == JobKind.Hls
                ? Path.ChangeExtension(choice.FileName, Path.GetExtension(job.FileName)) : choice.FileName;
            last = await engine.SwitchMediaAcquisitionAsync(job.Id,
                new MediaAcquisitionReplacement(new ResolvedMediaSource(item.Provider, item.MediaId!, format.Id,
                    choice.Url, choice.ExpiresAt, secrets), choice.Kind, fileName, choice.Quality!,
                    choice.MediaPlanJson, format.FileSizeIsEstimate ? null : format.FileSize, choice.Referer, choice.UserAgent),
                job.SourceUrl, ct).ConfigureAwait(false);
            if (last.Accepted || !StillAwaitingSource(engine, job)) return last;
        }
        return last;
    }

    private static bool StillAwaitingSource(DownloadEngine engine, JobSnapshot job) =>
        engine.Snapshot(job.Id) is { State: JobState.NeedsLink } current
        && current.SourceUrl == job.SourceUrl && current.Reason is ("expired" or "forbidden");

    internal static async Task<RescueResult> RefreshDirectAsync(DownloadEngine engine, JobSnapshot job, MediaItem item, CancellationToken ct,
        string? expectedExpiredSource = null)
    {
        MediaRepresentation? format = item.Representations.FirstOrDefault(r => r.Id == job.MediaFormatId);
        if (format is null || format.Protocol != StreamProtocol.Http)
            return new RescueResult(false, RescueMode.None,
                "The exact format or its required request context is unavailable. Restart as a separate copy; saved data was kept.", 0);
        if (!TryRefreshCredentials(format, out IReadOnlyDictionary<string, string>? secrets, out string? error))
            return new RescueResult(false, RescueMode.None, error!, 0);
        return await engine.RefreshMediaSourceAsync(job.Id, new ResolvedMediaSource(item.Provider,
            item.MediaId!, format.Id, format.Url.AbsoluteUri, format.ExpiresAt, secrets), ct, expectedExpiredSource);
    }

    internal static async Task<RescueResult> RefreshLiveHlsAsync(DownloadEngine engine, JobSnapshot job, MediaItem item, CancellationToken ct,
        string? expectedExpiredSource = null)
    {
        MediaRepresentation? format = item.Representations.FirstOrDefault(r => r.Id == job.MediaFormatId);
        if (format is null || format.Protocol != StreamProtocol.Hls)
            return new RescueResult(false, RescueMode.None,
                "The exact HLS stream format or its required request context is unavailable. Saved data was kept.", 0);
        if (!TryRefreshCredentials(format, out IReadOnlyDictionary<string, string>? secrets, out string? error))
            return new RescueResult(false, RescueMode.None, error!, 0);
        return await engine.RefreshMediaSourceAsync(job.Id, new ResolvedMediaSource(item.Provider,
            item.MediaId!, format.Id, format.Url.AbsoluteUri, format.ExpiresAt, secrets), ct, expectedExpiredSource);
    }

    private static async Task<RescueResult> RefreshDashAsync(DownloadEngine engine, JobSnapshot job, MediaItem item,
        CancellationToken ct, bool automatic)
    {
        MediaRepresentation? format = item.Representations.FirstOrDefault(r => r.Id == job.MediaFormatId);
        if (format is null || format.Protocol != StreamProtocol.Dash || item.Provider != job.MediaProvider || item.MediaId != job.MediaId)
            return new RescueResult(false, RescueMode.None, "The exact DASH representation is unavailable. Saved data was kept.", 0);
        if (!TryRefreshCredentials(format, out IReadOnlyDictionary<string, string>? secrets, out string? error))
            return new RescueResult(false, RescueMode.None, error!, 0);
        var prefs = format.HasVideo ? FormatPreferences.BestQuality : FormatPreferences.AudioOnly with { AudioCodecs = [] };
        (MediaDownloadChoice? choice, string? planningError) = MediaTransferBridge.Prepare(item with { Representations = [format] }, prefs);
        if (choice is not { Kind: JobKind.Dash, MediaPlanJson: { } plan })
            return new RescueResult(false, RescueMode.None, planningError ?? "The refreshed DASH representation cannot be planned safely.", 0);
        return await engine.RefreshDashPlanAsync(job.Id, new ResolvedMediaSource(item.Provider, item.MediaId!,
            format.Id, choice.Url, choice.ExpiresAt, secrets), plan, ct, automatic ? job.SourceUrl : null).ConfigureAwait(false);
    }

    private static bool TryRefreshCredentials(MediaRepresentation format, out IReadOnlyDictionary<string, string>? secrets, out string? error)
    {
        secrets = null;
        error = null;
        if (format.Context.Origin is not null || format.Context.AcceptLanguage is not null
            || format.Context.SensitiveHeaders.Keys.Any(name => !name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
        {
            error = "The exact format or its required request context is unavailable. Restart as a separate copy; saved data was kept.";
            return false;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> header in format.Context.HeadersFor(format.Url))
        {
            string? name = header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ? "Cookie"
                : header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? "Authorization" : null;
            if (name is not null && !headers.TryAdd(name, header.Value))
            {
                error = "The exact format or its required request context is unavailable. Restart as a separate copy; saved data was kept.";
                return false;
            }
        }
        if (format.Context.HasCredentials && headers.Count == 0)
        {
            error = "The refreshed link requires credentials that are not authorized for that media host. Saved data was kept.";
            return false;
        }

        if (headers.Count > 0) secrets = headers;
        return true;
    }

    // A merge job's format id is "videoId+audioId" (or one id for audio only). Restrict the item to exactly
    // those formats and let the planner build the same plan again with fresh links.
    private static async Task<RescueResult> RefreshMergeAsync(DownloadEngine engine, JobSnapshot job, MediaItem item,
        CancellationToken ct, bool automatic)
    {
        if (item.Provider != job.MediaProvider || item.MediaId != job.MediaId)
            return new RescueResult(false, RescueMode.None, "The refreshed media identity differs. Saved data was kept.", 0);
        string[] ids = job.MediaFormatId!.Split('+', StringSplitOptions.RemoveEmptyEntries);
        MediaRepresentation[] same = item.Representations.Where(r => ids.Contains(r.Id)).ToArray();
        if (same.Length != ids.Length)
            return new RescueResult(false, RescueMode.None,
                "The source no longer offers the same formats. Saved data was kept; restart as a separate copy.", 0);

        bool audioOnly = same.All(r => !r.HasVideo);
        var prefs = audioOnly ? FormatPreferences.AudioOnly with { AudioCodecs = [] } : FormatPreferences.BestQuality;
        (MediaDownloadChoice? choice, string? error) = MediaTransferBridge.Prepare(item with { Representations = same }, prefs);
        if (choice is not { Kind: JobKind.Merge, MediaPlanJson: { } plan })
            return new RescueResult(false, RescueMode.None,
                error ?? "The refreshed source cannot be planned the same way. Saved data was kept.", 0);
        if (choice.Secrets is { Count: > 0 })
            return await engine.RefreshMergePlanWithCredentialsAsync(job.Id, plan, choice.ExpiresAt, choice.Secrets, ct, automatic ? job.SourceUrl : null)
                .ConfigureAwait(false);
        return await engine.RefreshMergePlanAsync(job.Id, plan, choice.ExpiresAt, ct, automatic ? job.SourceUrl : null).ConfigureAwait(false);
    }
}
