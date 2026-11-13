# Native UI acceptance checklist

Automated tests cover some of these behaviors, but they do not replace the manual checks below. Record the Windows, Chrome, and VESPER versions with each run.

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

## Goal 5 real-world workflow matrix (pending manual execution)

Use public, authorized, non-DRM sample content where possible. Do not use tests that require defeating sign-in, age, bot, or DRM protections.

| Journey | Steps | Pass condition |
|---|---|---|
| First Flight install | Start Setup; watch from the first progress update through the publisher and VESPER reveals; try Skip; repeat with a fast install, a failure, and cancellation. Enable Windows reduced motion for a separate run. | Installation starts immediately and reports real progress. The approved logo, butterfly, publisher, and wordmark sequence is visible; success remains immediately actionable while the intro finishes; Skip works; failure/cancel appears immediately; reduced motion avoids forced animation. |
| Chrome capture and fallback | With the extension enabled and VESPER closed, start a direct file download. Repeat with VESPER unavailable or the native helper disconnected. | Chrome is cancelled only after VESPER accepts the request. If handoff is unavailable or rejected, Chrome continues the original download. Neither path reports success before a file is actually saved. |
| Native messaging and reconnect | Open VESPER from the extension, submit the same item twice, close/reopen VESPER, then submit a new item. | Replies stay associated with the originating request; repeated requests do not create duplicate jobs; reconnect succeeds without losing authorization or leaving Chrome's download cancelled. |
| YouTube watch, Shorts, and hover preview | Open a public watch page and a Short. On the homepage, hover several video cards and wait for each preview to start without moving the pointer; open VESPER's picker, navigate away, and repeat. | Each picker stays bound to the card/page whose player started, including delayed hover previews. Repeated interactions create no stale or duplicate item. Only currently usable formats are offered. |
| Instagram Reels and TikTok feed | Try a public Reel and feed video, then navigate between items while playback or discovery is active. | Any offered media belongs to the current page. Unsupported or protected media receives an honest fallback; no duplicate, stale selection, or false success appears. |
| Media choices and subtitles | On a supported item with multiple formats/tracks/captions, inspect the picker; choose video quality, audio-only, and one or more subtitle languages. Play any supported embedded track and open sidecars. | No subtitle is selected by default. Language choices are accurate; sidecars are separate files; embedded tracks are switchable only where the destination container and muxer support them; subtitles are never burned into the picture. Audio/quality choices match the selected item. |
| Expired link and retry | Pause or queue a supported short-lived media link until it expires, then resume or retry. Repeat with a browser-owned transfer. | VESPER explains the failure and offers a safe retry/reselection path. It does not reuse unverifiable partial media, report a false completion, or cancel Chrome before the replacement is accepted. |
| Music artwork and tags | Download an authorized supported music item with source-provided artwork and metadata; open the result in a Windows music player and inspect tags with an independent format reader. Repeat with no valid artwork. | The player shows the matching cover, title, artist, and album where that output format supports them. Available track/disc/date values are preserved where supported. Missing artwork never causes a fabricated image or a failed audio download. |
