using Vesper.MediaResolver.Discovery;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Protocols;

namespace Vesper.MediaResolver.Core;

/// <summary>Shared per-resolver infrastructure handed to adapters: one HTTP client, one manifest resolver.</summary>
public sealed class ResolverServices : IDisposable
{
    public ResolverOptions Options { get; }
    public SecureHttpClient Http { get; }
    public ManifestResolver Manifests { get; }
    internal CandidateProcessor Candidates { get; }

    /// <summary>Set by <see cref="DefaultMediaResolver"/>; lets the generic adapter resolve supported embedded players.</summary>
    public Func<MediaDiscoveryRequest, CancellationToken, Task<MediaDiscoveryResult>>? EmbeddedResolver { get; set; }

    public DateTimeOffset Now => Options.TimeProvider.GetUtcNow();

    public ResolverServices(ResolverOptions options, HttpMessageHandler? handler = null)
    {
        Options = options;
        Http = new SecureHttpClient(options, handler);
        Manifests = new ManifestResolver(Http, options);
        Candidates = new CandidateProcessor(Http, Manifests, options);
    }

    public void Dispose() => Http.Dispose();
}
