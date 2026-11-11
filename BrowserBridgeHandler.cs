using Vesper.Engine;
using Vesper.MediaResolver.Models;

namespace VESPER;

/// <summary>
/// Turns validated browser requests into engine calls. The browser only ever supplies links and optional hints;
/// where a file is saved is decided here (settings or the Quick Download window), never by the request.
/// "Success" is reported only after the engine has actually accepted the job.
/// </summary>
internal sealed class BrowserBridgeHandler(Form1 owner, DownloadEngine engine, Func<VesperPreferences> preferences)
{
    public async Task<BridgeResponse> HandleAsync(BridgeRequest request, CancellationToken ct)
    {
        switch (request.Type)
        {
            case "hello":
                return BridgeResponse.Success(request.Id, "VESPER is running.",
                    data: new { app = "VESPER", handoff = preferences().BrowserAction.ToString().ToLowerInvariant() }, version: Application.ProductVersion);

            case "probe":
                return BridgeResponse.Success(request.Id, data: await engine.ProbeMediaAsync(request.Url!, ct));

            case "media-picker":
                return await NativeMediaPickerAsync(request, ct);

            case "submit":
                return await SubmitAsync(request, ct);

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

        var hints = new BrowserDiscoveryMessage
        {
            Schema = BrowserDiscoveryMessage.CurrentSchema,
            PageUrl = page.AbsoluteUri,
            Title = request.PageTitle,
            Candidates = (request.Candidates ?? []).Select(h => new BrowserMediaCandidate
            { Url = h.Url, Kind = h.Kind, MimeType = h.MimeType, Source = "network" }).ToList()
        };
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
                AddResult added = engine.Add(new AddRequest(job.Url, StartNow: !queue,
                    FileName: job.FileName, Referer: job.Referer, UserAgent: job.UserAgent,
                    Kind: job.Kind, RequestId: $"{request.Id}:{i}", OriginalPageUrl: job.OriginalPageUrl,
                    MediaProvider: job.Provider, MediaId: job.MediaId, MediaQuality: job.Quality,
                    MediaThumbnailUrl: job.ThumbnailUrl, MediaExpiresAt: job.ExpiresAt,
                    MediaPlanJson: job.MediaPlanJson, MediaFormatId: job.FormatId));
                results.Add(new BridgeResult(UrlPolicy.Redact(job.Url),
                    added.Status == AddStatus.Added ? queue ? "queued" : "started"
                    : added.Status == AddStatus.AlreadySubmitted ? "already-submitted" : "rejected",
                    added.Ok ? added.JobId.ToString("D") : null, added.Message));
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

        bool startNow = mode == "download";
        string? folder = null;
        if (mode == "ask")
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
            BridgeItem item = request.Items[i];
            Dictionary<string, string>? secrets = null;
            if (item.Cookie is not null || item.Authorization is not null)
            {
                secrets = [];
                if (item.Cookie is not null) secrets["Cookie"] = item.Cookie;
                if (item.Authorization is not null) secrets["Authorization"] = item.Authorization;
            }

            AddResult added = engine.Add(new AddRequest(item.Url, folder, item.FileName, startNow, item.Referer, item.UserAgent,
                RequestId: $"{request.Id}:{i}", ExpectedSha256: item.Sha256,
                Kind: item.Kind == "hls" ? JobKind.Hls : JobKind.Http, Secrets: secrets));

            string status = added.Status switch
            {
                AddStatus.Added => startNow ? "started" : "queued",
                AddStatus.AlreadySubmitted => "already-submitted",
                AddStatus.Duplicate => "duplicate",
                AddStatus.DestinationUnavailable => "destination",
                _ => "invalid"
            };
            results.Add(new BridgeResult(UrlPolicy.Redact(item.Url), status, added.Ok || added.Status == AddStatus.Duplicate ? added.JobId.ToString("D") : null, added.Message));
        }

        bool anyAccepted = results.Any(r => r.Status is "started" or "queued" or "already-submitted");
        if (anyAccepted) return BridgeResponse.Success(request.Id, results: results);
        bool allDuplicate = results.All(r => r.Status == "duplicate");
        return new BridgeResponse(BridgeProtocol.Version, request.Id, false, allDuplicate ? "duplicate" : "rejected", results[0].Message, results);
    }

    /// <summary>Shows the compact Quick Download window on the UI thread and waits for the user's choice.</summary>
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
