using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Tests.TestSupport;

/// <summary>Scriptable IMediaResolver for pager/batch tests (no HTTP involved).</summary>
public sealed class FakeResolver : IMediaResolver
{
    private int _active, _maxActive;

    public Func<MediaDiscoveryRequest, CancellationToken, Task<MediaDiscoveryResult>> Handler { get; set; } =
        (_, _) => Task.FromResult(MediaDiscoveryResult.Failure(MediaErrors.Unavailable("not scripted")));

    public List<MediaDiscoveryRequest> Calls { get; } = new();
    public int MaxConcurrent => _maxActive;

    public FakeResolver() { }

    public FakeResolver(Func<MediaDiscoveryRequest, MediaDiscoveryResult> sync) =>
        Handler = (r, _) => Task.FromResult(sync(r));

    public async Task<MediaDiscoveryResult> ResolveAsync(MediaDiscoveryRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Calls) Calls.Add(request);
        int now = Interlocked.Increment(ref _active);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, now, seen) != seen) { }
        try { return await Handler(request, cancellationToken); }
        finally { Interlocked.Decrement(ref _active); }
    }
}
