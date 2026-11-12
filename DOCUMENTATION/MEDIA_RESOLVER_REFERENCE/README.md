# Vesper.MediaResolver

> Historical README from the early standalone resolver package. The resolver is now integrated into the main VESPER solution and has been built and tested. Its old workspace layout and "never compiled" status below are not current; see [current release status](../RELEASE.md) and [current media limits](../../MEDIA_ENGINE_LIMITATIONS.md).

Native C# / .NET 10 media-discovery library for the VESPER download manager. It **finds** media (items, playlists, formats,
subtitles, download plans). It does **not** download anything: the VESPER engine does that.

The reference package's compatibility and continuation notes remain useful for the original standalone resolver boundary, but they do not describe the current integrated application.

## Layout

```
Vesper.slnx
Vesper.MediaResolver/            the library (no third-party dependencies)
  Core/         IMediaResolver, DefaultMediaResolver, SecureHttpClient, options, cache, result finalizer, compat report
  Models/       typed results: MediaItem, MediaRepresentation, MediaCollection, MediaError, request context, browser message
  Discovery/    generic adapter (direct files, HTML5, OG/JSON-LD, HLS/DASH URLs, feeds), page scanner, browser message parser
  Protocols/    Hls/ and Dash/ parsers + representation builders, ManifestResolver
  Formats/      format ranking, dedup, quality menu, preferences, DownloadPlan (the hand-off to the engine)
  Playlists/    CollectionPager (lazy, bounded), BatchResolver (one preference for N items), UrlListParser
  Extractors/   YouTube, Vimeo, Dailymotion, Reddit, Internet Archive, Bandcamp, Bilibili, TikTok, PeerTube
  Security/     URL/SSRF validation, IP classifier, redaction, file-name sanitising, browser-message validation
  Utilities/    JSON/HTML helpers (no regex for structured data), expiry parser, chapter parser
Vesper.MediaResolver.Tests/      xUnit: protocol, format, security, adapter, playlist, performance, opt-in live tests + fixtures
Vesper.MediaResolver.Benchmarks/ console harness + yt-dlp comparison harness (yt-dlp is never bundled)
Vesper.MediaResolver.Examples/   integration examples (single URL, batch, 4K preference, failure, browser message)
docs/INTEGRATION.md              the integration boundary, in detail
docs/BROWSER_MESSAGE.md          the browser-to-native message format (schema vesper.discovery/1)
COMPATIBILITY.md  KNOWN_LIMITATIONS.md  CHECKPOINT.md
```

## Build and test (on your Windows PC)

```
dotnet build Vesper.slnx -c Release
dotnet test  Vesper.MediaResolver.Tests -c Release
set VESPER_LIVE_TESTS=1 && dotnet test Vesper.MediaResolver.Tests --filter LiveTests     (optional, real network)
dotnet run -c Release --project Vesper.MediaResolver.Benchmarks
dotnet run -c Release --project Vesper.MediaResolver.Benchmarks -- --compare urls.txt --ytdlp C:\tools\yt-dlp.exe --out report.md
```

## 20-second usage

```csharp
using var resolver = new DefaultMediaResolver();            // free to construct, nothing runs while idle

var result = await resolver.ResolveAsync(new MediaDiscoveryRequest { Url = new Uri(pastedText) }, ct);
if (!result.IsSuccess) { ShowError(result.Error!); return; }  // typed: code, user message, retry?, browser may help?, permanent?

var menu = QualityMenuBuilder.BuildDefault(result.Item!);     // Best-with-audio / Balanced / Smallest / Audio only
var plan = DownloadPlanBuilder.Build(result.Item!, menu[0].Selection).Plan!;
engine.Enqueue(plan);                                         // VESPER's engine; the resolver is done
```

## Design rules that are enforced in code

* One component touches the network (`SecureHttpClient`): http/https only, no URL credentials, manual redirects validated per hop,
  connect-time DNS filtering against private ranges (anti-SSRF/rebinding), response-size and time limits, bounded concurrency.
* A direct URL is only "downloadable" after a real probe (status + media content type). A manifest is only "resolved" when fully
  parsed. DRM, SAMPLE-AES, live streams and multi-period DASH are typed failures, never partial successes.
* Video-only streams are never presented as having sound. Muxing/extraction needs are explicit (`ProcessingRequirement`).
* Credentials from the browser are used only with consent, only for the page's own origin, and only for allow-listed headers.
* No scripts are executed, no binaries are downloaded or launched by the library.

Compatibility claims live in `COMPATIBILITY.md` and are generated from adapter declarations plus recorded verification runs.
Nothing is marked verified yet.
