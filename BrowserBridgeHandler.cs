using Vesper.MediaResolver.Core;
using Vesper.Engine;
using Vesper.MediaResolver.Models;

namespace VESPER;

/// <summary>
/// Turns validated browser requests into engine calls. VESPER chooses new download destinations.
/// "Success" is reported only after the engine has actually accepted the job.
/// </summary>
internal sealed partial class BrowserBridgeHandler(Form1 owner, DownloadEngine engine, Func<VesperPreferences> preferences)
{
    // Network settings are fixed for the engine lifetime. Share the resolver's
    // bounded HTTP pool and metadata cache across concurrent picker requests.
    private readonly object _resolverGate = new();
    private DefaultMediaResolver? _sharedResolver;
    private bool _resolverClosed;
    private DefaultMediaResolver SharedResolver
    {
        get
        {
            lock (_resolverGate)
            {
                ObjectDisposedException.ThrowIf(_resolverClosed, this);
                return _sharedResolver ??= new DefaultMediaResolver(EngineHost.MediaNetwork(options: engine.Options));
            }
        }
    }
    internal void CloseResolver()
    {
        lock (_resolverGate)
        {
            _resolverClosed = true;
            _sharedResolver?.Dispose();
            _sharedResolver = null;
        }
    }

    public async Task<BridgeResponse> HandleAsync(BridgeRequest request, CancellationToken ct)
    {
        switch (request.Type)
        {
            case "hello":
            {
                VesperPreferences current = preferences();
                return BridgeResponse.Success(request.Id, "VESPER is running.",
                    data: new { app = "VESPER", handoff = current.BrowserAction.ToString().ToLowerInvariant(),
                        siteRules = CurrentBrowserSiteRules(),
                        proofs = BrowserTransferProof.AcceptedBases,
                        managedDownloads = true,
                        build = Vesper.Shared.BuildInfo.Version(typeof(BrowserBridgeHandler).Assembly) },
                    version: Vesper.Shared.BuildInfo.Number(typeof(BrowserBridgeHandler).Assembly));
            }

            case "capture-preflight":
                return BridgeResponse.Success(request.Id, "VESPER is ready for a browser download.",
                    data: new { ready = true, siteRules = CurrentBrowserSiteRules(), proofs = BrowserTransferProof.AcceptedBases });

            case "probe":
                return BridgeResponse.Success(request.Id, data: await engine.ProbeMediaAsync(request.Url!, ct));

            case "media-picker":
                return await NativeMediaPickerAsync(request, ct);

            case "media-options":
            case "media-source-options":
            case "media-range-options":
            case "media-group-options":
                return await MediaOptionsAsync(request, ct);

            case "media-select":
                return SelectBrowserMedia(request);

            case "batch-review":
            case "submit":
            case "browser-submit":
            case "browser-capture":
                return await SubmitAsync(request, ct);

            case "browser-body":
                if (request.BrowserBody is { Operation: "begin", Automatic: true, Origin: { } origin }
                    && SiteRule.ForPage(SiteRule.Normalize(preferences().SiteRules), origin) is { NeverCapture: true })
                    return BridgeResponse.Fail(request.Id, "site-capture-disabled", "");
                return await engine.ReceiveBrowserBodyAsync(request.Id, request.BrowserBody!, ct);

            case "browser-transfer":
                return await BrowserTransferAsync(request, ct);

            case "browser-diagnostics":
                return engine.RecordBrowserDiagnostics(request.Diagnostics!)
                    ? BridgeResponse.Success(request.Id)
                    : BridgeResponse.Fail(request.Id, "diagnostic-storage", "Could not save the browser diagnostics.");

            case "browser-managed":
                return await ManageChromeDownloadAsync(request, ct);

            case "browser-activity":
                // Chrome-owned downloads must never appear as VESPER jobs, even from older extensions.
                return BridgeResponse.Fail(request.Id, "chrome-owned", "Chrome downloads stay in Chrome.");

            default:
                return BridgeResponse.Fail(request.Id, "invalid", "Unknown request.");
        }
    }

    private async Task<BridgeResponse> ManageChromeDownloadAsync(BridgeRequest request, CancellationToken ct)
    {
        if (request.Operation == "complete")
            return BridgeResponse.Fail(request.Id, "chrome-owned", "Chrome-owned downloads stay in Chrome history.");

        BrowserActivity activity = request.Activity!;
        BrowserActivity allowed = FilterManagedActivity(activity, preferences().SiteRules, out string[] blocked);
        if (request.Operation == "connect")
        {
            if (blocked.Length > 0)
                return BridgeResponse.Fail(request.Id, "site-capture-disabled", "This site is set to keep downloads in Chrome.");
            owner.RequestShow();
            await owner.ApplyBrowserActivityAsync(allowed, ct, reconcile: false);
            IReadOnlyList<string> neverCaptureHosts = SiteRule.Normalize(preferences().SiteRules)
                .Where(rule => rule.NeverCapture).Select(rule => rule.Host).ToArray();
            return BridgeResponse.Success(request.Id, data: new { managedDownloads = true, siteRules = new { neverCaptureHosts }, blocked });
        }

        return await WatchManagedBrowserActivityAsync(request.Id, allowed, blocked, ct);
    }

