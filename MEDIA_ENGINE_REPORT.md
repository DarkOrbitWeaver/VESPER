# VESPER — media-engine reliability update

**Baseline:** `b0c5ac14877dc729280b5eb210123f23da6ae067`
**Working branch:** `work/media-discovery-reliability`
**Verification environment:** Linux x64, temporary .NET SDK 10.0.404, 2026-11-11.

This updates the actual existing C#/.NET 10 WinForms solution. It is an incremental implementation, **not a claim of completed yt-dlp parity or universal site compatibility**.
The source ZIP contains the existing application, extension, native helper, SQLite engine, settings/history architecture, tests and release scripts with the changes below.

## Root causes confirmed in source

1. A failed YouTube adapter could fetch the same HTML again through the generic fallback. The fetched response is now reused within that call.
2. Browser-observed candidates were considered only after a watch-page extraction attempt. Validated candidates now get the direct shared discovery path first.
3. The discovery form waited for final resolution before starting known artwork. YouTube's fallback poster URL explained how a thumbnail could coexist with no playback result.
   Metadata/progress events, independent early poster fetching, bounded image caching and background image decoding now separate those operations explicitly.
4. Plain adaptive tracks could prevent trying a usable HLS alternative. HLS and DASH fallback are now considered when no complete source-muxed representation exists, with independent manifest requests run concurrently.
5. Missing playback data was indistinguishable from genuinely absent media. YouTube now reports browser/playback-context, cipher/SABR, identity and access limitations accurately.
6. DASH representations existed in the resolver but had no desktop transfer path. A supported clear fMP4 path now reaches the same engine, queue, persistence and finalization from native and browser entry points.
7. HTTP concurrency permits covered headers, not response bodies. Permits now remain held through body consumption/disposal; thumbnail bodies and whole discovery have deadlines.
8. HLS ignored unsafe range/discontinuity/init changes, and regression `.ts` payloads were arbitrary bytes. The engine now validates actual TS/fMP4 structures and fixtures contain generated media.
9. Generic observed `video/mp4`/`video/webm` URLs did not prove audio presence. Media handoffs now check bounded track metadata before publication; ordinary HTTP downloads remain unaffected.
10. A durable `Completed` record could be observed just before runner cleanup, causing an immediate remove action to race. Snapshots remain `Finalizing` until cleanup removes the active session.

## Implemented source changes

### Discovery / performance

- Structured per-call timing and request measurements with no raw signed query strings in the measurement model.
- Early metadata and source-format publication; known posters do not imply playback access.
- Per-call scratch isolation, page reuse, direct adapter dispatch, browser-first observed candidate processing.
- Complete cache keys, short successful metadata caching, expiry margin, exclusion of credential-bearing nested results.
- Reused HTTP clients; body-level concurrency; explicit 35-second overall discovery deadline; caller cancellation remains distinct from timeout.
- Independent thumbnail connection bound, deadline, 768 KiB response cap, pixel/dimension limits, eight-entry per-window cache and background decoding.
- Native explicitly selected batches resolve up to four items concurrently while preserving selection order and partial failures.
- Internet Archive derivative grouping uses the source's `original` identity instead of duplicating chapters by bitrate filename.

### Correct transfer / recovery

- Versioned immutable DASH fMP4 plans, separate small transactional cursor, exact ordered fragments and byte-range checks.
- Clear fMP4 structural checks: initialization, clear supported sample descriptions, track IDs/defaults, sample offsets, media ranges, fragment ordering, decode-time continuity and required populated tracks.
- HLS master audio-group matching, ended VOD requirement, resolved redirect bases, proper implicit byte-range resource checks, complete init-map identity and explicit encrypted-map IV requirement.
- MPEG-TS packet, clear-payload, CRC-checked PAT/PMT, single-program track/PES validation; raw AAC/discontinuity/mux-required cases are not disguised as valid TS.
- Committed-prefix SHA-256 verification on resume; changed/legacy/unverifiable state stops without deleting data. HLS reconstructs TS state while hashing the existing prefix.
- MP4/WebM audio-presence guard for HTTP media handoffs. Video-only observed files are retained as partials with an explanation, not published as silent successes.
- On-demand original-page HTTP source refresh in Rescue: exact provider/media/format identities, then the existing trusted byte-identity rules. Fragment-plan refresh remains unavailable.
- Additive SQLite migration 3; durable format ID and plan metadata; DPAPI protection of sensitive source state on Windows; collision-safe migration backups.
- No-overwrite output publishing and finalization cancellation preserved. Recovery manifests retain streaming kind instead of incorrectly restoring fragments as ordinary HTTP jobs.

### Integration / release

- `MediaTransferBridge.cs` is shared by native and browser media selection and the developer transfer tool.
- Existing branding/WinForms controls retained; no redesign, new downloader, JavaScript download engine or service.
- XML docs fixed with exact constructor/method parameter names. Single-item xUnit assertions use `Assert.Single`, including its predicate overload; no analyzer suppression added.
- Existing native-host/Chrome extension/installer registration logic preserved. No browser download takeover behavior was changed.
- Release output/version checks and extension-test invocation hardened. Added source-only, no-overwrite packaging script and Windows CI for both .NET test projects and extension regressions.
- See `reports/CHANGED_FILES.txt` for every modified/added source file.

