namespace Vesper.Engine;

/// <summary>A newly resolved plain-file URL, not permission to append a different representation.</summary>
public sealed record ResolvedMediaSource(string Provider, string MediaId, string FormatId, string Url,
    DateTimeOffset? ExpiresAt = null);

public sealed partial class DownloadEngine
{
    /// <summary>
    /// Explicit source re-resolution for HTTP media. Requires exact provider/media/format identities, then delegates
    /// byte identity to Rescue (strong same-resource validators or a trusted full/piece digest). A matching format ID
    /// alone is never proof that bytes match. Streaming refresh needs plan-level verification and is refused here.
    /// </summary>
    public async Task<RescueResult> RefreshMediaSourceAsync(Guid id, ResolvedMediaSource replacement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var job)) return Rejected("That entry no longer exists.");
            if (job.Kind != JobKind.Http) return Rejected("Streaming sources require a compatible fragment plan. Saved data was kept; resolve a separate copy.");
            if (string.IsNullOrEmpty(job.MediaProvider) || string.IsNullOrEmpty(job.MediaId) || string.IsNullOrEmpty(job.MediaFormatId))
                return Rejected("This older job does not have enough source identity for automatic re-resolution. Use ordinary Rescue with a trusted identity.");
            if (job.MediaProvider != replacement.Provider || job.MediaId != replacement.MediaId || job.MediaFormatId != replacement.FormatId)
                return Rejected("The newly resolved provider, media identity or selected format differs. Restart as a separate copy; no bytes were changed.");
            if (replacement.ExpiresAt <= DateTimeOffset.UtcNow) return Rejected("The replacement source is already expired.");
        }
        ct.ThrowIfCancellationRequested();
        var result = await ReplaceLinkAsync(id, replacement.Url, ct: ct).ConfigureAwait(false);
        if (result.Accepted)
        {
            lock (_lock)
            {
                if (_jobs.TryGetValue(id, out var job) && job.SourceUrl == replacement.Url)
                { job.MediaExpiresAt = replacement.ExpiresAt; SaveLocked(job); Changed(job); }
            }
            FlushOutbox();
        }
        return result;
    }
}
