using Vesper.Engine;

namespace VESPER;

internal sealed partial class BrowserBridgeHandler
{
    private sealed record BrowserCommand(string Id, string Action);
    private sealed class ProfileWatch
    {
        internal readonly Dictionary<string, BrowserCommand> Commands = new(StringComparer.Ordinal);
        internal HashSet<string> Files = new(StringComparer.Ordinal);
        internal TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal System.Threading.Timer? Expiry;
        internal DateTime LastSeen;
        internal int Revision;
    }

    private readonly Dictionary<string, ProfileWatch> _browserWatches = new(StringComparer.Ordinal);
    private readonly object _browserWatchLock = new();

    private async Task<BridgeResponse> WatchBrowserActivityAsync(BridgeRequest request, CancellationToken ct)
    {
        var activity = request.Activity!;
        if (activity.Files.Count == 0)
        {
            lock (_browserWatchLock)
                if (_browserWatches.Remove(activity.Profile, out var closed)) Release(closed);
            await owner.ApplyBrowserActivityAsync(activity, ct);
            return BridgeResponse.Success(request.Id);
        }

        ProfileWatch watch;
        Task changed;
        int revision;
        lock (_browserWatchLock)
        {
            if (!_browserWatches.TryGetValue(activity.Profile, out watch!))
            {
                if (_browserWatches.Count >= 8) return BridgeResponse.Fail(request.Id, "busy", "Too many browser connections.");
                _browserWatches[activity.Profile] = watch = new ProfileWatch();
                watch.Expiry = new System.Threading.Timer(_ => ExpireBrowserWatch(activity.Profile, watch));
            }
            watch.Files = activity.Files.Select(file => file.Id).ToHashSet(StringComparer.Ordinal);
            watch.LastSeen = DateTime.UtcNow;
            revision = ++watch.Revision;
            watch.Expiry!.Change(TimeSpan.FromSeconds(50), Timeout.InfiniteTimeSpan);
            changed = watch.Commands.Count == 0 ? watch.Changed.Task : Task.CompletedTask;
        }

        bool visible = await owner.ApplyBrowserActivityAsync(activity, ct);
        try { await changed.WaitAsync(visible ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(20), ct); }
        catch (TimeoutException) { }
        lock (_browserWatchLock)
        {
            if (watch.Revision != revision) return BridgeResponse.Success(request.Id);
            BrowserCommand[] commands = watch.Commands.Values.Where(command => watch.Files.Contains(command.Id)).ToArray();
            watch.Commands.Clear();
            if (watch.Changed.Task.IsCompleted) watch.Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return BridgeResponse.Success(request.Id, data: new { commands });
        }
    }

    internal void SendBrowserCommand(string profile, string id, string action)
    {
        lock (_browserWatchLock)
        {
            if (!_browserWatches.TryGetValue(profile, out var watch) || !watch.Files.Contains(id)) return;
            watch.Commands[id] = new BrowserCommand(id, action);
            watch.Changed.TrySetResult();
        }
    }

    internal void WakeBrowserActivity()
    {
        lock (_browserWatchLock)
            foreach (var watch in _browserWatches.Values) watch.Changed.TrySetResult();
    }

    internal void CloseBrowserActivity()
    {
        lock (_browserWatchLock)
        {
            foreach (var watch in _browserWatches.Values) Release(watch);
            _browserWatches.Clear();
        }
    }

    private void ExpireBrowserWatch(string profile, ProfileWatch watch)
    {
        lock (_browserWatchLock)
        {
            if (!_browserWatches.TryGetValue(profile, out var active) || active != watch
                || DateTime.UtcNow - watch.LastSeen < TimeSpan.FromSeconds(49)) return;
            _browserWatches.Remove(profile);
            Release(watch);
            owner.ClearBrowserActivity(profile);
        }
    }

    private static void Release(ProfileWatch watch)
    {
        watch.Expiry?.Dispose();
        watch.Commands.Clear();
        watch.Changed.TrySetResult();
    }
}
