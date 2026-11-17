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

    [Fact]
    public async Task Replacing_a_partial_strong_etag_download_without_an_etag_keeps_all_saved_bytes()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        using var harness = new Harness();

        Guid id = harness.Add(server.Url("/expires-without-etag"), "etag-recovery.bin");
        JobSnapshot stalled = await harness.WaitForStateAsync(id, JobState.NeedsLink);
        Assert.True(stalled.CommittedBytes > 0);
        byte[] saved = await File.ReadAllBytesAsync(stalled.PartialPath);

        RescueResult attempt = await harness.Engine.ReplaceLinkAsync(id, server.Url("/expires-without-etag?token=fresh"));
        JobSnapshot after = Assert.IsType<JobSnapshot>(harness.Engine.Snapshot(id));

        Assert.False(attempt.Accepted);
        Assert.Equal(RescueMode.R4, attempt.Mode);
        Assert.Equal(JobState.NeedsLink, after.State);
        Assert.Equal(stalled.SourceUrl, after.SourceUrl);
        Assert.Equal(stalled.CommittedBytes, after.CommittedBytes);
        Assert.Equal(saved, await File.ReadAllBytesAsync(stalled.PartialPath));
        Assert.True(File.Exists(stalled.PartialPath));
    }
}
