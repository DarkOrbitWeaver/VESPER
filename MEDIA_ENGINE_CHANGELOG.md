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
