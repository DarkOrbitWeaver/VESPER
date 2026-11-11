using System.Security.Cryptography;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires Windows current-user DPAPI; not executable on Linux."; }
}

public sealed class ProtectedMediaStateTests
{
    [Fact]
    public void LegacyPlaintext_RemainsReadableWithoutRewritingIt()
    {
        Assert.Null(ProtectedMediaState.Unprotect(null));
        Assert.Equal("https://example.com/media?token=legacy", ProtectedMediaState.Unprotect("https://example.com/media?token=legacy"));
    }

    [WindowsFact]
    public void DpapiRoundTrip_DoesNotPersistTheSignedUrlInPlaintext()
    {
        const string value = "{\"Url\":\"https://example.com/stream?token=private\"}";
        string ciphertext = ProtectedMediaState.Protect(value)!;
        Assert.StartsWith("vesper-dpapi:1:", ciphertext);
        Assert.DoesNotContain("token", ciphertext);
        Assert.Equal(value, ProtectedMediaState.Unprotect(ciphertext));
        Assert.Same(ciphertext, ProtectedMediaState.Protect(value));
    }

    [WindowsFact]
    public void MalformedProtectedState_FailsWithoutAnyPlaintextFallback()
    {
        Assert.Throws<CryptographicException>(() => ProtectedMediaState.Unprotect("vesper-dpapi:1:not valid base64!"));
    }
}
