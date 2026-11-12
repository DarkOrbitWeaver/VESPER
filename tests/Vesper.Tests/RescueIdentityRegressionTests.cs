using Vesper.Engine;

namespace Vesper.Tests;

public sealed class RescueIdentityRegressionTests
{
    private static readonly Uri Replacement = new("https://cdn.example.com/f/file.iso?token=new");
    private static readonly DateTimeOffset Modified = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DownloadJob StrongEtagJob() => new()
    {
        SourceUrl = "https://cdn.example.com/f/file.iso?token=old",
        TotalBytes = 1000,
        Identity = IdentityLevel.StrongEtag,
        ETag = "\"p1\"",
        LastModified = Modified
    };

    [Fact]
    public void R0_rejects_a_missing_strong_etag_even_when_size_and_time_match()
    {
        var probe = new ProbeFacts(206, true, 1000, null, Modified, true);

        RescueDecision decision = RescueEvaluator.Evaluate(StrongEtagJob(), Replacement, probe, null, null);

        Assert.False(decision.Accepted);
        Assert.Equal(RescueMode.R4, decision.Mode);
    }

    [Fact]
    public void A_trusted_full_hash_keeps_the_existing_R1_verification_path_available()
    {
        var probe = new ProbeFacts(206, true, 1000, null, Modified, true);

        RescueDecision decision = RescueEvaluator.Evaluate(StrongEtagJob(), Replacement, probe, new string('a', 64), null);

        Assert.True(decision.Accepted);
        Assert.Equal(RescueMode.R1, decision.Mode);
    }
}
