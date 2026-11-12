# Known limitations

> Historical standalone-resolver snapshot. The limits below describe that library contract, not every capability in the integrated Windows app. See [current media limits](../../MEDIA_ENGINE_LIMITATIONS.md).

## Verification state
* At this snapshot, the code had not been compiled or executed. `CHECKPOINT.md` records the original first-build concerns.
* All benchmark numbers must be produced by the harness on your machine. None are included, and no speed/memory claim vs yt-dlp is made.
* Site adapters are written from documented/commonly observed response shapes and have not been checked live. They will drift; each reports `SourceChanged` when its expected structure vanishes.

## By design (not bugs)
* **No JavaScript execution.** YouTube formats that need signature/`n` solving are omitted and reported; throttled-URL warning is surfaced. Browser-assisted discovery is the supported path for them.
* **No DRM, no account/anti-bot circumvention.** Widevine/PlayReady/FairPlay/CENC/SAMPLE-AES -> typed failure. Sign-in/bot checks -> `LoginRequired` / `BrowserContextRequired`.
* **No muxing/transcoding in the resolver library.** It reports `MuxAudioVideo` / `ExtractAudio`; the integrated VESPER engine handles compatible remux jobs.
* **Live streams** (`#EXT-X-ENDLIST` missing, DASH `type="dynamic"`, `isLive`) are rejected, not recorded.
* **Multi-period DASH** is rejected (periods may carry different init segments).
* Credentials from the browser are never forwarded to hosts outside the page host and its subdomains, so cookie-gated CDNs on other domains need a different mechanism.
* `UseSystemProxy` is off by default; with it on, connect-time private-address filtering is delegated to the proxy.

## Functional gaps
* YouTube playlists: first page only (continuation needs the private browse API). Channels/search/home unsupported.
* Playlist/collection support exists for: YouTube (partial), Internet Archive, Bandcamp, Bilibili parts, PeerTube, RSS/Atom feeds.
* HLS variants are not individually fetched (one probe playlist per master); per-variant protection differences are not detected. I-frame playlists, `EXT-X-DEFINE`, low-latency partial segments are not supported (`DEFINE` is rejected).
* DASH: no `SegmentBase` sidx-based segment splitting (such representations are returned as one progressive file), no `$SubNumber$`, no `xlink`, text tracks only when given by a plain BaseURL.
* Direct-file audio presence cannot be verified without parsing the container; such representations are marked `AudioUnverified`.
* Subtitle conversion is out of scope: URLs and formats (`vtt`, `srt`, `hls-webvtt`...) are reported.
* Size estimates exist only where a bitrate and duration are known (flagged `FileSizeIsEstimate`); otherwise `null`.
* Time-limited signed URLs: expiry is read only from well-known query parameters (`expires`, `expire`, `exp`, `deadline`, AWS `X-Amz-*`).
* No automatic retries/backoff; callers decide using `IsRetryable`.
* The Chrome extension and VESPER engine integration are not part of this deliverable.

## Test-suite caveats
* Raw-string fixtures and file fixtures are hand-written; two tests (`LiveTests`) use public URLs that were chosen from documentation and not yet exercised.
* Performance tests are loose regression guards (they only catch pathological behaviour), not benchmarks.
* Assertions on collections use `SequenceEqual` to avoid xUnit overload ambiguity with newer C# versions; if you upgrade xUnit, you may simplify them.
