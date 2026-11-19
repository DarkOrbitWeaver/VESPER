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

            var updated = new VesperPreferences
            {
                OpenFolderWhenQueueFinishes = true,
                SleepWhenQueueFinishes = true
            };
            Assert.Null(SettingsStore.Save(updated, directory));

            VesperPreferences loaded = SettingsStore.Load(directory);
            Assert.True(loaded.OpenFolderWhenQueueFinishes);
            Assert.True(loaded.SleepWhenQueueFinishes);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CopyPreferencesPreservesQueueCompletionActions()
    {
        var source = new VesperPreferences
        {
            OpenFolderWhenQueueFinishes = true,
            SleepWhenQueueFinishes = true
        };
        var destination = new VesperPreferences();

        Form1.CopyPreferences(source, destination);

        Assert.True(destination.OpenFolderWhenQueueFinishes);
        Assert.True(destination.SleepWhenQueueFinishes);
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
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void SleepBoundarySkipsCancellationWorkAndShutdown(
        bool approved, bool hasUnfinishedWork, bool shuttingDown)
    {
        int lockReleases = 0;
        int sleepRequests = 0;

        Form1.SleepPromptResult result = Form1.RequestSleepAfterPrompt(approved, shuttingDown,
            () => hasUnfinishedWork, () => lockReleases++, () => { sleepRequests++; return true; });

        Assert.Equal(Form1.SleepPromptResult.Skipped, result);
        Assert.Equal(0, lockReleases);
        Assert.Equal(0, sleepRequests);
    }

    [Fact]
    public void SleepBoundaryCallsInjectedActionOnlyAfterThePromptAndQueueChecksPass()
    {
        int lockReleases = 0;
        int sleepRequests = 0;

        Form1.SleepPromptResult result = Form1.RequestSleepAfterPrompt(approved: true, shuttingDown: false,
            hasUnfinishedWork: () => false, releaseSleepLock: () => lockReleases++,
            requestSleep: () => { sleepRequests++; return true; });

        Assert.Equal(Form1.SleepPromptResult.Slept, result);
        Assert.Equal(1, lockReleases);
        Assert.Equal(1, sleepRequests);
    }

    [Fact]
    public void SleepBoundaryReportsWindowsRefusalWithoutRetrying()
    {
        int sleepRequests = 0;

        Form1.SleepPromptResult result = Form1.RequestSleepAfterPrompt(approved: true, shuttingDown: false,
            hasUnfinishedWork: () => false, releaseSleepLock: () => { },
            requestSleep: () => { sleepRequests++; return false; });

        Assert.Equal(Form1.SleepPromptResult.Failed, result);
        Assert.Equal(1, sleepRequests);
    }

    [Fact]
    public void SleepBoundaryRechecksForNewWorkAfterReleasingTheAwakeRequest()
    {
        int checks = 0;
        int sleepRequests = 0;

        Form1.SleepPromptResult result = Form1.RequestSleepAfterPrompt(approved: true, shuttingDown: false,
            hasUnfinishedWork: () => ++checks == 2, releaseSleepLock: () => { },
            requestSleep: () => { sleepRequests++; return true; });

        Assert.Equal(Form1.SleepPromptResult.Skipped, result);
        Assert.Equal(2, checks);
        Assert.Equal(0, sleepRequests);
    }

}