## Verification actually executed

| Check | Baseline run | Updated Release run |
|---|---:|---:|
| Engine tests | 161 passed | **211 passed, 2 Windows DPAPI tests skipped** |
| Media resolver tests | 294 passed | **314 passed** |
| Extension tests | 6 passed | **6 passed** |
| Full solution cross-build | Baseline tests built their projects | **Succeeded, 0 warnings / 0 errors** |
| Developer benchmark build | Built separately against the exact baseline | **Succeeded, 0 warnings / 0 errors** |

The two skipped tests are not treated as passed. WinForms execution, DPAPI integration, installer/native registration and Windows CI execution remain unverified on this Linux host.
Full build/test logs and TRX files are under `reports/validation`.

New coverage includes DASH assembly/ranges/invalid offsets/timelines, actual source-muxed tracks, media-track absence, TS program CRCs, HLS ranges/keys/groups, partial corruption and resume,
expired links, media identity mismatch, file-link rescue rejection for streaming plans, cancellation, concurrent output collisions, body concurrency/deadlines, browser handoff, cache isolation and Archive duplicate grouping.
Existing playlist pagination/quality fallback/missing-audio/security tests were retained. No separate-track muxing test is claimed because no muxer was added.

## Live evidence

- **Mux public HLS:** five discovered qualities. A selected low-bandwidth stream completed (22,418,060 bytes) and the resulting H.264/AAC file fully decoded with an independent current FFmpeg tool.
- **Akamai public DASH:** nine deduplicated representations. The selectable complete audio stream completed (5,278,733 bytes) and independently probed/decoded as AAC/M4A.
- **Internet Archive public-domain LibriVox collection:** 12 chapters enumerated; exactly two explicitly selected items queued, no media bodies downloaded during that check.
  Metadata was available at ~365 ms; thumbnail bytes at ~533 ms, without blocking enumeration (~387 ms).
- **YouTube:** three actual .NET attempts returned `BrowserContextRequired`; no live YouTube media success claimed. A separate curl path returned HTTP 403.
- **Vimeo:** the tested URL returned `DrmProtected` from the resolver, without a successful transfer. This is not an independent DRM diagnosis.
- The first isolated FFmpeg 7.0.2 TS validation tool crashed. Its failure is recorded; a different current build subsequently probed and fully decoded the identical output successfully.

Exact URLs, raw results and tested scope are in `MEDIA_ENGINE_COMPATIBILITY.md` and `reports/benchmarks`.

## Measured speed, not marketing

With a deterministic 40 ms-per-request fixture, browser-assisted discovery changed from two requests / **81.42 ms median** to one request / **40.78 ms median** across 20 cold-resolver samples.
The missing-player fallback also dropped from two requests to one, but remained a failure, not a manufactured success.
Public HLS/DASH discovery succeeded; real-network variation, JIT and cache effects are reported separately in `reports/BENCHMARKS.md`.
No current yt-dlp executable timing was run; **no faster-than-yt-dlp claim is made**.

## Dependencies and research

**New application/runtime dependencies: none.** No Python, yt-dlp executable/package, FFmpeg, Chromium, Electron, service or new native-language source project was added.
Existing NuGet references/versions are retained. DPAPI uses the Windows OS API. The console benchmark references the existing two C# libraries and is not launched by the app.
Temporary SDK/FFmpeg tools were installed outside the deliverable solely for compiling, fixture generation and independent validation; their binaries are not in the ZIP.

Real yt-dlp source was inspected at commit `412e7f0a4b0588fe884a02b344ac66a06538475b`, including `YoutubeDL.py`, extractor dispatch, YouTube player/format/token handling,
TikTok rehydration, Instagram access handling and common HLS/DASH parsers. It informed lazy dispatch, phase separation, precise failures and explicit post-processing capability checks.
No extractor code was copied or mechanically translated; no yt-dlp dependency or runtime was introduced. Existing repository licensing was not replaced.

## Remaining work

Read `MEDIA_ENGINE_LIMITATIONS.md` before treating this as production-complete. In particular: no general separate-track muxer, live YouTube not resolved here, no signature/n/SABR solution,
no large YouTube continuation/channel implementation, no signed-fragment-plan refresh, no native subtitle downloading, and no added Instagram/Facebook/X/Twitch/SoundCloud adapters.
The existing library ranking supports more preferences than the compact native UI exposes; advanced language/HDR/frame-rate/subtitle controls were not expanded in this update.

## Local Windows validation

1. Build `VESPER.slnx` with .NET 10 and run `scripts/Run-Tests.ps1`. The two DPAPI cases run on Windows rather than skip.
2. Exercise the actual WinForms discovery, cancellation, Rescue and extension/native-host handoff with media you are authorized to access.
3. Use `scripts/Build-Release.ps1` for normal tested Windows packaging; existing outputs are never silently removed.
4. `scripts/Package-Source.ps1` (PowerShell 7) creates a filtered source ZIP without build output or external runtimes.
