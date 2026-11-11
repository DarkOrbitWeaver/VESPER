using System.Diagnostics;
using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Core;

/// <summary>Per-call measurements. No URLs, credentials, timers or scheduled work.</summary>
public sealed record DiscoveryMeasurement(TimeSpan Elapsed, TimeSpan? FirstMetadata,
    TimeSpan? FirstRepresentation, int NetworkRequests, int RepresentationCount, bool CacheHit,
    DiscoveryStatus Status, MediaErrorCode? ErrorCode);

internal sealed class DiscoveryTrace
{
    internal static readonly AsyncLocal<DiscoveryTrace?> Current = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private int _requests;
    private long _metadata = -1, _representation = -1;
    internal DiscoveryTrace? Parent { get; init; }
    internal bool CacheHit { get; set; }

    internal void RequestStarted()
    {
        for (DiscoveryTrace? trace = this; trace is not null; trace = trace.Parent)
            Interlocked.Increment(ref trace._requests);
    }

    internal void Observe(DiscoveryProgress update)
    {
        long elapsed = Stopwatch.GetElapsedTime(_started).Ticks;
        if (update.Title is not null || update.Thumbnail is not null)
            Interlocked.CompareExchange(ref _metadata, elapsed, -1);
        if (update.RepresentationCount > 0)
            Interlocked.CompareExchange(ref _representation, elapsed, -1);
    }

    internal DiscoveryMeasurement Finish(MediaDiscoveryResult result) => new(
        Stopwatch.GetElapsedTime(_started), _metadata < 0 ? null : TimeSpan.FromTicks(_metadata),
        _representation < 0 ? null : TimeSpan.FromTicks(_representation), Volatile.Read(ref _requests),
        result.Item?.Representations.Count ?? 0, CacheHit, result.Status, result.Error?.Code);
}
