using Vesper.MediaResolver.Security;

namespace Vesper.MediaResolver.Models;

public enum MediaErrorCode
{
    UnsupportedSite,
    NoDownloadableMedia,
    VideoUnavailable,
    LoginRequired,
    RegionRestricted,
    SourceExpired,
    DrmProtected,
    UnsupportedManifest,
    RateLimited,
    NetworkFailure,
    AuthorizationRequired,
    SourceChanged,
    PlaylistPartiallyResolved,
    BrowserContextRequired,
    BlockedBySecurityPolicy,
    ResponseTooLarge,
    LiveStreamNotSupported,
    InvalidInput,
    NoMatchingFormat,
    /// <summary>Internal signal: this adapter does not recognise the page; try the next one.</summary>
    NotThisSite,
    InternalError
}

/// <summary>Structured, UI-ready error. <see cref="Diagnostic"/> is for developers and never contains secrets.</summary>
public sealed record MediaError(
    MediaErrorCode Code,
    string UserMessage,
    string Diagnostic,
    bool IsRetryable,
    bool BrowserMayHelp,
    bool IsPermanent)
{
    public string? EntryId { get; init; }

    public override string ToString() => $"{Code}: {UserMessage}";
}

public sealed class MediaResolverException : Exception
{
    public MediaError Error { get; }

    public MediaResolverException(MediaError error) : base($"{error.Code}: {error.UserMessage}") => Error = error;
}

public static class MediaErrors
{
    public static MediaError UnsupportedSite(Uri url) => new(MediaErrorCode.UnsupportedSite,
        "This website is not supported.", $"No adapter or generic media found for {UrlRedactor.Redact(url)}", false, true, true);

    public static MediaError NoMedia(string diagnostic, bool browserMayHelp = true) => new(MediaErrorCode.NoDownloadableMedia,
        "No downloadable media was found on this page.", diagnostic, false, browserMayHelp, false);

    public static MediaError Unavailable(string diagnostic) => new(MediaErrorCode.VideoUnavailable,
        "This media is unavailable (removed, private or never existed).", diagnostic, false, false, true);

    public static MediaError LoginRequired(string diagnostic) => new(MediaErrorCode.LoginRequired,
        "This media requires you to be signed in or to have access permission.", diagnostic, false, true, false);

    public static MediaError Region(string diagnostic) => new(MediaErrorCode.RegionRestricted,
        "This media is not available in your region.", diagnostic, false, false, true);

    public static MediaError Expired(string diagnostic) => new(MediaErrorCode.SourceExpired,
        "The media link has expired. Resolve the page again to get a fresh link.", diagnostic, true, true, false);

    public static MediaError Drm(string diagnostic) => new(MediaErrorCode.DrmProtected,
        "This media is DRM-protected and cannot be downloaded.", diagnostic, false, false, true);

    public static MediaError UnsupportedManifest(string diagnostic) => new(MediaErrorCode.UnsupportedManifest,
        "The streaming manifest uses features that are not supported.", diagnostic, false, false, true);

    public static MediaError RateLimited(string diagnostic) => new(MediaErrorCode.RateLimited,
        "The website is limiting requests. Please try again later.", diagnostic, true, false, false);

    public static MediaError Network(string diagnostic, bool retry = true) => new(MediaErrorCode.NetworkFailure,
        "A network problem prevented discovery.", diagnostic, retry, false, false);

    public static MediaError AuthRequired(string diagnostic) => new(MediaErrorCode.AuthorizationRequired,
        "The website refused access. Browser-assisted discovery with your signed-in session may help.", diagnostic, false, true, false);

    public static MediaError SourceChanged(string diagnostic) => new(MediaErrorCode.SourceChanged,
        "The website changed its format and this adapter could not read it.", diagnostic, false, true, false);

    public static MediaError PartialPlaylist(string diagnostic) => new(MediaErrorCode.PlaylistPartiallyResolved,
        "Only part of this collection could be listed.", diagnostic, true, false, false);

    public static MediaError BrowserContext(string diagnostic) => new(MediaErrorCode.BrowserContextRequired,
        "Open the page in the browser and use the VESPER extension to supply the media.", diagnostic, false, true, false);

    public static MediaError Blocked(string diagnostic) => new(MediaErrorCode.BlockedBySecurityPolicy,
        "The address was blocked by VESPER's safety rules.", diagnostic, false, false, true);

    public static MediaError TooLarge(string diagnostic) => new(MediaErrorCode.ResponseTooLarge,
        "The response was too large to process safely.", diagnostic, false, false, true);

    public static MediaError Live(string diagnostic) => new(MediaErrorCode.LiveStreamNotSupported,
        "Live streams are not supported.", diagnostic, false, false, false);

    public static MediaError Invalid(string diagnostic) => new(MediaErrorCode.InvalidInput,
        "The link or request is not valid.", diagnostic, false, false, true);

    public static MediaError NoFormat(string diagnostic) => new(MediaErrorCode.NoMatchingFormat,
        "No available format satisfies the chosen quality preference.", diagnostic, false, false, false);

    public static MediaError NotThisSite(string diagnostic) => new(MediaErrorCode.NotThisSite,
        "Not handled by this adapter.", diagnostic, false, false, false);

    public static MediaError Internal(Exception ex) => new(MediaErrorCode.InternalError,
        "An unexpected problem occurred while reading this media.", $"{ex.GetType().Name}: {ex.Message}", false, false, false);

    /// <summary>Maps an HTTP status from a metadata request to a user-meaningful error.</summary>
    public static MediaError FromHttpStatus(int status, Uri url)
    {
        string diag = $"HTTP {status} from {UrlRedactor.Redact(url)}";
        return status switch
        {
            401 => LoginRequired(diag),
            403 => AuthRequired(diag),
            404 or 410 => Unavailable(diag),
            429 => RateLimited(diag),
            451 => Region(diag),
            >= 500 => Network(diag),
            _ => Network(diag, false)
        };
    }
}