    private static BrowserActivity FilterManagedActivity(BrowserActivity activity, IEnumerable<SiteRule>? rules,
        out string[] blocked)
    {
        var allowedFiles = new List<BrowserActiveFile>();
        var blockedFiles = new List<string>();
        foreach (BrowserActiveFile file in activity.Files)
        {
            if (BlockedHost(file.Hosts!, rules) is not null) blockedFiles.Add(file.Id);
            else allowedFiles.Add(file);
        }
        blocked = [.. blockedFiles];
        return new BrowserActivity(activity.Profile, allowedFiles);
    }

    private static string? BlockedHost(IReadOnlyList<string> hosts, IEnumerable<SiteRule>? rules)
    {
        IReadOnlyList<SiteRule> siteRules = SiteRule.Normalize(rules);
        foreach (string host in hosts)
        {
            string address = host.Contains(':') ? "https://[" + host + "]/" : "https://" + host + "/";
            if (SiteRule.ForPage(siteRules, address) is { NeverCapture: true }) return host;
        }
        return null;
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
                if (ct.IsCancellationRequested) { finished.TrySetCanceled(ct); return; }
                try
                {
                    owner.ShowFromTray();
                    using var picker = new MediaDiscoveryForm(preferences(), page, hints);
                    using var registration = ActivateReview(picker, ct);
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
            bool primaryAccepted = false;
            for (int index = 0; index < jobs.Count; index++)
            {
                var job = jobs[index];
                if (job.IsSubtitle && !primaryAccepted) continue;
                var result = QueueMedia(job, queue, $"{request.Id}:{index}");
                results.Add(result);
                if (!job.IsSubtitle) primaryAccepted = result.JobId is not null;
            }
            return results.Any(x => x.Subtitle is not true && x.JobId is not null)
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
        bool browserCapture = request.Type is "browser-submit" or "browser-capture";
        if (browserCapture)
        {
            IReadOnlyList<SiteRule> siteRules = SiteRule.Normalize(preferences().SiteRules);
            if (request.Items.Any(item => IsNeverCaptureRule(item, siteRules)))
                return BridgeResponse.Fail(request.Id, "site-capture-disabled", "This site is set to keep automatic downloads in Chrome.");
        }

        // "ask" from the browser means "apply VESPER's own handoff setting"; download/queue are explicit overrides.
        string mode = request.Mode == "ask" ? preferences().BrowserAction.ToString().ToLowerInvariant() : request.Mode;
        if (browserCapture && mode == "ask") mode = "download";

        IReadOnlySet<int>? selected = null;
        IReadOnlySet<int> trustedChecksums = new HashSet<int>();
        bool startNow = mode == "download";
        string? folder = SiteFolderFor(request.Items);
        bool requiresReview = RequiresNativeReview(request);
        if (requiresReview) mode = "ask";
        if (requiresReview)
        {
            var reviewed = await ReviewBatchAsync(request, folder, ct);
            if (reviewed.Outcome == QuickDownloadForm.Outcome.Cancel || reviewed.Selected.Count == 0)
                return BridgeResponse.Fail(request.Id, "cancelled", "No files were selected in VESPER.");
            startNow = reviewed.Outcome == QuickDownloadForm.Outcome.Download;
            folder = reviewed.Folder;
            selected = reviewed.Selected;
            trustedChecksums = reviewed.TrustedChecksums;
        }
        else if (mode == "ask")
        {
            (QuickDownloadForm.Outcome outcome, string chosen) = await AskUserAsync(request, folder, ct);
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

            var addRequest = new AddRequest(item.Url, folder, item.FileName, startNow, item.Referer, item.UserAgent,
                RequestId: $"{request.Id}:{i}", ExpectedSha256: ConfirmedChecksum(item, i, trustedChecksums),
                Kind: item.Kind == "hls" ? JobKind.Hls : JobKind.Http, Secrets: secrets, BrowserProof: request.BrowserProof);

            // A download waiting for a new link continues from this one instead of a second copy being started.
            if (request.ConfirmTransfer && await engine.OfferFreshLinkAsync(addRequest, ct) is { } waiting)
            {
                results.Add(new BridgeResult(UrlPolicy.Redact(item.Url), "started", waiting.ToString("D"),
                    "VESPER will continue its saved download from this link.", true, null));
                continue;
            }

            AddResult added = engine.Add(addRequest);

            string status = added.Status switch
            {
                AddStatus.Added => startNow && !added.StartDeferred ? "started" : "queued",
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
        if (anyAccepted)
        {
            if (!browserCapture && results.Any(r => r.Status is "started" or "queued"))
                owner?.RequestBrowserPresentation("submit:" + request.Id, explicitRequest: true);
            return BridgeResponse.Success(request.Id, results: results);
        }
        bool allDuplicate = results.All(r => r.Status == "duplicate");
        return new BridgeResponse(BridgeProtocol.Version, request.Id, false, allDuplicate ? "duplicate" : "rejected", results[0].Message, results);
    }

    internal static string? ConfirmedChecksum(BridgeItem item, int index, IReadOnlySet<int> confirmed) =>
        confirmed.Contains(index) ? item.Sha256 : null;

    internal static bool RequiresNativeReview(BridgeRequest request) => request.Type == "batch-review"
        || request.Items.Count > 1 || request.Items.Any(item => item.Sha256 is not null);

    private static bool IsNeverCaptureRule(BridgeItem item, IReadOnlyList<SiteRule> siteRules) =>
        SiteRule.ForPage(siteRules, item.Referer)?.NeverCapture == true
        || SiteRule.ForPage(siteRules, item.OriginalUrl)?.NeverCapture == true
        || SiteRule.ForPage(siteRules, item.Url)?.NeverCapture == true;

    private object CurrentBrowserSiteRules() => new
    {
        neverCaptureHosts = SiteRule.Normalize(preferences().SiteRules)
            .Where(rule => rule.NeverCapture).Select(rule => rule.Host).ToArray()
    };

    internal string? SiteFolderFor(IReadOnlyList<BridgeItem> items)
    {
        if (items.Count == 0) return null;
        VesperPreferences current = preferences();
        SiteRule? first = SiteRule.ForPage(current.SiteRules, items[0].Referer ?? items[0].Url);
        if (first?.Folder is not { Length: > 0 } folder) return null;
        foreach (BridgeItem item in items)
        {
            SiteRule? rule = SiteRule.ForPage(current.SiteRules, item.Referer ?? item.Url);
            if (!string.Equals(rule?.Host, first.Host, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return folder;
    }

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
            bool transferReady = staged
                ? snapshot.State == JobState.Downloading && snapshot.Reason == "browser-ready"
                : snapshot.State == JobState.Completed && !snapshot.FileMissing
                    || snapshot.ProgressBytes > 0 && snapshot.State is (JobState.Downloading or JobState.Verifying or JobState.Finalizing or JobState.Converting);
            if (transferReady) ready.TrySetResult(true);
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
        if (request.Operation == "accept" && !await engine.AcceptBrowserTransferAsync(id, ct))
            return BridgeResponse.Fail(request.Id, "unavailable", "VESPER could not take over this download.");
        if (request.Operation == "accept")
            owner?.RequestBrowserPresentation("transfer:" + id, explicitRequest: false);
        if (request.Operation == "abandon") engine.AbandonBrowserTransfer(id);

        string State()
        {
            JobSnapshot? snapshot = engine.BrowserTransferSnapshot(id);
            if (snapshot?.State == JobState.Cancelled && engine.BrowserTransferIsRunning(id)) return "stopping";
            string current = BrowserTransferState(snapshot);
            if (request.Operation == "status" && current == "active" && snapshot is { IsUnacceptedBrowserCapture: true })
                return "staged";
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
        IReadOnlySet<int> TrustedChecksums)> ReviewBatchAsync(BridgeRequest request, string? siteFolder, CancellationToken ct)
    {
        var done = new TaskCompletionSource<(QuickDownloadForm.Outcome, string, IReadOnlySet<int>, IReadOnlySet<int>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.BeginInvoke(() =>
        {
            if (ct.IsCancellationRequested) { done.TrySetCanceled(ct); return; }
            try
            {
                owner.ShowFromTray();
                VesperPreferences formPreferences = preferences().Clone();
                if (siteFolder is not null) formPreferences.DefaultFolder = siteFolder;
                using var form = new BatchReviewForm(formPreferences, request.Items);
                using var registration = ActivateReview(form, ct);
                form.ShowDialog(owner);
                done.TrySetResult((form.Result, form.Destination, form.SelectedIndices, form.TrustedChecksumIndices));
            }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        return await done.Task.WaitAsync(ct);
    }

    private async Task<(QuickDownloadForm.Outcome, string)> AskUserAsync(BridgeRequest request, string? siteFolder, CancellationToken ct)
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

        var done = new TaskCompletionSource<(QuickDownloadForm.Outcome, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.BeginInvoke(() =>
        {
            if (ct.IsCancellationRequested) { done.TrySetCanceled(ct); return; }
            try
            {
                owner.ShowFromTray();
                VesperPreferences formPreferences = preferences().Clone();
                if (siteFolder is not null) formPreferences.DefaultFolder = siteFolder;
                using var form = new QuickDownloadForm(formPreferences, title, detail, size);
                using var registration = ActivateReview(form, ct);
                form.ShowDialog(owner);
                done.TrySetResult((form.Result, form.Destination));
            }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        return await done.Task.WaitAsync(ct);
    }

    private static CancellationTokenRegistration ActivateReview(Form form, CancellationToken ct)
    {
        form.Shown += (_, _) =>
        {
            if (ct.IsCancellationRequested) form.BeginInvoke(() => form.Close());
            else form.Activate();
        };
        return ct.Register(() =>
        {
            if (!form.IsDisposed && form.IsHandleCreated)
                try { form.BeginInvoke(() => form.Close()); } catch (InvalidOperationException) { }
        });
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
