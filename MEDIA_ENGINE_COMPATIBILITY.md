# Live compatibility matrix — tested 2026-11-11

Runner: `tools/Vesper.LiveCheck` (same `MediaTransferBridge` + real `DownloadEngine` as the app) over `tests/live/corpus.tsv`.
Independent validation: ffprobe/ffmpeg 7.0.2 static (development tool, not bundled): full decode with `-xerror` (files ≤ 400 MB) or first 90 s + last 60 s decode plus container duration (larger files). Raw evidence: `reports/live/2026-11-11/`.
"VESPER validation" = built-in demux of the published file (track kinds, codecs, sample counts, durations). It understands MP4/WebM/MKV only.

| Platform | Scenario | Discovery | Format selection | Download | Audio present | Output validity (independent) | Tested | Failure reason / notes | Limitations |
|---|---|---|---|---|---|---|---|---|---|
| YouTube | Standard video (`jNQXAC9IVRw`) balanced | pass | pass (395+140) | pass | verified | full decode ok; AV1 320×240 + AAC MP4 | 2026-11-11 | — | — |
| YouTube | `youtu.be` link, audio only | pass | pass (140) | pass | verified | full decode ok; AAC M4A | 2026-11-11 | — | — |
| YouTube | YouTube Music URL, audio only | pass | pass (140) | pass | verified | full decode ok; AAC M4A | 2026-11-11 | — | Music-only (non-video) catalogue items not tested |
| YouTube | `youtube-nocookie.com/embed` smallest | pass | pass (395+140) | pass | verified | full decode ok | 2026-11-11 | — | Embeds discovered inside third-party pages not tested |
| YouTube | 1080p60 H.264+AAC (Big Buck Bunny) | pass | pass (299+140) | pass (267.8 MB, 8 s) | verified | full decode ok; h264 1920×1080@60 + aac, 634.6 s | 2026-11-11 | — | — |
| YouTube | 2160p60 4K (Big Buck Bunny) | pass | pass (315 VP9 + 140) | pass (1.37 GB, 22.5 s) | verified | head+tail decode ok; vp9 3840×2160 + aac MKV | 2026-11-11 | — | 8K not tested (no verified 8K item in corpus) |
| YouTube | HDR + 4K discovery (`LXb3EKWsInQ`) | pass | n/a | n/a | n/a | n/a | 2026-11-11 | HDR formats detected | HDR download not independently validated |
| YouTube | Multi-language audio discovery (`0e3GPea1Tyg`) | pass | n/a | n/a | n/a | n/a | 2026-11-11 | multiple audio tracks listed | — |
| YouTube | Non-default language (es) audio | pass | pass (140-es.3) | pass | verified | full decode ok; AAC M4A, 1541 s | 2026-11-11 | — | — |
| YouTube | Shorts `k5Z7sFwFF7Q` | pass | pass (788+140) | pass | verified | full decode ok; AV1 608×1080 | 2026-11-11 | — | — |
| YouTube | Shorts `-62C9R_41Sc` | pass | pass (788+140) | pass | verified | full decode ok | 2026-11-11 | — | — |
| YouTube | Shorts `myZ9kn9MIWQ` | pass | pass (788+251) | pass | verified | full decode ok; AV1+Opus WebM | 2026-11-11 | — | — |
| YouTube | Completed livestream (`MRg4uIpJ3xE`, 96 min) | pass | pass (135+140) | pass (215.7 MB) | verified | full decode ok; h264 854×480 | 2026-11-11 | — | — |
| YouTube | Item harvested as "stream" (`M3HKLzjvKPc`) | fail (correct) | n/a | n/a | n/a | n/a | 2026-11-11 | currently **live**; reported `LiveStreamNotSupported` | Live capture unsupported; excluded from the success denominator (not a completed recording) |
| YouTube | Playlist with continuation (NASA uploads) | pass | n/a | n/a | n/a | n/a | 2026-11-11 | 200 entries in 2 requests (continuation followed), `IsComplete=false` because the 200-item bound was reached | First run failed (2026 `lockupViewModel` markup); fixed and re-run the same day |
| YouTube | Mix / radio | **fail** | n/a | n/a | n/a | n/a | 2026-11-11 | watch pages no longer embed the Mix panel; now reported as `SourceChanged` with that reason | Needs InnerTube `next` endpoint (not implemented) |
| PeerTube | framatube "What is PeerTube?" | pass | pass (HLS 480p) | pass (50 s) | verified | full decode ok; h264 + aac | 2026-11-11 | — | HLS fetch slow (serial segments) |
| Bandcamp | yt-dlp public test track | pass | pass (mp3-128) | pass | verified (ffprobe) | full decode ok; mp3 | 2026-11-11 | VESPER validator does not parse MP3 | — |
| Internet Archive | `BigBuckBunny_124` smallest | pass | pass (AVI) | pass (332 MB) | verified (ffprobe) | full decode ok; mpeg4 + audio | 2026-11-11 | VESPER validator does not parse AVI | — |
| Podcast RSS | NPR feed | pass | n/a | n/a | n/a | n/a | 2026-11-11 | 200 enclosures listed (bounded) | — |
| Vimeo | `76979871` | **fail** | n/a | n/a | n/a | n/a | 2026-11-11 | `DrmProtected: HLS playlist uses a DRM key format` | Not yet investigated whether a clear rendition exists |
| SoundCloud | public track | fail | n/a | n/a | n/a | n/a | 2026-11-11 | no adapter; generic scan finds no media | Unsupported |
| Twitch | VOD URL | invalid test | — | — | — | — | 2026-11-11 | **corpus placeholder ID** (made up) — not a valid test; no Twitch adapter exists | Replace with a real public clip/VOD |
| X | post URL | invalid test | — | — | — | — | 2026-11-11 | **corpus placeholder ID** (HTTP 404) — not a valid test; no X adapter exists | Replace with a real public post |
| TikTok, Instagram, Facebook, Reddit, Dailymotion, Bilibili | — | not tested live | — | — | — | — | — | adapters exist only for TikTok/Reddit/Dailymotion/Bilibili (fixture-tested); Instagram/Facebook have none | Milestone C |

## Success rate on the defined supported set

Supported set = YouTube VOD/Shorts/audio/discovery/collections, PeerTube, Bandcamp, Internet Archive, podcast RSS, Vimeo (20 items; the live-now stream and the two placeholder URLs are excluded with reasons above).
**18 / 20 passed = 90 %** (target 95 % not reached). Failing: YouTube Mix, Vimeo.

## yt-dlp reference

yt-dlp 2026.10.26 (no JS runtime → its JS-less `visionos` client, the same client VESPER uses) downloaded the same YouTube formats successfully in every valid comparison run; no case was found where yt-dlp succeeded and VESPER failed on the compared items. yt-dlp **with** a JS runtime can reach clients VESPER cannot (e.g. "made for kids" videos); that gap is documented in `MEDIA_ENGINE_LIMITATIONS.md`.
