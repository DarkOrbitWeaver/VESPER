namespace Vesper.Engine;

/// <summary>
/// Bounded advisory history derived from the existing native job records. Completion is a
/// transport outcome, not a label for correct player identity. No URLs, credentials or media
/// titles are retained. The engine owns synchronization and forgets removed records.
/// </summary>
internal sealed class MediaDeliveryHistory
{
    private const int Capacity = 256;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private readonly Dictionary<Guid, Observation> _observations = [];
    private readonly record struct Observation(string Provider, JobKind Kind, bool Completed, DateTime At);
    private readonly record struct Route(JobKind Kind, int Total, int Completed)
    {
        public double Rate => (double)Completed / Total;
    }

    internal int Count => _observations.Count;

    internal void Observe(DownloadJob job)
    {
        if (job.MediaProvider is not { Length: > 0 and <= 80 } provider
            || job.Kind is not (JobKind.Http or JobKind.Hls or JobKind.Dash)
            || job.Output != MediaOutput.Original)
        {
            Forget(job.Id);
            return;
        }

        bool complete = job.State == JobState.Completed && job.CompletedUtc is not null
            && job.PublishedSha256 is { Length: 64 } && job.TotalBytes is > 0;
        bool failed = job.State == JobState.NeedsAttention
            && job.Reason is "media-validation" or "range-ignored" or "range-rejected" or "html" or "invalid-container" or "not-playlist";
        if (!complete && !failed)
        {
            Forget(job.Id);
            return;
        }

        DateTime at = complete ? job.CompletedUtc!.Value : job.UpdatedUtc;
        if (!_observations.ContainsKey(job.Id) && _observations.Count >= Capacity)
        {
            var oldest = _observations.MinBy(entry => entry.Value.At);
            if (oldest.Value.At >= at) return;
            _observations.Remove(oldest.Key);
        }
        _observations[job.Id] = new(provider, job.Kind, complete, at);
    }

    internal void Forget(Guid id) => _observations.Remove(id);

    internal JobKind? Preferred(string provider, IReadOnlyCollection<JobKind> available, DateTime now)
    {
        var kinds = available.Distinct().Where(kind => kind is JobKind.Http or JobKind.Hls or JobKind.Dash).ToArray();
        if (kinds.Length < 2) return null;
        var recent = _observations.Values.Where(value => value.At >= now - Lifetime && value.At <= now
            && kinds.Contains(value.Kind)).ToArray();

        // Prefer evidence from the same named adapter. Transfer to another adapter only
        // when each compared route was observed across multiple named adapter families.
        if (!string.IsNullOrEmpty(provider) && provider != "generic")
        {
            var local = Compare(recent.Where(value => value.Provider == provider).ToArray(), kinds, false);
            if (local.Enough) return local.Preferred;
        }
        return Compare(recent.Where(value => value.Provider != "generic").ToArray(), kinds, true).Preferred;
    }

    private static (bool Enough, JobKind? Preferred) Compare(Observation[] observations, JobKind[] kinds, bool diverse)
    {
        var routes = new List<Route>(kinds.Length);
        foreach (JobKind kind in kinds)
        {
            var rows = observations.Where(value => value.Kind == kind).ToArray();
            if (rows.Length < 6 || diverse && rows.Select(value => value.Provider).Distinct().Count() < 2)
                return (false, null);
            routes.Add(new(kind, rows.Length, rows.Count(value => value.Completed)));
        }
        var winner = routes.MaxBy(route => route.Rate);
        bool clear = winner.Rate >= 0.8 && routes.Where(route => route.Kind != winner.Kind)
            .All(route => route.Total - route.Completed >= 2 && winner.Rate - route.Rate >= 0.35);
        return (true, clear ? winner.Kind : null);
    }
}
