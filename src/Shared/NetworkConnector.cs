using System.Net;
using System.Net.Sockets;

namespace Vesper.Shared;

/// <summary>Connects only prevalidated addresses, with two bounded, staggered attempts.</summary>
internal static class NetworkConnector
{
    public static async ValueTask<Stream> ConnectAsync(IPAddress[] addresses, int port, CancellationToken ct)
    {
        var v6 = new Queue<IPAddress>(addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6));
        var v4 = new Queue<IPAddress>(addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork));
        var ordered = new Queue<IPAddress>();
        while (v6.Count + v4.Count > 0)
        {
            if (v6.TryDequeue(out var six)) ordered.Enqueue(six);
            if (v4.TryDequeue(out var four)) ordered.Enqueue(four);
        }
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new List<Task<Socket>>(2);
        Exception? last = null;
        try
        {
            while (ordered.Count > 0 || pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (pending.Count == 0 && ordered.TryDequeue(out var first)) pending.Add(ConnectOne(first, port, race.Token));
                using var stagger = CancellationTokenSource.CreateLinkedTokenSource(race.Token);
                Task delay = Task.Delay(ordered.Count > 0 && pending.Count < 2 ? 250 : Timeout.Infinite, stagger.Token);
                Task finished = await Task.WhenAny(pending.Cast<Task>().Append(delay)).ConfigureAwait(false);
                stagger.Cancel();
                if (finished == delay)
                {
                    ct.ThrowIfCancellationRequested();
                    if (ordered.TryDequeue(out var next)) pending.Add(ConnectOne(next, port, race.Token));
                    continue;
                }
                var attempt = (Task<Socket>)finished;
                pending.Remove(attempt);
                try { return new NetworkStream(await attempt.ConfigureAwait(false), ownsSocket: true); }
                catch (SocketException ex) { last = ex; }
            }
            throw last ?? new SocketException((int)SocketError.HostUnreachable);
        }
        finally
        {
            race.Cancel();
            // Observe failures and close successful losers even on cancellation, not just on success.
            foreach (var attempt in pending)
            {
                try { (await attempt.ConfigureAwait(false)).Dispose(); }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException) { }
            }
        }
    }

    private static async Task<Socket> ConnectOne(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false); return socket; }
        catch { socket.Dispose(); throw; }
    }
}
