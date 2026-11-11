using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Playlists;

public sealed record BatchEntry(Uri Url, string? Id = null, MediaItem? Preresolved = null);

public sealed class BatchRequest
{
    public required IReadOnlyList<BatchEntry> Entries { get; init; }

    /// <summary>The one preference applied to every item unless overridden below.</summary>
    public FormatPreferences Preferences { get; init; } = new();

    /// <summary>Optional saved per-site / per-item preferences (they win over <see cref="Preferences"/> when explicitly set).</summary>
    public PreferenceStore? PreferenceStore { get; init; }

    /// <summary>Per-item overrides keyed by <see cref="BatchEntry.Id"/>, media id or URL string. Highest priority.</summary>
    public IReadOnlyDictionary<string, FormatPreferences>? ItemOverrides { get; init; }

    /// <summary>The caller must set this only after the user explicitly approved downloading the whole batch.</summary>
    public bool UserAuthorizedBatch { get; init; }

    public int MaxConcurrency { get; init; } = 4;
    public int MaxItems { get; init; } = 500;
}

public sealed class BatchOutcome
{
    public required BatchEntry Entry { get; init; }
    public MediaItem? Item { get; init; }
    public FormatSelection? Selection { get; init; }
    public DownloadPlan? Plan { get; init; }
    public MediaError? Error { get; init; }
    public bool IsSuccess => Plan is not null;
}

public sealed class BatchResult
{
    public required IReadOnlyList<BatchOutcome> Outcomes { get; init; }
    public bool WasCancelled { get; init; }
    public bool Truncated { get; init; }
    public int Succeeded => Outcomes.Count(o => o.IsSuccess);
    public int Failed => Outcomes.Count(o => !o.IsSuccess);
    public int Fallbacks => Outcomes.Count(o => o.Plan is { IsFallback: true });
    public bool IsPartial => Failed > 0 || WasCancelled || Truncated;
}

/// <summary>
/// Applies ONE quality preference to many items. Each item is resolved independently with bounded concurrency; a failing
/// item never stops the others; unavailable heights fall back according to the preference's policy. Refuses to run
/// unless the caller states the batch was explicitly authorised by the user.
/// </summary>
public sealed class BatchResolver
{
    private readonly IMediaResolver _resolver;
    private readonly TimeProvider _time;

    public BatchResolver(IMediaResolver resolver, TimeProvider? time = null)
    {
        _resolver = resolver;
        _time = time ?? TimeProvider.System;
    }

    public async Task<BatchResult> ResolveAsync(BatchRequest request, IProgress<BatchOutcome>? progress, CancellationToken ct)
    {
        if (!request.UserAuthorizedBatch)
            throw new InvalidOperationException("A batch may only be resolved after the user explicitly authorised it (BatchRequest.UserAuthorizedBatch).");

        var entries = request.Entries.Take(request.MaxItems).ToList();
        bool truncated = request.Entries.Count > entries.Count;
        var slots = new BatchOutcome?[entries.Count];
        bool cancelled = false;

        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, entries.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(request.MaxConcurrency, 1, 8), CancellationToken = ct },
                async (i, token) =>
                {
                    var outcome = await ProcessOneAsync(entries[i], request, token).ConfigureAwait(false);
                    slots[i] = outcome;
                    progress?.Report(outcome);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }

        return new BatchResult
        {
            Outcomes = slots.Where(s => s is not null).Select(s => s!).ToList(),
            WasCancelled = cancelled,
            Truncated = truncated
        };
    }

    private async Task<BatchOutcome> ProcessOneAsync(BatchEntry entry, BatchRequest request, CancellationToken ct)
    {
        MediaItem? item = entry.Preresolved;
        if (item is null)
        {
            var result = await _resolver.ResolveAsync(new MediaDiscoveryRequest
            {
                Url = entry.Url,
                Options = new DiscoveryOptions { CollectionIntent = CollectionIntent.ItemOnly }
            }, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
                return new BatchOutcome { Entry = entry, Error = (result.Error ?? MediaErrors.NoMedia("unknown", false)) with { EntryId = entry.Id } };
            if (result.Item is null)
                return new BatchOutcome { Entry = entry, Error = MediaErrors.Invalid("Entry resolved to a collection, not a single item") with { EntryId = entry.Id } };
            item = result.Item;
        }

        var prefs = ChoosePreferences(entry, item, request);
        var selection = FormatSelector.Select(item, prefs);
        if (selection.Selection is null)
            return new BatchOutcome { Entry = entry, Item = item, Error = selection.Error! with { EntryId = entry.Id } };

        var plan = DownloadPlanBuilder.Build(item, selection.Selection, _time);
        return plan.Plan is null
            ? new BatchOutcome { Entry = entry, Item = item, Selection = selection.Selection, Error = plan.Error! with { EntryId = entry.Id } }
            : new BatchOutcome { Entry = entry, Item = item, Selection = selection.Selection, Plan = plan.Plan };
    }

    internal static FormatPreferences ChoosePreferences(BatchEntry entry, MediaItem item, BatchRequest request)
    {
        var o = request.ItemOverrides;
        if (o is not null)
        {
            if (entry.Id is not null && o.TryGetValue(entry.Id, out var byId)) return byId;
            if (item.MediaId is not null && o.TryGetValue(item.MediaId, out var byMedia)) return byMedia;
            if (o.TryGetValue(entry.Url.AbsoluteUri, out var byUrl)) return byUrl;
        }
        if (request.PreferenceStore is { } store && store.TryGetExplicit(item.Provider, item.MediaId, out var saved)) return saved;
        return request.Preferences;
    }
}
