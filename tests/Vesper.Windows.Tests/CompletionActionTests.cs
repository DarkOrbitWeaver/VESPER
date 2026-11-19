using Vesper.Engine;
using VESPER;

namespace Vesper.Windows.Tests;

public sealed class CompletionActionTests
{
    [Fact]
    public void NewQueueCompletionActionsDefaultOffAndPersist()
    {
        string directory = Directory.CreateTempSubdirectory("vesper-completion-actions-").FullName;
        try
        {
            VesperPreferences defaults = SettingsStore.Load(directory);
            Assert.False(defaults.OpenFolderWhenQueueFinishes);
            Assert.False(defaults.SleepWhenQueueFinishes);
            Assert.False(defaults.ShutdownWhenQueueFinishes);

            var updated = new VesperPreferences
            {
                OpenFolderWhenQueueFinishes = true,
                SleepWhenQueueFinishes = true,
                ShutdownWhenQueueFinishes = true
            };
            Assert.Null(SettingsStore.Save(updated, directory));

            VesperPreferences loaded = SettingsStore.Load(directory);
            Assert.True(loaded.OpenFolderWhenQueueFinishes);
            Assert.True(loaded.SleepWhenQueueFinishes);
            Assert.True(loaded.ShutdownWhenQueueFinishes);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CopyPreferencesPreservesQueueCompletionActions()
    {
        var source = new VesperPreferences
        {
            OpenFolderWhenQueueFinishes = true,
            SleepWhenQueueFinishes = true,
            ShutdownWhenQueueFinishes = true
        };
        var destination = new VesperPreferences();

        Form1.CopyPreferences(source, destination);

        Assert.True(destination.OpenFolderWhenQueueFinishes);
        Assert.True(destination.SleepWhenQueueFinishes);
        Assert.True(destination.ShutdownWhenQueueFinishes);
    }

    [Fact]
    public void OnlyCompletedOrCancelledNativeJobsCountAsFinished()
    {
        JobSnapshot[] finished =
        [
            new() { State = JobState.Completed },
            new() { State = JobState.Cancelled },
            new() { Kind = JobKind.BrowserFile, State = JobState.Downloading },
            new() { IsUnacceptedBrowserCapture = true, State = JobState.NeedsAttention }
        ];
        JobSnapshot[] unfinished =
        [
            new() { State = JobState.Completed },
            new() { State = JobState.Failed }
        ];

        Assert.False(Form1.HasUnfinishedQueueWork(finished));
        Assert.True(Form1.HasUnfinishedQueueWork(unfinished));
    }

    [Fact]
    public void CancellingTheLastJobFinishesTheQueueAndUsesTheLatestCompletedFolder()
    {
        Guid firstCompletedId = Guid.NewGuid();
        Guid latestCompletedId = Guid.NewGuid();
        var firstCompleted = new JobSnapshot
        {
            Id = firstCompletedId, State = JobState.Completed, CompletedUtc = DateTime.UtcNow.AddMinutes(-2)
        };
        var latestCompleted = new JobSnapshot
        {
            Id = latestCompletedId, State = JobState.Completed, CompletedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        var cancelled = new JobSnapshot { State = JobState.Cancelled };
        JobSnapshot[] current = [firstCompleted, latestCompleted, cancelled];

        Assert.True(Form1.QueueFinishedAfterTransitions([cancelled], current));
        Assert.Equal(latestCompletedId, Form1.LatestCompletedQueueJob(current)?.Id);
        Assert.False(Form1.QueueFinishedAfterTransitions([cancelled],
            current.Append(new JobSnapshot { State = JobState.Queued })));
        Assert.False(Form1.QueueFinishedAfterTransitions(
            [new JobSnapshot { State = JobState.Failed }], [new JobSnapshot { State = JobState.Failed }]));
    }

    [Theory]
    [InlineData((int)Form1.QueuePowerAction.None, true, false, false)]
    [InlineData((int)Form1.QueuePowerAction.Sleep, false, false, false)]
    [InlineData((int)Form1.QueuePowerAction.Shutdown, true, true, false)]
    [InlineData((int)Form1.QueuePowerAction.Sleep, true, false, true)]
    public void QueueActionBoundarySkipsCancellationDisabledActionsWorkAndShutdown(
        int actionValue, bool enabled, bool shuttingDown, bool hasUnfinishedWork)
    {
        Form1.QueuePowerAction action = (Form1.QueuePowerAction)actionValue;
        int lockReleases = 0, sleepRequests = 0, shutdownRequests = 0;
        Form1.QueueActionResult result = Form1.RequestQueuePowerAction(action, () => enabled, () => shuttingDown,
            () => hasUnfinishedWork, () => lockReleases++, () => { sleepRequests++; return true; },
            () => { shutdownRequests++; return true; });

        Assert.Equal(Form1.QueueActionResult.Skipped, result);
        Assert.Equal(0, lockReleases);
        Assert.Equal(0, sleepRequests);
        Assert.Equal(0, shutdownRequests);
    }

    [Theory]
    [InlineData((int)Form1.QueuePowerAction.Sleep)]
    [InlineData((int)Form1.QueuePowerAction.Shutdown)]
    public void QueueActionBoundaryCallsOnlyTheUserSelectedInjectedAction(int actionValue)
    {
        Form1.QueuePowerAction action = (Form1.QueuePowerAction)actionValue;
        int lockReleases = 0, sleepRequests = 0, shutdownRequests = 0;
        Form1.QueueActionResult result = Form1.RequestQueuePowerAction(action, () => true, () => false,
            () => false, () => lockReleases++, () => { sleepRequests++; return true; },
            () => { shutdownRequests++; return true; });

        Assert.Equal(Form1.QueueActionResult.Requested, result);
        Assert.Equal(1, lockReleases);
        Assert.Equal(action == Form1.QueuePowerAction.Sleep ? 1 : 0, sleepRequests);
        Assert.Equal(action == Form1.QueuePowerAction.Shutdown ? 1 : 0, shutdownRequests);
    }

    [Theory]
    [InlineData((int)Form1.QueuePowerAction.Sleep)]
    [InlineData((int)Form1.QueuePowerAction.Shutdown)]
    public void QueueActionBoundaryReportsInjectedOsRefusalWithoutRetrying(int actionValue)
    {
        Form1.QueuePowerAction action = (Form1.QueuePowerAction)actionValue;
        int sleepRequests = 0, shutdownRequests = 0;
        Form1.QueueActionResult result = Form1.RequestQueuePowerAction(action, () => true, () => false,
            () => false, () => { }, () => { sleepRequests++; return false; },
            () => { shutdownRequests++; return false; });

        Assert.Equal(Form1.QueueActionResult.Failed, result);
        Assert.Equal(action == Form1.QueuePowerAction.Sleep ? 1 : 0, sleepRequests);
        Assert.Equal(action == Form1.QueuePowerAction.Shutdown ? 1 : 0, shutdownRequests);
    }

    [Fact]
    public void QueueActionBoundaryRechecksForNewWorkAfterReleasingTheAwakeRequest()
    {
        int checks = 0;
        int sleepRequests = 0, shutdownRequests = 0;

        Form1.QueueActionResult result = Form1.RequestQueuePowerAction(Form1.QueuePowerAction.Sleep,
            () => true, () => false, () => ++checks == 2, () => { },
            () => { sleepRequests++; return true; }, () => { shutdownRequests++; return true; });

        Assert.Equal(Form1.QueueActionResult.Skipped, result);
        Assert.Equal(2, checks);
        Assert.Equal(0, sleepRequests);
        Assert.Equal(0, shutdownRequests);
    }

    [Fact]
    public void QueueActionBoundaryRechecksSettingsAndAppShutdownAfterReleasingTheAwakeRequest()
    {
        int settingChecks = 0, shutdownChecks = 0, sleepRequests = 0;

        Form1.QueueActionResult disabledResult = Form1.RequestQueuePowerAction(Form1.QueuePowerAction.Sleep,
            () => ++settingChecks == 1, () => false, () => false, () => { },
            () => { sleepRequests++; return true; }, () => true);
        Form1.QueueActionResult shutdownResult = Form1.RequestQueuePowerAction(Form1.QueuePowerAction.Shutdown,
            () => true, () => ++shutdownChecks == 2, () => false, () => { },
            () => true, () => { sleepRequests++; return true; });

        Assert.Equal(Form1.QueueActionResult.Skipped, disabledResult);
        Assert.Equal(Form1.QueueActionResult.Skipped, shutdownResult);
        Assert.Equal(2, settingChecks);
        Assert.Equal(2, shutdownChecks);
        Assert.Equal(0, sleepRequests);
    }
}
