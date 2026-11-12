using Vesper.Engine;

namespace Vesper.Tests;

public sealed class CredentialOriginSurvivesRescueTests
{
    [Fact]
    public async Task Replacing_a_link_does_not_move_saved_credentials_to_the_new_origin()
    {
        await using var original = new FaultServer();
        await using var replacement = new FaultServer();
        await original.StartAsync();
        await replacement.StartAsync();
        using var harness = new Harness();

        var credentials = new Dictionary<string, string>
        {
            ["Cookie"] = "session=original-secret",
            ["Authorization"] = "Bearer original-secret"
        };
        AddResult added = harness.Engine.Add(new AddRequest(original.Url("/file"), FileName: "rescued.zip",
            StartNow: false, Secrets: credentials, ExpectedSha256: Payloads.Sha256(replacement.V1)));
        Assert.True(added.Ok, added.Message);

        RescueResult rescued = await harness.Engine.ReplaceLinkAsync(added.JobId, replacement.Url("/file"));
        Assert.True(rescued.Accepted, rescued.Message);
        JobSnapshot done = await harness.WaitForStateAsync(added.JobId, JobState.Completed);

        Assert.Equal(replacement.V1, await File.ReadAllBytesAsync(done.FinalPath!));
        FaultServer.ObservedHeaders request = Assert.Single(replacement.RequestsFor("/file"));
        Assert.Null(request.Cookie);
        Assert.Null(request.Authorization);
    }
}
