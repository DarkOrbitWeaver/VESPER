using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Core;

/// <summary>
/// Tiny TTL cache for successful, credential-free discovery results. No timers or threads: entries are checked
/// on access and evicted on insert, so the cache costs nothing while idle.
/// </summary>
internal sealed class MetadataCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (MediaDiscoveryResult Result, DateTimeOffset Expires)> _entries = new(StringComparer.Ordinal);
    private readonly int _max;

    public MetadataCache(int maxEntries) => _max = Math.Max(1, maxEntries);

    public bool TryGet(string key, DateTimeOffset now, out MediaDiscoveryResult result)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var e))
            {
                if (e.Expires > now) { result = e.Result; return true; }
                _entries.Remove(key);
            }
        }
        result = null!;
        return false;
    }

    public void Set(string key, MediaDiscoveryResult result, DateTimeOffset now, TimeSpan ttl)
    {
        // Never cache beyond the earliest signed-URL expiry.
        var expires = now + ttl;
        if (result.Item?.EarliestExpiry is { } exp && exp < expires) expires = exp;
        if (expires <= now) return;

        lock (_gate)
        {
            if (_entries.Count >= _max && !_entries.ContainsKey(key))
            {
                string? oldest = null;
                DateTimeOffset oldestAt = DateTimeOffset.MaxValue;
                foreach (var kv in _entries)
                    if (kv.Value.Expires < oldestAt) { oldestAt = kv.Value.Expires; oldest = kv.Key; }
                if (oldest is not null) _entries.Remove(oldest);
            }
            _entries[key] = (result, expires);
        }
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }
}
