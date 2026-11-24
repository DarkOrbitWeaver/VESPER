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

    private const string Modified = "Tue, 03 Nov 2026 10:00:00 GMT";

    [Fact]
    public void A_strong_last_modified_with_an_exact_length_is_a_proof()
    {
        var proof = new BrowserTransferProof(4096, null, "application/zip", LastModified: Modified, Basis: "last-modified");

        Assert.True(proof.IsValid);
        Assert.Contains("last-modified", BrowserTransferProof.AcceptedBases);
    }

    [Theory]
    [InlineData(null, null, Modified)]                       // no length
    [InlineData(0L, null, Modified)]                         // an empty file proves nothing
    [InlineData(4096L, null, null)]                          // no date
    [InlineData(4096L, null, "2026-11-03T10:00:00Z")]        // not an HTTP date
    [InlineData(4096L, "\"file-v1\"", Modified)]             // an ETag must be judged as an ETag
    public void A_last_modified_proof_needs_an_exact_length_an_http_date_and_no_etag(long? length, string? etag, string? modified)
    {
        var proof = new BrowserTransferProof(length, etag, "application/zip", LastModified: modified, Basis: "last-modified");

        Assert.False(proof.IsValid);
    }

    [Fact]
    public void Matching_metadata_without_a_validator_is_still_never_a_proof()
    {
        var proof = new BrowserTransferProof(4096, null, "application/zip", LastModified: Modified, FileName: "archive.zip",
            Attachment: true, Basis: "metadata");

        Assert.False(proof.IsValid);
    }

    [Fact]
    public void Negative_length_is_rejected_even_with_a_strong_etag()
    {
        var proof = new BrowserTransferProof(-1, "\"file-v1\"", "application/octet-stream");

        Assert.False(proof.IsValid);
    }
}
