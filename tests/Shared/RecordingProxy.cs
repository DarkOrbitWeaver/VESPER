using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Vesper.Tests.Shared;

internal sealed class RecordingProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<string> _headers = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _clientHello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _server;
    private TcpClient? _client;
    private int _accepted;

    internal RecordingProxy()
    {
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Address = new Uri($"http://127.0.0.1:{endpoint.Port}/");
        _server = ServeAsync();
    }

    internal Uri Address { get; }
    internal int Accepted => Volatile.Read(ref _accepted);
    internal Task<string> Headers => _headers.Task;
    internal Task<string> ClientHello => _clientHello.Task;

    private async Task ServeAsync()
    {
        try
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
            Volatile.Write(ref _client, client);
            Interlocked.Increment(ref _accepted);
            await using NetworkStream stream = client.GetStream();
            byte[] headerBytes = await ReadHeadersAsync(stream, _stop.Token);
            string headers = Encoding.ASCII.GetString(headerBytes);
            _headers.TrySetResult(headers);

            if (headers.StartsWith("CONNECT ", StringComparison.Ordinal))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\nProxy-Agent: VESPER-Test\r\n\r\n"), _stop.Token);
                await stream.FlushAsync(_stop.Token);
                byte[] hello = await ReadTlsRecordAsync(stream, _stop.Token);
                _clientHello.TrySetResult(Encoding.Latin1.GetString(hello));
                return;
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\nok"), _stop.Token);
            await stream.FlushAsync(_stop.Token);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            _headers.TrySetCanceled(_stop.Token);
            _clientHello.TrySetCanceled(_stop.Token);
        }
        catch (SocketException) when (_stop.IsCancellationRequested)
        {
            _headers.TrySetCanceled(_stop.Token);
            _clientHello.TrySetCanceled(_stop.Token);
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
            _headers.TrySetCanceled(_stop.Token);
            _clientHello.TrySetCanceled(_stop.Token);
        }
        catch (IOException) when (_stop.IsCancellationRequested)
        {
            _headers.TrySetCanceled(_stop.Token);
            _clientHello.TrySetCanceled(_stop.Token);
        }
        catch (EndOfStreamException)
        {
            _clientHello.TrySetResult("");
        }
    }

    private static async Task<byte[]> ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        byte[] one = new byte[1];
        while (buffer.Length < 16 * 1024)
        {
            int read = await stream.ReadAsync(one, ct);
            if (read == 0) throw new EndOfStreamException("The proxy request ended before its headers.");
            buffer.WriteByte(one[0]);
            if (buffer.Length >= 4)
            {
                byte[] bytes = buffer.GetBuffer();
                int end = (int)buffer.Length - 4;
                if (bytes[end] == '\r' && bytes[end + 1] == '\n' && bytes[end + 2] == '\r' && bytes[end + 3] == '\n')
                    return buffer.ToArray();
            }
        }
        throw new InvalidDataException("The proxy request headers exceeded the test limit.");
    }

    private static async Task<byte[]> ReadTlsRecordAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[5];
        await stream.ReadExactlyAsync(header, ct);
        int payloadLength = (header[3] << 8) | header[4];
        byte[] record = new byte[header.Length + payloadLength];
        header.CopyTo(record, 0);
        await stream.ReadExactlyAsync(record.AsMemory(header.Length), ct);
        return record;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        Interlocked.Exchange(ref _client, null)?.Dispose();
        try { await _server; }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
