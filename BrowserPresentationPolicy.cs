namespace VESPER;

/// <summary>One reveal per burst of automatic browser jobs, explicit commands take priority.</summary>
internal sealed class BrowserPresentationPolicy
{
    private const long QuietPeriodMs = 45_000;
    private const int Capacity = 64;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private long? _lastAccepted;

    // UI-thread only. Replayed receipts cannot reopen the window.
    internal bool ShouldPresent(string receipt, bool explicitRequest, long now)
    {
        if (!_seen.Add(receipt)) return false;
        _order.Enqueue(receipt);
        if (_order.Count > Capacity) _seen.Remove(_order.Dequeue());
        bool present = explicitRequest || _lastAccepted is not long last || now - last >= QuietPeriodMs;
        _lastAccepted = now;
        return present;
    }
}
