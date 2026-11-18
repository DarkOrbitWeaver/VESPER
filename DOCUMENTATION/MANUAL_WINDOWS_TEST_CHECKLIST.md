# Native UI acceptance checklist

Automated tests cover some of these behaviors, but they do not replace the manual checks below. Record the Windows, Chrome, and VESPER versions with each run.

The standalone First Flight Studio timeline or manifest was not present with the supplied source assets. Verify the requested sequence beats below on Windows; an original-keyframe comparison is not available.

2026-11-18 at ae9b091 (Windows build 26200): 10 First Flight UI tests passed with the visible capture option enabled. Light and dark captures show the intro, publisher reveal, and completion with Open VESPER available during the intro. This run used an injected install operation. Packaged install, cancellation, live reduced motion and mixed DPI remain in the manual check below.

- [ ] `dotnet restore` + Release build on Windows 11 with .NET 10.
- [ ] Form has real Windows caption, usable minimize/maximize/resize, no custom titlebar.
- [ ] Default v8.3 clean light surface shows list; footer always 30 logical pixels.
- [ ] Search finds filename, media metadata, category, status, and public source address without shifting the list; signed query strings are not searchable.
- [ ] Every state filter and sort mode acts on only sample records.
- [ ] Clicking a regular row does not select it or change the record.
- [ ] Clicking checkbox cell toggles selection and selection-only toolbar replaces search area.
- [ ] Header checkbox toggles all visible rows; Ctrl+F clears checked selection.
- [ ] Clicking file-type icon opens independent Details; right-click and ellipsis open same menu.
- [ ] Keyboard arrows, Space, Enter, Shift+F10 and screen reader announcements work.
- [ ] Queue and completed states show NO fake progress bars or speeds; missing is distinct from failed.
- [ ] Completed and missing Details show no irrelevant speed/ETA; no simulated file can be opened or deleted.
- [ ] Add Download validates HTTP and HTTPS URLs, rejects torrent, collapsed Save-to expands and browses a folder.
- [ ] Download and queue chevron both create demo-only queue entries with explicit message.
- [ ] Settings Save applies changes to this session; Cancel changes nothing.
- [ ] Hide missing completed records does not hide paused/active records.
- [ ] Density 34/46/58 at 100/125/150/200% DPI and increased OS text size shows no clipping.
- [ ] Light/dark/system themes and high contrast checked.
- [ ] v9 header branch, if enabled, remains noninteractive and clear of all controls and footer.
- [ ] Idle CPU stable, no ongoing animation, GDI/working set/scroll with 10,000 demo rows measured.
- [ ] Illustrated ICO looks sharp enough in real Windows taskbar / Explorer at 16/24/32/48 sizes.
- [ ] Changing settings does not contact any download endpoint or alter files on disk.

## Isolated Chrome ownership smoke check (optional)

The normal installed Chrome 157 cannot load an unpacked MV3 extension with `--load-extension`; do not use it as evidence that the extension failed. A separate **Chrome for Testing** or Chromium binary is required for the opt-in test. No such binary was present on the current machine. The test has not passed in a browser yet.

In PowerShell, with a separately acquired Chrome for Testing executable:

```powershell
$env:CHROME_PATH = 'C:\path\to\chrome-for-testing\chrome-win64\chrome.exe'
node tests/extension/chrome-owned-smoke.mjs
Remove-Item Env:\CHROME_PATH
```

This harness starts only a temporary headless browser profile and a local HTTP fixture, disables VESPER interception, verifies the exact bytes saved by Chrome, and cleans up its profile. It deliberately does **not** register or invoke VESPER's native helper or exercise interception success. That requires a separate live Chrome/native messaging acceptance test in an isolated environment.

## Goal 5 real-world workflow matrix (pending manual execution)

Use public, authorized, non-DRM sample content where possible. Do not use tests that require defeating sign-in, age, bot, or DRM protections.

| Journey | Steps | Pass condition |
|---|---|---|
| First Flight install | Start Setup; watch from the first progress update through the publisher reveal and completion screen; try Skip; repeat with a fast install, a failure, and cancellation. Enable Windows reduced motion for a separate run. | Installation starts immediately and reports real progress. The approved logo flashes and butterfly movement lead to the readable DarkOrbitWeaver publisher wordmark, then the VESPER welcome branding appears at completion; success remains immediately actionable while the intro finishes; Skip works; failure/cancel appears immediately; reduced motion avoids forced animation. |
| Chrome capture and fallback | With capture enabled and Automatic connection selected, start a direct file download and a safe ZIP from a redirecting API endpoint; repeat with Use site sign-in off and with VESPER/native messaging unavailable. | Only fresh downloads observed in `onCreated` with zero bytes already received may be considered for automatic capture, and only replayable GETs with proof of source identity may transfer. Chrome pauses only after native transfer readiness and is canceled only after verified VESPER completion. Any Chrome-owned download, including a file Chrome finishes or starts receiving before acceptance, remains Chrome-owned and is never imported into VESPER or labeled completed by VESPER. |
| Native messaging and reconnect | Open VESPER from the extension, submit the same item twice, close/reopen VESPER, then submit a new item. | Replies stay associated with the originating request; repeated requests do not create duplicate jobs; reconnect succeeds without losing authorization or leaving Chrome's download cancelled. |
| YouTube watch, Shorts, and hover preview | Open a public watch page and a Short. On the homepage, hover several video cards and wait for each preview to start without moving the pointer; open VESPER's picker, navigate away, and repeat. | Each picker stays bound to the card/page whose player started, including delayed hover previews. Repeated interactions create no stale or duplicate item. Only currently usable formats are offered. |
| Instagram Reels and TikTok feed | Try a public Reel and feed video, then navigate between items while playback or discovery is active. | Any offered media belongs to the current page. Unsupported or protected media receives an honest fallback; no duplicate, stale selection, or false success appears. |
| Media choices and subtitles | On a supported item with multiple formats/tracks/captions, inspect the picker; choose video quality, audio-only, and one or more subtitle languages. Play any supported embedded track and open sidecars. | No subtitle is selected by default. Language choices are accurate; sidecars are separate files; embedded tracks are switchable only where the destination container and muxer support them; subtitles are never burned into the picture. Audio/quality choices match the selected item. |
| Expired link and retry | Pause or queue a supported short-lived media link until it expires, then resume or retry. Repeat with a browser-owned transfer. | VESPER explains the failure and offers a safe retry/reselection path. It does not reuse unverifiable partial media, report a false completion, or cancel Chrome before the replacement is accepted. |
| Music artwork and tags | Download an authorized supported music item with source-provided artwork and metadata; open the result in a Windows music player and inspect tags with an independent format reader. Repeat with no valid artwork. | The player shows the matching cover, title, artist, and album where that output format supports them. Available track/disc/date values are preserved where supported. Missing artwork never causes a fabricated image or a failed audio download. |
