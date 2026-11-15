using System.Net;

namespace Vesper.MediaResolver.Security;

/// <summary>Decides whether an address is publicly routable. Used on literal hosts and on every resolved DNS answer at connect time.</summary>
public static class IpAddressClassifier
{
    // Same rules as the download engine: both link src/Shared/NetworkAddressPolicy.cs.
    public static bool IsPublic(IPAddress ip) => Vesper.Shared.NetworkAddressPolicy.IsPublic(ip);
}
