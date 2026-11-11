using System.Net;
using System.Net.Http.Headers;
using Vesper.MediaResolver.Core;
using Xunit;

namespace Vesper.MediaResolver.Tests.SecurityTests;

public sealed class HttpBodyLimitTests
{
    [Fact]
    public async Task ConcurrencyPermit_IsHeldUntilTheBodyHasBeenRead()
    {
        var handler = new BodyHandler(TimeSpan.FromMilliseconds(40));
        using var http = new SecureHttpClient(new ResolverOptions { MaxConcurrentRequests = 1 }, handler);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(i => http.GetStringAsync(new($"https://example.com/{i}"), null, CancellationToken.None)));
        Assert.Equal(1, handler.MaxActive);
        Assert.Equal(0, handler.Active);
    }

    [Fact]
    public async Task ThumbnailBody_HasItsOwnDeadline()
    {
        var handler = new BodyHandler(TimeSpan.FromSeconds(10), "image/jpeg");
        using var http = new SecureHttpClient(new ResolverOptions { RequestTimeout = TimeSpan.FromMilliseconds(40) }, handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetThumbnailAsync(new("https://example.com/poster.jpg"), CancellationToken.None));
        Assert.Equal(0, handler.Active);
    }

    [Fact]
    public async Task CancellingAnActiveBody_ReleasesThePermitForTheNextRequest()
    {
        var handler = new BodyHandler(TimeSpan.FromMilliseconds(80));
        using var http = new SecureHttpClient(new ResolverOptions { MaxConcurrentRequests = 1 }, handler);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(15));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetStringAsync(new("https://example.com/cancel"), null, cancel.Token));
        Assert.Equal("x", (await http.GetStringAsync(new("https://example.com/next"), null, CancellationToken.None)).Content);
        Assert.Equal(0, handler.Active);
    }

    private sealed class BodyHandler(TimeSpan delay, string mime = "text/plain") : HttpMessageHandler
    {
        internal int Active, MaxActive;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new DelayedContent(this, delay, mime) });
    }
    private sealed class DelayedContent : HttpContent
    {
        private readonly BodyHandler _owner;
        private readonly TimeSpan _delay;
        private DelayedStream? _stream;
        internal DelayedContent(BodyHandler owner, TimeSpan delay, string mime)
        { _owner = owner; _delay = delay; Headers.ContentType = new MediaTypeHeaderValue(mime); }
        protected override bool TryComputeLength(out long length) { length = 1; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream ??= new DelayedStream(_owner, _delay));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => CreateContentReadStreamAsync();
        protected override void Dispose(bool disposing) { if (disposing) _stream?.Dispose(); base.Dispose(disposing); }
    }
    private sealed class DelayedStream : Stream
    {
        private readonly BodyHandler _owner;
        private readonly TimeSpan _delay;
        private bool _read;
        private int _disposed;
        internal DelayedStream(BodyHandler owner, TimeSpan delay)
        {
            _owner = owner; _delay = delay;
            int active = Interlocked.Increment(ref owner.Active);
            owner.MaxActive = Math.Max(owner.MaxActive, active);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_read) return 0;
            await Task.Delay(_delay, ct); _read = true; buffer.Span[0] = (byte)'x'; return 1;
        }
        protected override void Dispose(bool disposing)
        { if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _owner.Active); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => 1; public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
