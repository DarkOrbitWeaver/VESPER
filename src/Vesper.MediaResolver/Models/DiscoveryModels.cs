using Vesper.MediaResolver.Security;
using Vesper.MediaResolver.Core;

namespace Vesper.MediaResolver.Models;

public sealed class DiscoveryOptions
{
    public CollectionIntent CollectionIntent { get; init; } = CollectionIntent.Auto;
    public string? PageToken { get; init; }
    public int MaxCollectionItems { get; init; } = 200;
    public bool ResolveEmbeds { get; init; } = true;
    public bool IncludeSubtitles { get; init; } = true;

    /// <summary>Internal recursion guard for embedded players.</summary>
    public int EmbedDepth { get; init; }
}

/// <summary>Source metadata is independent of transfer compatibility. A poster never implies a playable stream.</summary>
public sealed record DiscoveryProgress(string? Title = null, Uri? Thumbnail = null, int RepresentationCount = 0);

public sealed class MediaDiscoveryRequest
{
    public required Uri Url { get; init; }

    /// <summary>Optional browser-supplied context (Mode B). Validated before use.</summary>
    public BrowserDiscoveryMessage? Browser { get; init; }

    public DiscoveryOptions Options { get; init; } = new();

    /// <summary>Optional incremental metadata; implementations must not block the network pipeline.</summary>
    public IProgress<DiscoveryProgress>? Progress { get; init; }

    internal HttpTextResponse? PageResponse { get; set; }

    internal void Publish(DiscoveryProgress update)
    {
        DiscoveryTrace.Current.Value?.Observe(update);
        try { Progress?.Report(update); }
        catch (Exception) { /* Optional observer failures must not change discovery results. */ }
    }

    public DiscoveryMode Mode => Browser is null ? DiscoveryMode.Native : DiscoveryMode.BrowserAssisted;

    /// <summary>Set by the resolver after validating <see cref="Browser"/>.</summary>
    internal ValidatedBrowserContext? ValidatedBrowser { get; set; }

    internal MediaRequestContext AuthContext => ValidatedBrowser?.Context ?? MediaRequestContext.Empty;
}

public sealed class MediaDiscoveryResult
{
    public DiscoveryStatus Status { get; init; }
    public MediaItem? Item { get; init; }
    public MediaCollection? Collection { get; init; }
    public MediaError? Error { get; init; }
    public IReadOnlyList<MediaError> Warnings { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool IsSuccess => Status != DiscoveryStatus.Failed;
    public bool NeedsBrowserContext => Error?.BrowserMayHelp == true;

    public static MediaDiscoveryResult FromItem(MediaItem item, IEnumerable<MediaError>? warnings = null, IEnumerable<string>? notes = null) =>
        new() { Status = DiscoveryStatus.Success, Item = item, Warnings = warnings?.ToList() ?? [], Notes = notes?.ToList() ?? [] };

    public static MediaDiscoveryResult FromCollection(MediaCollection c, IEnumerable<MediaError>? warnings = null, IEnumerable<string>? notes = null)
    {
        var w = warnings?.ToList() ?? [];
        return new()
        {
            Status = c.IsComplete && w.Count == 0 ? DiscoveryStatus.Success : DiscoveryStatus.Partial,
            Collection = c,
            Warnings = w,
            Notes = notes?.ToList() ?? []
        };
    }

    public static MediaDiscoveryResult Failure(MediaError error) => new() { Status = DiscoveryStatus.Failed, Error = error };

    public override string ToString() => Status == DiscoveryStatus.Failed
        ? $"Failed: {Error}"
        : $"{Status}: {(Item is not null ? Item.Title : Collection?.Title)}";
}
