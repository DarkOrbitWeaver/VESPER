# Media-engine change log

Changes from `b0c5ac14877dc729280b5eb210123f23da6ae067` on `work/media-discovery-reliability`:

- Fixed HLS/subtitle XML parameter documentation and single-item xUnit analyzer issues.
- Added progressive metadata/format events, per-call timings, request deadlines and correct body-level concurrency.
- Reused fallback HTML, preferred validated browser candidates and isolated per-call/cache state.
- Made thumbnail fetching independent, bounded, cancellable and cached; retained the native UI and placeholder.
- Improved YouTube plain-URL/manifest discovery and precise errors without challenge solving or access bypasses.
- Added clear single-representation fMP4 DASH transfer and container/track/timeline validation.
- Hardened HLS byte ranges, map identity, AES IVs, redirects, TS program tables and fMP4 handling.
- Added committed-prefix integrity checks and safe fragment resume; refused changed/unverifiable plans.
- Prevented silent HTTP media handoffs by checking MP4/WebM audio presence before publication.
- Added exact format provenance and explicit identity-checked HTTP source refresh to Rescue.
- Shared desktop/browser transfer preparation and bounded concurrent selected-item discovery.
- Fixed Archive derivative duplicates; verified a public-domain 12-chapter collection and explicit two-item queue.
- Added nullable SQLite migration 3, current-user DPAPI protection on Windows and no-overwrite migration backups.
- Fixed the completion/runner-cleanup snapshot race; retained ordinary HTTP, history and no-overwrite publication.
- Added synthetic real media fixtures, regression tests, developer benchmarks, evidence logs and Windows CI.
- Hardened release outputs and added no-overwrite source-only packaging. Native host/extension/installer registration retained.

**Not added:** general A/V muxing, FFmpeg dependency, yt-dlp/Python/browser runtime, DRM circumvention, live YouTube success,
large YouTube continuation/channel support, fragment-plan URL refresh, native subtitle downloading or untested site adapters.

Verification and limitations: `MEDIA_ENGINE_REPORT.md`, `MEDIA_ENGINE_COMPATIBILITY.md`, `MEDIA_ENGINE_LIMITATIONS.md`, `reports/BENCHMARKS.md`.

## 3.0 — 2026-11-11 (branch media-engine-3.0)
- YouTube: layered InnerTube pipeline (visionOS client, watch-page session values, byte-probe verification, page fallback), byte identity, 2026 playlist markup + continuation, honest Mix/live diagnostics.
- Native pure-C# remux (MP4/fMP4, WebM/Matroska readers; fast-start MP4 and WebM/MKV writers) with output re-validation; `JobKind.Merge` durable merge job; identity-gated `RefreshMergePlan`.
- Capability-aware `MediaTransferBridge`; `MergeCapability`; `SecureHttpClient.PostJsonAsync`/`ProbeRangeAsync`.
- Tests: engine 221 passed / 2 skipped, resolver 322 passed. Live runner `tools/Vesper.LiveCheck`; evidence in `reports/live/2026-11-11/`.

Phase 1 reliability changes from `2620e76e0fa987e93189646ae6442cd0005fba7c`:

- Converted M4A and compatible MP4 outputs now keep the discovered title, artist, album and cover. The converted samples are stream-copied into a tagged MP4, re-read and compared before the file is published; a tagging failure keeps the valid untagged conversion and says so.
- A converted file is always published under its `.m4a`/`.mp4` name, with or without tags.
- The engine and the media resolver share one public-address rule (`src/Shared/NetworkAddressPolicy.cs`). NAT64, 6to4, Teredo, IPv4-translated and benchmarking/documentation ranges are refused unless private networks are allowed.
- Saved names drop invisible and direction-changing characters, and use the Windows invalid-character set on every platform (`src/Shared/FileNameRules.cs`).
- The Chrome extension records, for the current browser session, why a download stayed in Chrome and shows it in the popup with a recovery action. VESPER reports why a handoff was not ready (`transferReason`).
- Extension commands are allow-listed by sender; site sign-in cookies follow SameSite rules.
