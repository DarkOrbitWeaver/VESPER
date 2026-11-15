using Vesper.Engine;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;

namespace VESPER;

/// <summary>
/// Gets a fresh set of links for a media download whose signed URLs expired, without losing the saved bytes.
/// Re-resolves the original page, rebuilds the plan for exactly the formats the job was started with, and
/// lets the engine decide whether the new plan continues the old data (same ids, sizes and identities).
/// Used by the Rescue window and, for merge jobs, automatically when a link expires.
/// </summary>
internal static class MediaSourceRescue
{
    private static readonly Dictionary<Guid, DateTime> LastAutomaticAttempt = [];

    public static bool CanRefresh(JobSnapshot job) =>
        job.Kind is JobKind.Http or JobKind.Merge && job.OriginalPageUrl is not null && job.MediaId is not null
        && job.MediaFormatId is not null;

    /// <summary>One automatic attempt per job per hour; the Rescue window can always try again by hand.</summary>
    public static bool ShouldTryAutomatically(JobSnapshot job)
    {
        if (job.Kind != JobKind.Merge || job.Reason != "expired" || !CanRefresh(job)) return false;
        lock (LastAutomaticAttempt)
        {
            if (LastAutomaticAttempt.TryGetValue(job.Id, out DateTime last) && DateTime.UtcNow - last < TimeSpan.FromHours(1)) return false;
            LastAutomaticAttempt[job.Id] = DateTime.UtcNow;
            return true;
        }
    }

    public static async Task<RescueResult> RefreshAsync(DownloadEngine engine, JobSnapshot job, ResolverOptions network, CancellationToken ct)
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

        if (job.Kind == JobKind.Http) return await RefreshDirectAsync(engine, job, item, ct);
        return RefreshMerge(engine, job, item);
    }

    private static async Task<RescueResult> RefreshDirectAsync(DownloadEngine engine, JobSnapshot job, MediaItem item, CancellationToken ct)
    {
        MediaRepresentation? format = item.Representations.FirstOrDefault(r => r.Id == job.MediaFormatId);
        if (format is null || format.Protocol != StreamProtocol.Http || format.Context.HasCredentials
            || format.Context.Origin is not null || format.Context.AcceptLanguage is not null)
            return new RescueResult(false, RescueMode.None,
                "The exact format or its required request context is unavailable. Restart as a separate copy; saved data was kept.", 0);
        return await engine.RefreshMediaSourceAsync(job.Id, new ResolvedMediaSource(item.Provider,
            item.MediaId!, format.Id, format.Url.AbsoluteUri, format.ExpiresAt), ct);
    }

    // A merge job's format id is "videoId+audioId" (or one id for audio only). Restrict the item to exactly
    // those formats and let the planner build the same plan again with fresh links.
    private static RescueResult RefreshMerge(DownloadEngine engine, JobSnapshot job, MediaItem item)
    {
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
        return engine.RefreshMergePlan(job.Id, plan, choice.ExpiresAt);
    }
}
