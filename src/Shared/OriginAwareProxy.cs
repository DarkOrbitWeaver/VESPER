using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;

namespace Vesper.Shared;

internal sealed class OriginAwareProxy(IWebProxy inner) : IWebProxy
{
    private readonly ConditionalWeakTable<Uri, OriginalAddress> _originals = new();

    public ICredentials? Credentials
    {
        get => inner.Credentials;
        set => inner.Credentials = value;
    }

    internal Uri Pin(Uri original, IPAddress address)
    {
        var pinned = new UriBuilder(original) { Host = address.ToString() }.Uri;
        _originals.Add(pinned, new(original));
        return pinned;
    }

    internal void PinRequest(HttpRequestMessage request, Uri original, IPAddress address)
    {
        request.RequestUri = Pin(original, address);
        request.Headers.Host = original.Authority;
    }

    public Uri? GetProxy(Uri destination) => inner.GetProxy(Original(destination));

    public bool IsBypassed(Uri host) => inner.IsBypassed(Original(host));

    private Uri Original(Uri address) => _originals.TryGetValue(address, out var original) ? original.Uri : address;

    private sealed record OriginalAddress(Uri Uri);
}
