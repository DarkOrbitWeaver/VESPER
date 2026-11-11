namespace Vesper.MediaResolver.Core;

/// <summary>All limits are enforced; none are advisory. Defaults are deliberately conservative.</summary>
public sealed class ResolverOptions
{
    public int MaxPageBytes { get; init; } = 6 * 1024 * 1024;
    public int MaxManifestBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxJsonBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxErrorBodyBytes { get; init; } = 256 * 1024;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Whole-call deadline, including fallback, manifests, queueing and body reads.</summary>
    public TimeSpan DiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(35);

    /// <summary>Called once per completed lookup; measurements contain no source URLs.</summary>
    public Action<DiscoveryMeasurement>? Measurement { get; init; }

    public int MaxRedirects { get; init; } = 5;
    public int MaxConcurrentRequests { get; init; } = 4;

    public int MaxCollectionItems { get; init; } = 500;
    public int MaxCollectionPages { get; init; } = 20;

    /// <summary>YouTube InnerTube client profiles to try, in order (see YouTubeClients). Repairs need no code change.</summary>
    public IReadOnlyList<string> YouTubeClients { get; init; } = ["visionos"];
    public int MaxSegmentsPerRepresentation { get; init; } = 50_000;
    public int MaxCandidatesPerPage { get; init; } = 24;
    public int MaxManifestsPerPage { get; init; } = 3;

    /// <summary>Disables the private-network (SSRF) guard. Only for tests/loopback fixtures.</summary>
    public bool AllowPrivateNetworks { get; init; }

    /// <summary>
    /// When true the system proxy is used and DNS/IP filtering at connect time is delegated to the proxy
    /// (only literal-host checks remain). Default false.
    /// </summary>
    public bool UseSystemProxy { get; init; }

    public string UserAgent { get; init; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) VesperMediaResolver/0.1";

    /// <summary>Lazy metadata cache. Zero disables it. There is no timer: expiry is checked on access.</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromSeconds(90);
    public int CacheMaxEntries { get; init; } = 32;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Optional developer diagnostics. Messages never contain secrets (URLs are redacted).</summary>
    public Action<string>? Diagnostics { get; init; }
}
