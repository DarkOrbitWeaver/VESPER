# Media engine limitations (3.0, 2026-11-11)

## YouTube
- **No player-JavaScript execution.** Formats needing signature deciphering or the `n` throttling transform are omitted and counted in notes. Consequence: "made for kids" videos (not served to the visionOS client) and any future case where JS-less clients stop working will fail with a precise `BrowserContextRequired` diagnostic. Smallest practical fix (needs approval, not added): an optional pure-managed JS evaluator such as Jint (BSD-2, ~1 MB) behind the existing client-profile seam.
- **No proof-of-origin tokens** are generated or imitated. iOS/Android clients' URLs are cut off after ~1 MiB without them; the byte probe detects this and they are never offered.
- **Bot gates** ("Sign in to confirm you're not a bot") from flagged networks (datacenter IPs) are reported, not bypassed. The visionOS strategy worked from this sandbox only with watch-page visitor data.
- **Live streams** are detected and refused (completed recordings work). **Mix/radio** enumeration needs the InnerTube `next` endpoint (not implemented); the current video still resolves.
- **DRC audio variants** are collapsed with their regular twin by the shared deduplicator when metadata is identical.
- **Subtitles/automatic captions** are listed (VTT URLs) but caption *download* was not live-validated; some caption URLs may require tokens. Subtitles are not muxed into outputs.
- **8K** availability is parsed generically but no 8K item was live-tested. HDR formats are detected; an HDR download was not independently validated.
- Server-ABR-only and OTF (segmented) formats are unsupported and reported.

## Merging / containers
- Remux only. Supported outputs: MP4 (H.264/HEVC/AV1 + AAC/MP3), WebM (VP8/VP9/AV1 + Opus/Vorbis), MKV (any of these). Anything else is refused before download (`MergeCapability`) and re-checked after download (no publication on mismatch).
- Merge jobs take plain HTTP tracks of exactly known size. Separate HLS/DASH segment-list tracks are not merged yet (single-representation HLS/DASH downloads keep working as before).
- One video track + up to three audio tracks per plan; the bridge currently selects one audio track.
- Matroska output timestamps use 1 ms precision; MP4→MKV drops MP4 edit lists for timing shifts other than the initial offset (AAC priming ≈ 20 ms not compensated in MKV).
- HDR colour metadata is preserved verbatim for MP4→MP4 and WebM→WebM/MKV; MP4→MKV relies on in-bitstream colour information.
- Temporary disk need ≈ 2× output size during merge (tracks + merged file until publication).
- The `.tracks` sidecar can leak if the process dies after publication but before sidecar deletion (harmless; not yet garbage-collected).

## Platforms
- No adapters: SoundCloud, Twitch, X/Twitter, Instagram, Facebook. Existing adapters for TikTok, Reddit, Dailymotion, Bilibili were not live-tested in this pass. Vimeo's live test reported DRM (uninvestigated).
- The built-in validator parses MP4/WebM/MKV only; AVI/MP3/TS outputs are validated only by the existing per-protocol checks (and independently with ffprobe in live runs).

## Verification gaps
- **Windows not verified**: only a Linux cross-compile of the WinForms project; the 2 DPAPI tests are skipped on Linux. No packaged build was produced.
- Performance numbers come from one sandbox network with 1–3 runs per case; memory comparisons use peak RSS of the largest process.
