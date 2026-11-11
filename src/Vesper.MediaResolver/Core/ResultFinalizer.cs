using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Security;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Core;

/// <summary>
/// Last gate before a result leaves the library: drops invalid/expired representations, removes duplicates,
/// bounds collections and refuses to call an item "successful" when nothing actionable remains.
/// </summary>
internal static class ResultFinalizer
{
    public static MediaDiscoveryResult Finalize(MediaDiscoveryResult r, ResolverOptions o, DateTimeOffset now)
    {
        if (!r.IsSuccess) return r;

        if (r.Item is { } item)
        {
            var fixedItem = FinalizeItem(item, o, now, out var error);
            return fixedItem is null
                ? MediaDiscoveryResult.Failure(error!)
                : new MediaDiscoveryResult { Status = r.Status, Item = fixedItem, Warnings = r.Warnings, Notes = r.Notes };
        }

        if (r.Collection is { } c)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<CollectionEntry>();
            bool truncated = false;
            foreach (var e in c.Entries)
            {
                if (!UrlValidator.IsAllowed(e.Url, o, out _)) continue;
                if (!seen.Add(e.MediaId)) continue;
                if (entries.Count >= o.MaxCollectionItems) { truncated = true; break; }

                var entry = e with { Index = entries.Count, Title = e.Title is null ? null : TextUtil.Clip(e.Title.Trim(), 300) };
                if (e.Resolved is not null)
                {
                    var fi = FinalizeItem(e.Resolved, o, now, out var ierr);
                    entry = fi is null ? entry with { Resolved = null, Error = ierr } : entry with { Resolved = fi };
                }
                entries.Add(entry);
            }
            var fc = c with
            {
                Entries = entries,
                IsComplete = c.IsComplete && !truncated,
                NextPageToken = truncated ? null : c.NextPageToken,
                Title = TextUtil.Clip(c.Title.Trim(), 300)
            };
            return new MediaDiscoveryResult
            {
                Status = fc.IsComplete && r.Warnings.Count == 0 ? DiscoveryStatus.Success : DiscoveryStatus.Partial,
                Collection = fc,
                Warnings = r.Warnings,
                Notes = r.Notes
            };
        }
        return r;
    }

    private static MediaItem? FinalizeItem(MediaItem item, ResolverOptions o, DateTimeOffset now, out MediaError? error)
    {
        error = null;
        var valid = item.Representations.Where(rep => UrlValidator.IsAllowed(rep.Url, o, out _)).ToList();
        var fresh = valid.Where(rep => rep.ExpiresAt is null || rep.ExpiresAt > now).ToList();
        if (valid.Count > 0 && fresh.Count == 0)
        {
            error = MediaErrors.Expired("All source URLs were already expired when discovered");
            return null;
        }

        var deduped = FormatDeduplicator.Deduplicate(fresh);
        if (deduped.Count == 0)
        {
            error = MediaErrors.NoMedia("No valid representation remained after validation", browserMayHelp: true);
            return null;
        }

        return item with
        {
            Representations = deduped,
            Kind = deduped.Any(x => x.HasVideo) ? MediaKind.Video : MediaKind.Audio,
            Subtitles = item.Subtitles.Where(s => UrlValidator.IsAllowed(s.Url, o, out _)).Take(64).ToList(),
            Title = TextUtil.Clip(item.Title.Trim(), 300),
            Description = item.Description is null ? null : TextUtil.Clip(item.Description, 5000),
            Thumbnail = item.Thumbnail is { } t && UrlValidator.IsAllowed(t, o, out _) ? t : null
        };
    }
}
