using Vesper.Engine;
using Vesper.MediaResolver.Models;

namespace VESPER;

/// <summary>
/// Turns validated browser requests into engine calls. VESPER chooses new download destinations.
/// A file already saved by Chrome can be listed at its verified existing location.
/// "Success" is reported only after the engine has actually accepted the job.
/// </summary>
internal sealed partial class BrowserBridgeHandler(Form1 owner, DownloadEngine engine, Func<VesperPreferences> preferences)
{
    public async Task<BridgeResponse> HandleAsync(BridgeRequest request, CancellationToken ct)
    {
        switch (request.Type)
        {
            case "hello":
                return BridgeResponse.Success(request.Id, "VESPER is running.",
                    data: new { app = "VESPER", handoff = preferences().BrowserAction.ToString().ToLowerInvariant(),
                        build = Vesper.Shared.BuildInfo.Version(typeof(BrowserBridgeHandler).Assembly) },
                    version: Vesper.Shared.BuildInfo.Number(typeof(BrowserBridgeHandler).Assembly));

            case "probe":
                return BridgeResponse.Success(request.Id, data: await engine.ProbeMediaAsync(request.Url!, ct));

            case "media-picker":
                return await NativeMediaPickerAsync(request, ct);

            case "media-options":
                return await MediaOptionsAsync(request, ct);

            case "media-select":
                return SelectBrowserMedia(request);

            case "batch-review":
            case "submit":
            case "browser-submit":
                return await SubmitAsync(request, ct);

            case "browser-transfer":
                return await BrowserTransferAsync(request, ct);

            case "browser-activity":
                return await WatchBrowserActivityAsync(request, ct);

            default:
                return BridgeResponse.Fail(request.Id, "invalid", "Unknown request.");
        }
    }

