using Vesper.Engine;

namespace Vesper.Tests;

public sealed class MeteredNetworkTests
{
    [Fact]
    public async Task Metered_connection_waits_and_start_now_overrides_one_job()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        NetworkCostStatus cost = NetworkCostStatus.Metered;
        using var harness = new Harness(tweak: options =>
        {
            options.PauseOnMeteredNetwork = true;
            options.NetworkCostProvider = () => cost;
        });

        Guid id = harness.Add(server.Url("/file"), startNow: false);
        JobSnapshot waiting = await harness.WaitForStateAsync(id, JobState.WaitingForNetwork);

        Assert.Equal("metered-network", waiting.Reason);
        Assert.Contains("metered", waiting.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.Hits("/file"));

        harness.Engine.StartNow(id);
        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(server.V1.Length, done.CommittedBytes);
        Assert.True(server.Hits("/file") > 0);
    }

    [Fact]
    public async Task Unknown_cost_is_shown_separately_and_unmetered_event_wakes_the_job()
    {
        await using var server = new FaultServer();
        await server.StartAsync();
        NetworkCostStatus cost = NetworkCostStatus.Unknown;
        using var harness = new Harness(tweak: options =>
        {
            options.PauseOnMeteredNetwork = true;
            options.NetworkCostProvider = () => cost;
        });

        Guid id = harness.Add(server.Url("/file"), startNow: false);
        JobSnapshot waiting = await harness.WaitForStateAsync(id, JobState.WaitingForNetwork);

        Assert.Equal("network-cost-unknown", waiting.Reason);
        Assert.Contains("cannot report", waiting.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.Hits("/file"));

        cost = NetworkCostStatus.Unmetered;
        harness.Engine.RefreshMeteredNetworkStatus();
        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(server.V1.Length, done.CommittedBytes);
        Assert.True(server.Hits("/file") > 0);
    }

    [Fact]
    public async Task Metered_transition_checkpoints_running_bytes_and_resumes_when_unmetered()
    {
        await using var server = new FaultServer(size: 2 * 1024 * 1024);
        await server.StartAsync();
        NetworkCostStatus cost = NetworkCostStatus.Unmetered;
        using var harness = new Harness(tweak: options =>
        {
            options.PauseOnMeteredNetwork = true;
            options.NetworkCostProvider = () => cost;
        });

        Guid id = harness.Add(server.Url("/slow"), startNow: false);
        await harness.WaitAsync(id, job => job.State == JobState.Downloading && job.CommittedBytes >= 64 * 1024);

        cost = NetworkCostStatus.Metered;
        harness.Engine.RefreshMeteredNetworkStatus();
        JobSnapshot waiting = await harness.WaitAsync(id, job => job.State == JobState.WaitingForNetwork
            && job.Reason == "metered-network");
        Assert.True(waiting.CommittedBytes >= 64 * 1024);

        cost = NetworkCostStatus.Unmetered;
        harness.Engine.RefreshMeteredNetworkStatus();
        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(server.V1.Length, done.CommittedBytes);
        Assert.Equal(server.V1, await File.ReadAllBytesAsync(done.FinalPath!));
    }
}
