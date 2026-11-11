# VESPER Media Engine 3.0 — Engineering Report

Branch: `media-engine-3.0` (based on verified `main` @ `fbf40a9c7ac23f975870e486a5bff903558ddc85`). Date of all live evidence: **2026-11-11**.
Environment: Linux x86_64 sandbox, .NET SDK 10.0.404. **Windows was not available**: the WinForms app was only cross-compiled on Linux (`-p:EnableWindowsTargeting=true`), which is *not* Windows verification.

## 1. Root cause of "title and thumbnail but no stream" (YouTube)

Measured live from this network on 2026-11-11:

| Client / path | Result |
|---|---|
| Watch-page `ytInitialPlayerResponse` (the only path in 3.0's predecessor) | formats ciphered / server-ABR only → no usable URL |
| `WEB` player API | `UNPLAYABLE` |
| `TVHTML5`, `ANDROID_VR` | `LOGIN_REQUIRED` ("confirm you're not a bot") |
| `IOS`, `ANDROID` | URLs returned, **first 1 MiB served, mid-file range → HTTP 403** (GVS proof-of-origin enforcement) |
| `VISIONOS` + watch-page `visitorData` + `signatureTimestamp` | **OK, direct URLs up to 2160p60, full ranges served (206) at 10–45 MB/s, no `n` challenge** |

In addition, `MediaTransferBridge.PlayableOnly` dropped every selection needing `MuxAudioVideo`, so even working adaptive (HD) formats could never be downloaded.

## 2. What was implemented

**YouTube (`src/Vesper.MediaResolver/Extractors/YouTubeAdapter.cs`, `YouTubeClients.cs`)**
- Layered pipeline: one watch-page request (metadata, captions, `VISITOR_DATA`, `STS`, consent handled with a youtube.com-scoped `SOCS=CAI` reject-all cookie) → ordered InnerTube client strategies (`ResolverOptions.YouTubeClients`, default `["visionos"]`) → watch-page fallback restricted to formats that need no player transform.
- **Capability verification**: a strategy only wins after a 1-byte range probe near the end of one of its media URLs succeeds. This is what catches the "first MiB only" trap that produced false successes.
- All client identifiers in one file; profiles document *why* `ios`/`android_vr` are not used (PO token / upstream 403) instead of silently trying them.
- Error ladder that ranks facts about the video (DRM > region > login > live > unavailable > bot gate > rate limit > PO token > player-transform) above strategy/network noise, with a privacy-preserving strategy trail in `Diagnostic` (no signed URLs, cookies or tokens).
- Format model: unique ids for DRC and multi-language tracks (`yt-140-drc-en.4`), HDR10/HLG from `colorInfo`, 360° flag, OTF/SABR/cipher/`n` formats counted and reported, **byte identity** `youtube:{id}:{itag}:{lmt}:{clen}` in the new `MediaRepresentation.ContentIdentity`.
- Self-contained HLS alternative fetched **concurrently** with the byte probe when no muxed HTTP format exists.
- Playlists: 2026 `lockupViewModel` entries and classic renderers; continuation through `youtubei/v1/browse`, bounded by `MaxCollectionItems`/`MaxCollectionPages`, honest `IsComplete`. URL forms: watch, `youtu.be`, Shorts, live, embed, `youtube-nocookie`, `music.youtube.com`.
- No signature/n-challenge execution, no token manufacturing, no DRM/login/age bypass.

**Native remuxing (`src/Vesper.Engine/Media/Mux/`)** — pure C#, no dependencies, **remux only (never transcodes)**
- `Mp4Demuxer`: progressive (stbl) and fragmented (moof/trun, all tfhd/trun flag combinations, default-base-is-moof, signed CTO), bounded moov/moof sizes, every sample range checked against the file, encrypted entries (`encv/enca/sinf`) rejected as DRM.
- `WebmDemuxer`: EBML with known/unknown-size clusters, SimpleBlock/BlockGroup, lacing preserved, ContentEncodings (encryption/compression) rejected; only block headers are read.
- `Mp4Muxer`: flat MP4 with **moov before mdat** (progressive playback), interleaved ~0.5 s chunks, stts/ctts(v1 for negative)/stss/stsc/stsz/stco→co64 when > 4 GiB, edit lists preserved, sample entries copied byte-for-byte (keeps avcC/av1C/esds/colr HDR metadata).
- `MatroskaMuxer`: WebM/MKV with decode-order interleave, PTS timestamps (B-frames), keyframe-aligned clusters, Cues, SeekHead, Opus `CodecDelay`/`SeekPreRoll`.
- `MediaMerger`: chooses the container from **actually demuxed** codecs (MP4: H.264/HEVC/AV1+AAC/MP3; WebM: VP8/VP9/AV1+Opus/Vorbis; otherwise MKV), refuses illegal requests (e.g. VP9 as `.mp4`), and **re-reads the output** to prove track count, codecs, per-track sample count, payload bytes and duration.

**Merge job (`JobKind.Merge = 3`, `MergeTransfer.cs`, `MediaMergePlan.cs`)**
- Tracks are downloaded into regions of the job's partial file with ≤ 8 MiB range requests (3 bounded workers) through the existing checkpoint pipeline → durable progress, pause/resume, crash recovery, per-chunk retries (3 attempts) and global/job rate limits are all reused.
- After download: demux → remux to `.merging` → flush → validate → SHA-256 → atomic swap (tracks kept as `.tracks` sidecar) → commit cursor → existing no-overwrite `FinalizeAsync`. Sidecars are deleted only after publication. A crash between renames is reconciled on restart. A failed merge never deletes downloaded data; nothing is published unless validation passed.
- Missing audio in an "audio" stream → `NeedsAttention: missing-audio`, nothing published (tested end-to-end).
- **Expiring URLs**: `DownloadEngine.RefreshMergePlan` accepts a re-resolved plan only when every track's role, format id, exact size, codec and provider identity match (plan fingerprint deliberately excludes URLs); otherwise nothing on disk changes. No SQLite migration was needed (job kind is an integer; `MediaPlanJson` already existed).
- Audio-only downloads with provider identity also use this job (chunked ranges, validated, normalised to flat M4A / WebM, no transcoding).

**Shared path (desktop + extension)**: `MediaTransferBridge` (used by `Form1`, `BrowserBridgeHandler` and the live runner via a linked source file) is now capability-aware: video-only formats are offered only when a natively mergeable audio exists (`MergeCapability`, `FormatPreferences.NativeMergeOnly`); an exact video-only pick is paired with the preferred mergeable audio; merges with differing request contexts or unknown sizes are refused.

**Live runner**: `tools/Vesper.LiveCheck` + `tests/live/corpus.tsv` + `scripts/run-live.sh` (resolve → bridge → real engine job → VESPER validation → optional ffprobe/ffmpeg decode via `--ffmpeg-dir`; dev tools are never committed or bundled).

## 3. Tests (all executed in this environment)

| Suite | Before (baseline) | After |
|---|---|---|
| `Vesper.Tests` (engine) | 211 passed, 2 skipped | **221 passed, 0 failed, 2 skipped** (DPAPI tests are Windows-only) |
| `Vesper.MediaResolver.Tests` | 314 passed | **322 passed, 0 failed** |

New: 10 merge tests (real H.264/AAC/VP9/Opus CC0 fixtures, sample-by-sample payload equality, container legality, truncation/size corruption/random bytes/encrypted entries, plan identity/SSRF rules, two **end-to-end merge jobs through the real engine** incl. missing-audio refusal) and 8 YouTube strategy tests (session values, POST headers, single byte probe, no cookie leakage to googlevideo, PO-token trap, bot-gate ranking, continuation paging, URL forms, merge capability). Six existing YouTube tests were updated because the primary path changed from page-only to player API; their intent (page fetched once, Mix not enumerated, ciphered/challenge formats omitted, typed playability errors) is preserved and asserted.

## 4. Live results (details: `MEDIA_ENGINE_COMPATIBILITY.md`, raw: `reports/live/2026-11-11/`)

Independently validated with ffprobe + ffmpeg 7.0.2 (full decode `-xerror`, or head 90 s + tail 60 s for files > 400 MB): standard 240p (AV1+AAC→MP4), `youtu.be`/Music audio (AAC→M4A), nocookie embed, **1080p60 H.264+AAC→MP4 (267.8 MB)**, **2160p60 VP9+AAC→MKV (1.37 GB)**, Spanish dubbed track (AAC→M4A), 3 Shorts (AV1+AAC→MP4, AV1+Opus→WebM), 96-minute completed livestream (H.264+AAC→MP4, 215.7 MB), PeerTube HLS→MP4, Bandcamp MP3, Internet Archive AVI.

Success on the defined supported set (YouTube VOD/Shorts/collections, PeerTube, Bandcamp, Internet Archive, podcast RSS, Vimeo): **18 / 20 = 90 %** — below the 95 % target. Failures: YouTube Mix (watch pages no longer embed the Mix panel; needs the `next` endpoint) and Vimeo (reported DRM; unverified whether a clear rendition exists).

## 5. VESPER vs yt-dlp 2026.10.26 (same machine, network, formats; yt-dlp without a JS runtime = its `visionos`-only mode)

| Scenario | VESPER wall (median of 3) | yt-dlp wall (median of 3) | VESPER peak RSS | yt-dlp peak RSS* | Output |
|---|---|---|---|---|---|
| Big Buck Bunny 1080p60, itags 299+140 (268 MB) | **4.46 s** (5.91/3.48/4.46) | **18.40 s** (13.64/33.20/18.40) | 113–124 MiB | 60–62 MiB | both h264 1920×1080@60 + aac, duration 634.62 s |
| NASA Short, 788+140 (5.3 MB) | **2.44 s** (2.05/5.46/2.44) | **5.16 s** (5.16/6.81/3.72) | 102–110 MiB | 74–79 MiB | both AV1+AAC |

\*largest process in the measured tree (`scripts/measure.py`, `RUSAGE_CHILDREN`). VESPER uses 3 parallel range workers; yt-dlp used its defaults (one connection per track) plus ffmpeg for merging. Network variance is large (yt-dlp 13.6–33.2 s). VESPER's discovery took ~0.4–2.1 s with 5 requests (page, player API, byte probe, HLS master/media). **VESPER used ~1.5–2× more memory.** A first comparison run was invalid (my harness mangled multi-audio itags) and is archived, not used.

## 6. Requires integration review
- `Vesper.Engine`: new `JobKind.Merge`, `MergeTransfer`, `RefreshMergePlan`, sidecar cleanup in `RestartFromScratch`, add-time plan validation.
- `MediaTransferBridge.cs` behaviour change (HD YouTube now produces merge jobs; YouTube audio-only now produces single-track merge jobs).
- UI: no UI files changed. `RescueForm` still calls `RefreshMediaSourceAsync` (HTTP jobs only); wiring merge-job refresh into the UI (`RefreshMergePlan`) is left to the UI owner.
- `SecureHttpClient.PostJsonAsync`/`ProbeRangeAsync` (same validated path as GET).
- `tests/Vesper.Tests/TestSupport.cs`: `FaultServer.Extra` payload registry.

## 7. Smallest next actions
1. Mix/radio via InnerTube `next` (bounded snapshot). 2. Vimeo DRM report investigation. 3. Merge-job refresh wiring in UI. 4. Optional, explicitly approved JS evaluator (e.g. Jint, pure managed, BSD-2) for signature/n challenges — this is what "made for kids" videos and non-visionOS clients need; not added without approval. 5. Live HLS capture. 6. Windows run of the full suite and a packaged build.
