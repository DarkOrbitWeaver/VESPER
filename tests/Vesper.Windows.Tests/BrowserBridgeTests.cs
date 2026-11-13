using VESPER;
using Vesper.MediaResolver.Models;

namespace Vesper.Windows.Tests;

public sealed class BrowserBridgeTests
{
    [Theory]
    [InlineData(MediaErrorCode.NoDownloadableMedia, "no-media")]
    [InlineData(MediaErrorCode.VideoUnavailable, "unavailable")]
    [InlineData(MediaErrorCode.SourceExpired, "source-expired")]
    [InlineData(MediaErrorCode.LoginRequired, "sign-in")]
    [InlineData(MediaErrorCode.DrmProtected, "protected")]
    [InlineData(MediaErrorCode.UnsupportedManifest, "unsupported")]
    [InlineData(MediaErrorCode.LiveStreamNotSupported, "unsupported")]
    [InlineData(MediaErrorCode.BrowserContextRequired, "browser-context")]
    [InlineData(MediaErrorCode.NetworkFailure, "network")]
    [InlineData(MediaErrorCode.InternalError, "resolver-error")]
    [InlineData(MediaErrorCode.ResponseTooLarge, "unsupported")]
    public void ResolverFailuresKeepTheirUserFacingReason(MediaErrorCode code, string expected)
    {
        Assert.Equal(expected, BrowserBridgeHandler.MapResolverError(code));
    }
}
