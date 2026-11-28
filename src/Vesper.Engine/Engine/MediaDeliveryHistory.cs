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
    private readonly record struct Observation(string Provider, string? OriginKey, JobKind Kind, bool Completed, DateTime At,
        string? AssetKey, long Bytes = 0, double BytesPerSecond = 0);
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
            && job.PublishedSha256 is { Length: 64 } digest && digest.All(Uri.IsHexDigit)
            && job.TotalBytes is > 0 && job.Reason != "unverified-length";
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
        string? originKey = OriginKey(job.OriginalPageUrl);
        string? assetKey = originKey is not null && job.MediaId is { Length: > 0 and <= 512 } mediaId
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(originKey + "\n" + provider + "\n" + mediaId)))
            : null;
        long bytes = 0;
        double speed = 0;
        if (complete && job.DeliveryElapsedMilliseconds is >= 250 and <= 604800000
            && job.TotalBytes is long size && size >= 1024 * 1024)
        {
            bytes = size;
            speed = size * 1000.0 / job.DeliveryElapsedMilliseconds.Value;
        }
        _observations[job.Id] = new(provider, originKey, job.Kind, complete, at, assetKey, bytes, speed);
    }

    // Store only real measured, unrestricted, verified completed media runs.
    // The elapsed value piggybacks on the existing final job transaction.
    internal static long? EligibleElapsed(DownloadJob job, TimeSpan? elapsed)
    {
        if (elapsed is not { } duration || duration.TotalMilliseconds < 250 || duration.TotalDays > 7
            || job.State != JobState.Completed || job.CompletedUtc is null
            || job.MediaProvider is not { Length: > 0 and <= 80 }
            || job.Kind is not (JobKind.Http or JobKind.Hls or JobKind.Dash)
            || job.Output != MediaOutput.Original || job.Reason == "unverified-length"
            || job.PublishedSha256 is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit)
            || job.TotalBytes is not >= 1024 * 1024) return null;
        return (long)duration.TotalMilliseconds;
    }

    internal void Forget(Guid id) => _observations.Remove(id);

    // Reconstruct local context from existing job records; retain no raw origin
    // or path in the advisory cache. Generic sites can learn independently.
    private static string? OriginKey(string? pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? page)
            || page.Scheme is not ("http" or "https") || page.UserInfo.Length != 0) return null;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(page.GetLeftPart(UriPartial.Authority))));
    }

    internal JobKind? Preferred(string provider, IReadOnlyCollection<JobKind> available, DateTime now, long? expectedBytes = null, string? pageUrl = null)
    {
        var kinds = available.Distinct().Where(kind => kind is JobKind.Http or JobKind.Hls or JobKind.Dash).ToArray();
        if (kinds.Length < 2) return null;
        var eligible = _observations.Values.Where(value => value.At >= now - Lifetime && value.At <= now
            && kinds.Contains(value.Kind)).ToArray();
        // Multiple copies of the same known program are not independent
        // reliability trials. Keep the latest outcome per program and route.
        // Unknown identity remains ordinary delivery evidence, never an identity label.
        var recent = eligible.Where(value => value.AssetKey is null).Concat(eligible
            .Where(value => value.AssetKey is not null)
            .GroupBy(value => (value.AssetKey, value.Kind))
            .Select(group => group.OrderByDescending(value => value.At).ThenBy(value => value.Completed).First()))
            .ToArray();

        string? key = OriginKey(pageUrl);
        if (key is not null)
        {
            var originRows = recent.Where(value => value.OriginKey == key).ToArray();
            var localOrigin = Compare(originRows, kinds, false);
            if (localOrigin.Enough) return localOrigin.Preferred ?? CompareSpeed(originRows, kinds, expectedBytes);
        }

        // Prefer evidence from the same named adapter. Transfer to another adapter only
        // when each compared route was observed across multiple named adapter families.
        if (!string.IsNullOrEmpty(provider) && provider != "generic")
        {
            var localRows = recent.Where(value => value.Provider == provider).ToArray();
            var local = Compare(localRows, kinds, false);
            if (local.Enough) return local.Preferred ?? CompareSpeed(localRows, kinds, expectedBytes);
        }
        return Compare(recent.Where(value => value.Provider != "generic").ToArray(), kinds, true).Preferred;
    }

    private static JobKind? CompareSpeed(Observation[] observations, JobKind[] kinds, long? expectedBytes)
    {
        if (expectedBytes is not long size || size < 1024 * 1024) return null;
        var speeds = new List<(JobKind Kind, double Median)>(kinds.Length);
        foreach (JobKind kind in kinds)
        {
            var rows = observations.Where(row => row.Kind == kind).ToArray();
            // Reliability must already be high. Compare only similar-sized files on this adapter.
            if (rows.Length < 6 || rows.Count(row => row.Completed) < rows.Length * 0.9) return null;
            var samples = rows.Where(row => row.BytesPerSecond > 0 && row.Bytes >= size / 2
                && row.Bytes / 2 <= size).Select(row => row.BytesPerSecond).Order().ToArray();
            if (samples.Length < 6) return null;
            speeds.Add((kind, samples[samples.Length / 2]));
        }
        var winner = speeds.MaxBy(row => row.Median);
        return speeds.Where(row => row.Kind != winner.Kind).All(row => winner.Median >= row.Median * 1.5)
            ? winner.Kind : null;
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
