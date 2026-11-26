using Vesper.Engine;
using VESPER;

namespace Vesper.Windows.Tests;

public sealed class DownloadActionEligibilityTests
{
    [Fact]
    public void Recovery_actions_follow_job_kind_and_reason()
    {
        var missingPieceMap = new JobSnapshot
        {
            Kind = JobKind.Http, State = JobState.NeedsAttention,
            Reason = "recovery-integrity-missing", CommittedBytes = 100
        };

        Assert.False(DownloadActionEligibility.CanResume(missingPieceMap));
        Assert.True(DownloadActionEligibility.CanReplaceOrRefresh(missingPieceMap));
        Assert.True(DownloadActionEligibility.CanStartOver(missingPieceMap));

        var browserStream = new JobSnapshot
        {
            Kind = JobKind.BrowserStream, State = JobState.Failed, CommittedBytes = 100
        };

        Assert.False(DownloadActionEligibility.CanReplaceOrRefresh(browserStream));
        Assert.False(DownloadActionEligibility.CanStartOver(browserStream));

        var refreshableHls = new JobSnapshot
        {
            Kind = JobKind.Hls, State = JobState.NeedsLink,
            OriginalPageUrl = "https://example.test/watch", MediaProvider = "test",
            MediaId = "video", MediaFormatId = "format"
        };

        Assert.True(DownloadActionEligibility.CanReplaceOrRefresh(refreshableHls));
        Assert.True(DownloadActionEligibility.IsRefreshOnly(refreshableHls));
        Assert.Equal("Refresh original media link", DownloadActionEligibility.ReplacementLabel(refreshableHls));

        var hlsSubtitle = new JobSnapshot
        {
            Kind = JobKind.Hls, State = JobState.NeedsLink,
            OriginalPageUrl = "https://example.test/watch", MediaProvider = "test",
            MediaId = "video", MediaFormatId = "subtitle:en"
        };

        Assert.False(DownloadActionEligibility.CanReplaceOrRefresh(hlsSubtitle));
        Assert.False(DownloadActionEligibility.IsRefreshOnly(hlsSubtitle));
    }

    [Fact]
    public void Chrome_owned_files_only_offer_folder_reveal()
    {
        var browserFile = new JobSnapshot { Kind = JobKind.BrowserFile, State = JobState.Completed };
        var missingBrowserFile = new JobSnapshot { Kind = JobKind.BrowserFile, State = JobState.Completed, FileMissing = true };
        var missingNativeFile = new JobSnapshot { Kind = JobKind.Http, State = JobState.Completed, FileMissing = true };

        Assert.False(DownloadActionEligibility.CanOpenFile(browserFile));
        Assert.False(DownloadActionEligibility.CanLocateFile(browserFile));
        Assert.True(DownloadActionEligibility.CanShowFolder(browserFile));
        Assert.False(DownloadActionEligibility.CanOpenFile(missingBrowserFile));
        Assert.False(DownloadActionEligibility.CanLocateFile(missingBrowserFile));
        Assert.True(DownloadActionEligibility.CanShowFolder(missingBrowserFile));
        Assert.False(DownloadActionEligibility.CanOpenFile(missingNativeFile));
        Assert.True(DownloadActionEligibility.CanLocateFile(missingNativeFile));
    }
}
