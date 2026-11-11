# Compatibility report

**Last full verification run: never.** Nothing in this repository has been compiled or executed yet, and no live site has been
contacted. The status below is therefore `NotTested` for every adapter. Fixture tests exist (`Vesper.MediaResolver.Tests/ExtractorTests`) but
fixtures are synthetic reconstructions (see `Fixtures/FIXTURES.md`); passing them proves parsing logic, **not** live compatibility.

A site counts as *supported* only after a recorded run returns verified, actionable media from real authorised media. Record runs as
`CompatibilityRecord`s and regenerate this table with `CompatibilityReport.ToMarkdown(...)` (the report generator derives everything else
from each adapter's own `AdapterCapabilities`, so declarations and report cannot drift apart).

## Implemented adapters (all live status: NotTested)

| Adapter | Single item | Playlist / collection | Audio | Subtitles | Auth | Mechanism | Declared limits |
|---|---|---|---|---|---|---|---|
| YouTube | watch, shorts, live/ended-VOD URLs, embed, youtu.be | playlist page (first page), Mix = dynamic snapshot | audio-only streams | captions (VTT) | none | watch-page player response; plain-URL formats only | cipher/`n`-solving formats omitted (no script execution), DRM, live, playlist continuations, channels. **Partial by design.** |
| Vimeo | public + unlisted-with-hash | no | no | yes | none | `player.vimeo.com/video/{id}/config` (progressive + HLS) | private/password/domain-locked, showcases, OTT |
| Dailymotion | public | no | no | yes | none | `player/metadata/video/{id}` (HLS + MP4) | geo-blocked, private, playlists |
| Reddit | v.redd.it posts | no | yes (DASH audio) | no | none | post JSON -> DASH/HLS manifests | external embeds, galleries, login-walled |
| Internet Archive | video/audio items | multi-track audio items, albums | yes | yes | none | `archive.org/metadata/{id}` | lending/borrow items, texts |
| Bandcamp | public stream (usually 128 kbps MP3) | albums | yes | no | none | page `data-tralbum` JSON | no purchase bypass; custom domains |
| Bilibili | BV/av URLs, `?p=` parts | multi-part videos | yes | no | none (guest qualities) | public web API view + playurl (DASH) | paid/bangumi, login-only qualities, subtitles |
| TikTok | public videos | no | no | no | none | page rehydration JSON + session cookies scoped to TikTok hosts | photo posts, private, captcha pages. **Experimental.** |
| PeerTube | any instance (API-confirmed) | playlists with paging | yes | yes | none | `/api/v1` REST | private/password, live |
| Generic | direct files, HTML5, OG/JSON-LD, inline manifests, embeds (1 level), RSS/Atom feeds | feeds | yes | yes (track tags, HLS renditions) | browser context (consented) | verified by probe/parse | DRM, live, script-generated media, multi-period DASH |

Protocol support (parsing implemented, unit-tested offline, not run): HLS master/media playlists, audio and subtitle renditions, byte ranges,
discontinuities, init segments, AES-128 detection, SAMPLE-AES/DRM detection, live detection; DASH static single-period MPD with
SegmentTemplate (number/time/timeline), SegmentList, SegmentBase/BaseURL progressive files, language/channel metadata, ContentProtection
and dynamic detection, DTD-free XML.

## Tier A sites **not** implemented (honest gaps)

| Site | Why not yet |
|---|---|
| SoundCloud | needs a `client_id` scraped from site scripts plus transcoding resolution; planned with fixtures first |
| Twitch (clips/VODs) | public clip/VOD data is only reachable through Twitch's undocumented GraphQL endpoint; not adopted without a documented route |
| Instagram | media is behind login or undocumented embed endpoints; the generic adapter will pick up `og:video` where Instagram serves it, nothing more |
| Facebook | `browser_native_*_url` page scraping is fragile and often login-gated; browser-assisted discovery is the intended path |
| X / Twitter | embed/syndication endpoints are undocumented and token-gated; browser-assisted discovery is the intended path |
| Spotify, Netflix, Disney+, Prime Video, etc. | DRM; will never be supported |

For these, VESPER should use Mode B (extension supplies the manifest/stream URLs the page actually requested); the generic adapter verifies and ranks them.

## yt-dlp comparison

Not run. The harness exists (`Benchmarks -- --compare`). Until a corpus run is committed, **no parity or speed claim is made**. yt-dlp supports
well over a thousand sites; this library supports nine adapters plus generic discovery and is not at parity.

## Known regressions

None recorded (nothing has run).
