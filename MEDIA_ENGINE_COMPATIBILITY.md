# Media compatibility — measured scope, not hostname recognition

Baseline: `b0c5ac14877dc729280b5eb210123f23da6ae067`. Working branch: `work/media-discovery-reliability`.
Checks performed in the Linux sandbox on 2026-11-11. Windows execution remains unverified.

**Verified** means only the stated operation passed. **Partial** means implemented paths/fixture coverage with important gaps.
**Unsupported** means deliberately unavailable. **Untested** is not a claim of compatibility.

| Source / workflow | Classification | Actual evidence and boundaries |
|---|---|---|
| Mux public Big Buck Bunny HLS | **Verified: discovery + complete transfer** | `https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8`: five source qualities. Low-bandwidth media playlist fully transferred through VESPER: 22,418,060 bytes; independent current FFmpeg probe and full decode succeeded, H.264 + AAC, 634.633333 s. Not every quality was downloaded. |
| Akamai public Big Buck Bunny DASH | **Verified: discovery + audio transfer** | `https://dash.akamaized.net/akamai/bbb_30fps/bbb_30fps.mpd`: nine deduplicated representations. One selectable complete audio representation. 5,278,733-byte AAC/M4A output independently probed and fully decoded, 634.354750 s. Separate video/audio pairing is **not** implemented. |
| Internet Archive / LibriVox public-domain collection | **Verified: enumeration + explicit queue** | `https://archive.org/details/adventures_holmes`: 12 chapters; two explicitly selected items durably queued, no bodies automatically downloaded. Title and thumbnail retrieval measured independently. Full chapter downloads were not run. |
| Ordinary HTTP files | **Verified in regression fixtures** | Existing range, identity, retry, rescue, cancellation, hash, file-safety and crash tests retained. Not a universal live-host claim. |
| Clear source-muxed fragmented MP4 | **Verified in fixtures** | Synthetic H.264/AAC initialization + three fragments, exact-byte assembly, timeline/offset checks, crash/expiration recovery, independent decode. No combining of separate tracks. |
| HLS AES-128 CBC with accessible identity key | **Verified in local regression fixtures** | Existing AES transfer tests retained and now use actual synthetic TS media. Encrypted initialization logic implemented with explicit-IV requirement; that specific path is not independently live-tested. SAMPLE-AES/DRM/session keys remain unavailable. |
| Direct MP4 / M4A / WebM media handoff | **Verified track-presence fixture checks; partial container validation** | MP4/WebM audio presence checked before publishing media handoffs. Video-only MP4/WebM is refused rather than silently accepted as an audio/video result. This is not full codec decoding. |
| YouTube videos / Shorts | **Partial implementation; live resolution failed here** | Embedded plain playback URLs, HLS/DASH fallback, source identity and typed errors tested with synthetic fixtures. Three actual `https://www.youtube.com/watch?v=aqz-KE-bpKQ` .NET discoveries returned `BrowserContextRequired`; no live YouTube download succeeded. A separate curl request returned HTTP 403. No signature/n challenge solving, SABR implementation, token generation or anti-bot evasion. |
| YouTube playlists / Mix | **Partial / live untested** | Existing first-page playlist and bounded dynamic Mix snapshot tests pass. No continuation API or large/channel enumeration added. Browser candidates can avoid a redundant watch-page request; that handoff path is fixture-tested, not live-browser verified. |
| TikTok | **Partial / live untested** | Existing rehydration formats/cookie-scoping tests preserved; early metadata added. The desktop still refuses transfer contexts it cannot safely retain/apply. No live TikTok success claimed. |
| Vimeo | **Partial; tested URL produced no usable result** | `https://vimeo.com/76979871` returned `DrmProtected` from the resolver after three requests. This records resolver behavior, not an independent determination about the source's actual protection. No successful transfer verified. |
| Dailymotion, Reddit, Bilibili, PeerTube | **Partial / live untested** | Existing independent adapters and regression fixtures preserved. Separate-track media still needs an unavailable muxer. No claim that all current live response shapes work. |
| Internet Archive single files, Bandcamp | **Partial / additional live transfers untested** | Existing adapters preserved; Archive derivative grouping improved using the source's `original` field. Collection operations verified above, not blanket download compatibility. |
| Generic HTML5 / embeds / RSS | **Partial** | Existing parser, embed, feed and security tests pass. Public HLS/DASH paths verified above. Arbitrary player JavaScript is not executed. |
| Instagram, Facebook, X/Twitter, Twitch, SoundCloud | **Unsupported dedicated extraction / generic paths untested** | No empty or hostname-only adapters were added. Generic exposed clear media might work, but there is no live evidence here. |
| DRM, live streams, multi-period DASH, changing init maps, discontinuity remux, general A/V mux | **Unsupported** | Kept unavailable rather than concatenating incompatible bytes or advertising unsupported qualities. |

## Reproduce the actual checks

Use the .NET 10 developer tool in `src/Vesper.MediaResolver.Benchmarks`. Commands and cold/warm methodology are in `reports/BENCHMARKS.md`.
Raw JSONL, successful independent probes, and decoder logs are included under `reports/benchmarks`.
Full external media, downloaded binaries, credentials and temporary test databases are not included in this source delivery.
