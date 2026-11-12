# Integration guide (historical resolver boundary)

This guide records the initial resolver integration contract. Its API examples may predate the current main branch; see [current release status](../../RELEASE.md) and [current media limits](../../../MEDIA_ENGINE_LIMITATIONS.md).

## Ownership boundary

| Concern | Owner |
|---|---|
| Find media, formats, playlists, subtitles, expiry, request context; produce a validated `DownloadPlan` | **Vesper.MediaResolver** |
| Transfers, scheduling, segmentation, resume, partial files, recovery, integrity, finalisation, storage, history | **VESPER engine** (existing) |
| Quality picker, progress, error presentation | **VESPER UI** |
| Page context, user consent, native messaging | **Chrome extension** |

The resolver has no queue, no database, no downloader, no timers. Persist preferences yourself (`PreferenceStore.ToJson/FromJson`).

## Lifetime and threading

* Create **one** `DefaultMediaResolver` per application (or per profile) and dispose it on exit. Construction allocates almost nothing;
  the shared `HttpClient`, cache and adapters appear on first use. It is thread-safe.
* Every call is bounded by `ResolverOptions` (timeouts, byte limits, redirect limit, concurrency 4, collection caps). Always pass a
  `CancellationToken`; cancellation throws `OperationCanceledException` (the only exception `ResolveAsync` throws for runtime conditions).
* All other failures are returned as `MediaDiscoveryResult.Failure(MediaError)`.
* `ResolverOptions.UseSystemProxy` defaults to **false** (connect-time private-network filtering needs direct sockets). If your users
  require a corporate proxy, enable it knowingly: only literal-host checks remain.

## The calls you need

```csharp
// Mode A (URL pasted): item, or collection when the URL is a playlist page
var r = await resolver.ResolveAsync(new MediaDiscoveryRequest { Url = url }, ct);

// Mode B (extension): same call, same models, plus the validated browser message
var msg = BrowserMessageParser.Parse(jsonFromNativeMessaging);
var r2 = await resolver.ResolveAsync(new MediaDiscoveryRequest { Url = new Uri(msg.PageUrl), Browser = msg }, ct);

// Quality
var menu = QualityMenuBuilder.BuildDefault(item, userBaselinePreferences);       // 4 distinct choices
var sel  = FormatSelector.Select(item, new FormatPreferences { Mode = QualityMode.Custom, TargetHeight = 2160 });
var plan = DownloadPlanBuilder.Build(item, sel.Selection!);                      // validates URLs, expiry, DASH segment plans

// Collections (lazy; the user decides what to queue)
var snap = await new CollectionPager(resolver).CollectAsync(new MediaDiscoveryRequest { Url = playlistUrl }, ct);

// Batch: ONE preference for N items, per-item overrides, failure-isolated, requires explicit authorisation
var batch = await new BatchResolver(resolver).ResolveAsync(new BatchRequest
{
    Entries = snap.Entries.Select(e => new BatchEntry(e.Url, e.MediaId, e.Resolved)).ToList(),
    Preferences = FormatPreferences.Height(1080),
    UserAuthorizedBatch = true            // set ONLY after the user confirmed the batch
}, progress, ct);
```

A URL with `list=` and a video id resolves to the **video only** (`item.Related` describes the collection, `IsDynamic = true` for Mix/radio).
Pass `CollectionIntent.CollectionOnly` (or use `CollectionPager`) to list the collection; dynamic mixes are snapshots and flagged partial.

## The hand-off: `DownloadPlan` / `TransferStep`

`plan.Steps` is 1 step (complete stream) or 2 (video + audio). Each step carries everything the engine needs:

* `Protocol`: `Http` -> ordinary (segmented/resumable) download of `Url`.
  `Hls` / `Dash` -> fetch `Segments` in order (`InitializationUrl` first, then each `MediaSegment`, honouring `Range`), concatenate;
  fragmented MP4/TS concatenation of a single representation is valid without re-encoding. If `Segments` is null on Hls, `Url` is the media playlist.
* `IsEncryptedAes128`: segments are AES-128-CBC; fetch the key from the segment/playlist key URI (HTTPS) and decrypt with the platform AES.
  `SAMPLE-AES`, FairPlay, Widevine, PlayReady and CENC are rejected before you ever see a plan.
* `Context` (`MediaRequestContext`): apply `HeadersFor(url)` to **every** request, including redirects you follow yourself.
  Cookie/Authorization are only for `CredentialHosts` (exact or subdomain). Do not log it with `ToString()` surprises: it is redacted, but do not dump fields.
* `ExpectedSize`: real (`Content-Length`/API) or `null`. Estimates only exist on representation level and are flagged `FileSizeIsEstimate`.
* `plan.ExpiresAt` / per-rep `ExpiresAt`: signed URLs. If the transfer starts after expiry, call the resolver again; do not retry a dead URL.
* `plan.Requirements`: `MuxAudioVideo` / `ExtractAudio` mark post-transfer work. Standalone resolver consumers must provide it; the integrated VESPER engine handles compatible clear HTTP merges. `plan.RequiresExternalHelper` describes the resolver library boundary, not every app capability. `plan.HasAudio == false` means the source offers no audio for that video.
* `SuggestedFileName` is sanitised (no separators, reserved names, bidi tricks); still resolve it inside the user's download folder yourself.

## Errors

`MediaError { Code, UserMessage, Diagnostic, IsRetryable, BrowserMayHelp, IsPermanent, EntryId }`

* Show `UserMessage`; log `Diagnostic` (secret-free, URLs redacted, no stack traces).
* `BrowserMayHelp` / `result.NeedsBrowserContext` -> offer "open the page and use the VESPER extension".
* `IsRetryable` -> offer retry; `IsPermanent` -> do not.
* `BrowserContextRequired` is used when a site gates the video behind checks that only the user's own browser session can satisfy
  (e.g. YouTube sign-in/bot verification). The library does not attempt to bypass them.

## Extending

Add a file in `Extractors/` with a static `AdapterDescriptor`, register it in `AdapterRegistry.Default`, add fixtures + tests, and
record a `CompatibilityRecord` after a real run. An adapter may return `MediaErrors.NotThisSite(...)` to defer to the next adapter / generic scan.
