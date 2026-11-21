using Vesper.Engine;

namespace Vesper.Tests;

public sealed class TransferProgressTests
{
    [Theory]
    [InlineData(JobState.Downloading, 99, true)]
    [InlineData(JobState.Downloading, 100, false)]
    [InlineData(JobState.Paused, 100, true)]
    [InlineData(JobState.Verifying, 100, false)]
    [InlineData(JobState.Finalizing, 100, false)]
    [InlineData(JobState.Converting, 100, false)]
    [InlineData(JobState.Completed, 100, false)]
    public void Byte_progress_is_not_processing_progress(JobState state, int bytes, bool show)
    {
        var snapshot = new JobSnapshot { State = state, TotalBytes = 100, ProgressBytes = bytes };
        Assert.Equal(show, snapshot.ShowTransferProgress);
        Assert.Equal(state == JobState.Downloading && bytes == 100, snapshot.FinishingTransfer);
    }
}