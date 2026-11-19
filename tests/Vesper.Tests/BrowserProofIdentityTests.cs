using Vesper.Engine;

namespace Vesper.Tests;

public sealed class BrowserProofIdentityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(4096L)]
    public void Strong_etag_is_required_and_length_is_optional(long? length)
    {
        var proof = new BrowserTransferProof(length, "\"file-v1\"", "application/octet-stream");

        Assert.True(proof.IsValid);
    }

    [Fact]
    public void Length_alone_does_not_identify_a_browser_source()
    {
        var proof = new BrowserTransferProof(4096, null, "application/octet-stream");

        Assert.False(proof.IsValid);
    }

    [Theory]
    [InlineData("W/\"file-v1\"")]
    [InlineData("*")]
    [InlineData("file-v1")]
    [InlineData("\"unterminated")]
    [InlineData(" \"file-v1\"")]
    [InlineData("\"file-v1\" ")]
    public void Weak_or_malformed_etags_are_rejected(string etag)
    {
        var proof = new BrowserTransferProof(4096, etag, "application/octet-stream");

        Assert.False(proof.IsValid);
    }

    [Fact]
    public void Negative_length_is_rejected_even_with_a_strong_etag()
    {
        var proof = new BrowserTransferProof(-1, "\"file-v1\"", "application/octet-stream");

        Assert.False(proof.IsValid);
    }
}