    /// <summary>
    /// Browser and native Add Download share the same quality picker, resolver, format rules
    /// and engine. The browser may supply only validated media hints, never filenames or paths.
    /// </summary>
    private async Task<BridgeResponse> NativeMediaPickerAsync(BridgeRequest request, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? page))
            return BridgeResponse.Fail(request.Id, "invalid", "The media page address is invalid.");

        var hints = MediaHints(request, page);
        var finished = new TaskCompletionSource<(IReadOnlyList<MediaDownloadChoice> Jobs, bool Queue)>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            owner.BeginInvoke(() =>
            {
                try
                {
                    owner.RequestShow();
                    using var picker = new MediaDiscoveryForm(preferences(), page, hints);
                    DialogResult outcome = picker.ShowDialog(owner);
                    finished.TrySetResult(outcome == DialogResult.OK
                        ? (picker.Selections, picker.QueueOnly)
                        : (Array.Empty<MediaDownloadChoice>(), false));
                }
                catch (Exception ex)
                {
                    finished.TrySetException(ex);
                }
            });
            (IReadOnlyList<MediaDownloadChoice> jobs, bool queue) = await finished.Task.WaitAsync(ct);
            if (jobs.Count == 0) return BridgeResponse.Fail(request.Id, "cancelled", "No media was added.");
            var results = new List<BridgeResult>();
            foreach (var (job, i) in jobs.Select((item, index) => (item, index)))
            {
                results.Add(QueueMedia(job, queue, $"{request.Id}:{i}"));
            }
            return results.Any(x => x.Status is "started" or "queued" or "already-submitted")
                ? BridgeResponse.Success(request.Id, results: results)
                : BridgeResponse.Fail(request.Id, "rejected", "No compatible media was accepted.");
        }
        catch (OperationCanceledException)
        {
            return BridgeResponse.Fail(request.Id, "cancelled", "The media chooser was cancelled.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return BridgeResponse.Fail(request.Id, "app-unavailable", "VESPER couldn't open its media chooser.");
        }
    }

    private async Task<BridgeResponse> SubmitAsync(BridgeRequest request, CancellationToken ct)
    {
        // "ask" from the browser means "apply VESPER's own handoff setting"; download/queue are explicit overrides.
        string mode = request.Mode == "ask" ? preferences().BrowserAction.ToString().ToLowerInvariant() : request.Mode;
        if (request.Type == "browser-submit" && mode == "ask") mode = "download";

        IReadOnlySet<int>? selected = null;
        IReadOnlySet<int> trustedChecksums = new HashSet<int>();
        bool startNow = mode == "download";
        string? folder = null;
        bool requiresReview = RequiresNativeReview(request);
        if (requiresReview) mode = "ask";
        if (requiresReview)
        {
            var reviewed = await ReviewBatchAsync(request, ct);
            if (reviewed.Outcome == QuickDownloadForm.Outcome.Cancel || reviewed.Selected.Count == 0)
                return BridgeResponse.Fail(request.Id, "cancelled", "No files were selected in VESPER.");
            startNow = reviewed.Outcome == QuickDownloadForm.Outcome.Download;
            folder = reviewed.Folder;
            selected = reviewed.Selected;
            trustedChecksums = reviewed.TrustedChecksums;
        }
        else if (mode == "ask")
        {
            (QuickDownloadForm.Outcome outcome, string chosen) = await AskUserAsync(request, ct);
            if (outcome == QuickDownloadForm.Outcome.Cancel)
                return BridgeResponse.Fail(request.Id, "cancelled", "Cancelled in VESPER.");
            startNow = outcome == QuickDownloadForm.Outcome.Download;
            folder = chosen;
        }

        var results = new List<BridgeResult>();
        for (int i = 0; i < request.Items.Count; i++)
        {
            if (selected is not null && !selected.Contains(i)) continue;
            BridgeItem item = request.Items[i];
            Dictionary<string, string>? secrets = null;
            if (item.Cookie is not null || item.Authorization is not null)
            {
                secrets = [];
                if (item.Cookie is not null) secrets["Cookie"] = item.Cookie;
                if (item.Authorization is not null) secrets["Authorization"] = item.Authorization;
            }

            AddResult added = engine.Add(new AddRequest(item.Url, folder, item.FileName, startNow, item.Referer, item.UserAgent,
                RequestId: $"{request.Id}:{i}", ExpectedSha256: ConfirmedChecksum(item, i, trustedChecksums),
                Kind: item.Kind == "hls" ? JobKind.Hls : JobKind.Http, Secrets: secrets, BrowserProof: request.BrowserProof));

            string status = added.Status switch
            {
                AddStatus.Added => startNow ? "started" : "queued",
                AddStatus.AlreadySubmitted => "already-submitted",
                AddStatus.Duplicate => "duplicate",
                AddStatus.DestinationUnavailable => "destination",
                _ => "invalid"
            };
            bool? transferReady = null;
            string? transferReason = null;
            if (request.ConfirmTransfer && added.Ok)
                (transferReady, transferReason) = await ConfirmTransferAsync(added.JobId, startNow, request.BrowserProof is not null, ct);
            results.Add(new BridgeResult(UrlPolicy.Redact(item.Url), status,
                added.Ok || added.Status == AddStatus.Duplicate ? added.JobId.ToString("D") : null, added.Message,
                transferReady, transferReason));
        }

        bool anyAccepted = results.Any(r => r.Status is "started" or "queued" or "already-submitted");
        if (anyAccepted) return BridgeResponse.Success(request.Id, results: results);
        bool allDuplicate = results.All(r => r.Status == "duplicate");
        return new BridgeResponse(BridgeProtocol.Version, request.Id, false, allDuplicate ? "duplicate" : "rejected", results[0].Message, results);
    }

    internal static string? ConfirmedChecksum(BridgeItem item, int index, IReadOnlySet<int> confirmed) =>
        confirmed.Contains(index) ? item.Sha256 : null;

    internal static bool RequiresNativeReview(BridgeRequest request) => request.Type == "batch-review"
        || request.Items.Count > 1 || request.Items.Any(item => item.Sha256 is not null);

    /// <summary>
    /// Waits until VESPER has really received the file's first response. When it has not, the second value says why
    /// (the job's own reason, or "queued", "busy", "slow"), so Chrome can tell the user why it kept the download.
    /// </summary>
    private async Task<(bool Ready, string? Reason)> ConfirmTransferAsync(Guid id, bool startNow, bool staged, CancellationToken ct)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? reason = null;
        void Inspect(JobSnapshot snapshot)
        {
            if (snapshot.Id != id) return;
            if (snapshot.State == JobState.Completed && !snapshot.FileMissing ||
                (staged ? snapshot.Reason == "browser-ready" && snapshot.State == JobState.Downloading
                    : snapshot.ProgressBytes > 0 && snapshot.State is (JobState.Downloading or JobState.Verifying or JobState.Finalizing or JobState.Converting)))
                ready.TrySetResult(true);
            else if (snapshot.State is JobState.NeedsLink or JobState.NeedsAttention or JobState.Failed
                or JobState.Cancelled or JobState.Suspect or JobState.Paused or JobState.Completed)
            {
                if (ready.TrySetResult(false)) reason = TransferReasonCode(snapshot.Reason);
            }
        }
        void InspectProgress(IReadOnlyList<JobSnapshot> snapshots)
        {
            foreach (JobSnapshot snapshot in snapshots) Inspect(snapshot);
        }

        engine.JobChanged += Inspect;
        engine.Progress += InspectProgress;
        try
        {
            if (engine.Snapshot(id) is not { } current) return (false, "unavailable");
            Inspect(current);
            // Queued downloads can wait indefinitely. Keep Chrome running if VESPER
            // cannot confirm receipt before this request returns.
            if (!startNow)
                return ready.Task.IsCompletedSuccessfully && ready.Task.Result ? (true, null) : (false, reason ?? "queued");
            try { return await ready.Task.WaitAsync(TimeSpan.FromSeconds(8), ct) ? (true, null) : (false, reason ?? "unavailable"); }
            catch (TimeoutException)
            {
                // Still waiting for a free download slot, or the server has not answered yet.
                return (false, engine.Snapshot(id)?.State == JobState.Queued ? "busy" : "slow");
            }
        }
        finally
        {
            engine.JobChanged -= Inspect;
            engine.Progress -= InspectProgress;
        }
    }

    // Engine reasons are short lowercase codes such as "auth" or "browser-fallback". Anything else is not passed on.
    private static string TransferReasonCode(string reason) =>
        reason.Length is > 0 and <= 32 && reason.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
            ? reason : "unavailable";

    private async Task<BridgeResponse> BrowserTransferAsync(BridgeRequest request, CancellationToken ct)
    {
        string id = request.TransferId!;
        if (request.Operation == "accept" && !engine.AcceptBrowserTransfer(id))
            return BridgeResponse.Fail(request.Id, "unavailable", "VESPER could not take over this download.");
        if (request.Operation == "abandon") engine.AbandonBrowserTransfer(id);

        string State()
        {
            JobSnapshot? snapshot = engine.BrowserTransferSnapshot(id);
            if (snapshot?.State == JobState.Cancelled && engine.BrowserTransferIsRunning(id)) return "stopping";
            string current = BrowserTransferState(snapshot);
            if (current != "complete") return current;
            if (snapshot is not { FinalPath: { } path, TotalBytes: long expected }) return "failed";
            FileCheck file = PathSafety.Inspect(path);
            return file is { Exists: true, IsDirectory: false, IsReparsePoint: false, IsCloudPlaceholder: false }
                && file.Length == expected ? "complete" : "failed";
        }
        string state = State();
        if (request.Operation == "watch" && state == "active")
        {
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Guid? jobId = engine.BrowserTransferSnapshot(id)?.Id;
            void Changed(JobSnapshot snapshot)
            {
                if (snapshot.Id == jobId && BrowserTransferState(snapshot) != "active") terminal.TrySetResult();
            }
            void Removed(Guid removed) { if (removed == jobId) terminal.TrySetResult(); }
            engine.JobChanged += Changed;
            engine.JobRemoved += Removed;
            try
            {
                if (State() == "active") await terminal.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
            }
            catch (TimeoutException) { }
            finally { engine.JobChanged -= Changed; engine.JobRemoved -= Removed; }
            state = State();
        }
        return BridgeResponse.Success(request.Id, data: new { state });
    }

    private static string BrowserTransferState(JobSnapshot? snapshot) => snapshot switch
    {
        { State: JobState.Completed, FileMissing: false } => "complete",
        null or { State: JobState.Failed or JobState.Suspect or JobState.Cancelled or JobState.NeedsLink or JobState.NeedsAttention
            or JobState.Completed } => "failed",
        _ => "active"
    };

    /// <summary>Shows the compact Quick Download window on the UI thread and waits for the user's choice.</summary>
    private async Task<(QuickDownloadForm.Outcome Outcome, string Folder, IReadOnlySet<int> Selected,
        IReadOnlySet<int> TrustedChecksums)> ReviewBatchAsync(
        BridgeRequest request, CancellationToken ct)
    {
        var done = new TaskCompletionSource<(QuickDownloadForm.Outcome, string, IReadOnlySet<int>, IReadOnlySet<int>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.BeginInvoke(() =>
        {
            if (ct.IsCancellationRequested) { done.TrySetCanceled(ct); return; }
            try
            {
                owner.RequestShow();
                using var form = new BatchReviewForm(preferences(), request.Items);
                using var registration = ct.Register(() =>
                {
                    if (!form.IsDisposed && form.IsHandleCreated)
                        try { form.BeginInvoke(() => form.Close()); } catch (InvalidOperationException) { }
                });
                form.ShowDialog(owner);
                done.TrySetResult((form.Result, form.Destination, form.SelectedIndices, form.TrustedChecksumIndices));
            }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        return await done.Task.WaitAsync(ct);
    }

    private async Task<(QuickDownloadForm.Outcome, string)> AskUserAsync(BridgeRequest request, CancellationToken ct)
    {
        BridgeItem first = request.Items[0];
        string title, detail;
        long? size = null;

        if (request.Items.Count == 1)
        {
            title = first.FileName ?? FileNames.FromUrl(new Uri(first.Url));
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            MediaProbeResult probe = await engine.ProbeMediaAsync(first.Url, probeTimeout.Token);
            size = probe.ContentLength;
            detail = DescribeProbe(probe);
        }
        else
        {
            title = $"{request.Items.Count} files";
            detail = "Sent from your browser";
        }

        var done = new TaskCompletionSource<(QuickDownloadForm.Outcome, string)>();
        owner.BeginInvoke(() =>
        {
            using var form = new QuickDownloadForm(preferences(), title, detail, size);
            form.ShowDialog();
            done.TrySetResult((form.Result, form.Destination));
        });
        await using (ct.Register(() => done.TrySetCanceled(ct))) return await done.Task;
    }

    private static string DescribeProbe(MediaProbeResult probe)
    {
        var parts = new List<string>();
        string kind = probe.Kind switch { "hls-master" or "hls-media" => "HLS video", "direct" => FriendlyType(probe.ContentType), _ => "" };
        if (kind.Length > 0) parts.Add(kind);
        if (probe.ContentLength is long length) parts.Add(Formatting.Bytes(length));
        if (probe.DurationSeconds is double seconds) parts.Add(TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss"));
        if (probe.Kind is "unreachable" or "unsupported" or "invalid") parts.Add(probe.Message);
        return parts.Count == 0 ? "Details are not available until the download starts." : string.Join(" · ", parts);
    }

    private static string FriendlyType(string? mime) => mime switch
    {
        "video/mp4" => "MP4 video", "video/webm" => "WebM video", "audio/mpeg" => "MP3 audio", "audio/mp4" => "M4A audio",
        "application/zip" => "ZIP archive", "application/pdf" => "PDF document", null => "", _ => mime
    };
}
