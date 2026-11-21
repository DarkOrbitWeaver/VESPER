# Native UI acceptance checklist

Run these checks on the exact candidate. Record the commit and the Windows, Chrome, and VESPER versions. Use disposable downloads, folders, and browser profiles.

Q5 accessibility is explicitly deferred by the owner and optional for this release. Keep its Narrator, screen-reader, high-contrast and accessibility-specific text-scaling criteria in `QUALITY-RELEASE-GATES.md`; these items are not required release checks.

The standalone First Flight Studio timeline or manifest was not present with the supplied source assets. Verify the requested sequence beats below on Windows; an original-keyframe comparison is not available.

2026-11-18 at ae9b091 (Windows build 26200): 10 First Flight UI tests passed with the visible capture option enabled. Light and dark captures show the intro, publisher reveal, and completion with Open VESPER available during the intro. This run used an injected install operation. Packaged install, cancellation, live reduced motion and mixed DPI remain in the manual check below.

2026-11-20, preview `1.0.2-firstflight.8` at `3da0d28`: the packaged app and native helper passed abrupt HTTP interruption and restart in a disposable Windows account. One run resumed at the saved offset, published a byte-exact file and rejected duplicate submission. A second run preserved saved bytes and refused publication after the server changed its ETag. The .8 setup assembly used its embedded payload and real per-user Windows registration to pass clean install, .7-to-.8 running-app upgrade, rollback after registration, repair and both uninstall modes in isolated folders. The First Flight window and real power-loss cases are still open. Local scripts and logs are under `release/audit-evidence/packaged-http-recovery` and `release/audit-evidence/packaged-setup-transaction`.

The packaged .8 engine also passed ten isolated process stops in a test worker. HLS and DASH each passed after flush and after commit with a checkpoint per fixture segment. Direct audio/video merge passed before swapping, after moving the source tracks, and after moving the merged output. Native AAC-to-WAV conversion passed after preparing the output and after moving it into place. FFmpeg independently decoded all nine recovered media outputs. A tenth stop during Cancel preserved an unrelated file and restored the pending cancellation for a successful retry. The reproducer and saved results are under `release/audit-evidence/segmented-process-recovery`.

- [ ] `dotnet restore` + Release build on Windows 11 with .NET 10.
- [ ] Form has real Windows caption, usable minimize/maximize/resize, no custom titlebar.
- [ ] Default v8.3 clean light surface shows list; footer always 30 logical pixels.
- [ ] Search finds filename, media metadata, category, status, and public source address without shifting the list; signed query strings are not searchable.
- [ ] Every state filter and sort mode shows the matching native download jobs; status updates and row reordering do not move focus to a different job.
- [ ] Clicking a regular row does not select it or change the record.
- [ ] Clicking checkbox cell toggles selection and selection-only toolbar replaces search area.
- [ ] Header checkbox toggles all visible rows; Ctrl+F clears checked selection.
- [ ] Clicking file-type icon opens independent Details; right-click and ellipsis open same menu.
- [ ] Keyboard arrows, Space, Enter and Shift+F10 work; focus stays on the same download when rows refresh or reorder.
- [ ] Queue and completed states show NO fake progress bars or speeds; missing is distinct from failed.
- [ ] Completed and missing Details show no irrelevant speed/ETA. Test Open, Locate, and Delete with a missing file and a same-path replacement; only the unchanged, verified completed file may be opened or deleted, and any conflict leaves an actionable entry.
- [ ] Add Download validates HTTP and HTTPS URLs, rejects torrent, collapsed Save-to expands and browses a folder.
- [ ] Add a file from a local HTTP fixture with Download and another with Queue. Both create real engine jobs. With the transfer slots occupied, Download goes ahead of existing queued jobs and Queue goes to the end; a queued job may start when the scheduler has capacity. Verify received bytes, final hash, collision handling, and history after restart.
- [ ] Settings Save persists and applies preferences; Cancel leaves saved preferences unchanged. Check that live concurrency and queue changes do not corrupt active transfers, and that proxy/private-network changes explain they apply on restart.
- [ ] Run the packaged uninstaller with its data checkbox left off, then in a disposable install opt in. Confirm the default preserves settings/history/downloads; opt-in removes only VESPER settings/history and recognized backups, preserving unrelated files and all downloaded media. Interrupt at each journal boundary and verify recovery restores or completes the intended operation.
- [ ] With a disposable Windows network profile, enable Pause on metered or unknown networks. Confirm queued jobs wait, active transfers checkpoint, Start now overrides only the selected job, disabling the preference resumes policy-held jobs, and an unmetered transition wakes waiting jobs. Record Windows-reported cost and whether COM notifications were delivered.
- [ ] Import and export JSON and CSV lists under Settings → Downloads → Download lists. Verify confirmation before queued items start, paused entries remain paused, malformed/oversized lists and credential-bearing URLs are rejected, duplicate items are reported, and exported content contains no access credentials or Chrome-owned/completed jobs.
- [ ] Hide missing completed records does not hide paused/active records.
- [ ] Density 34/46/58 at 100/125/150/200% DPI and increased OS text size shows no clipping.
- [ ] Light/dark/system themes checked.
- [ ] v9 header branch, if enabled, remains noninteractive and clear of all controls and footer.
- [ ] Idle CPU is measured with no active jobs and no ongoing animation. Record GDI, working set, and scroll p95 using the 10,000-row synthetic UI preview fixture, with the fixture, build, and hardware identified in the saved results.
- [ ] Illustrated ICO looks sharp enough in real Windows taskbar / Explorer at 16/24/32/48 sizes.
- [ ] With no queued or active jobs, changing and saving settings does not contact a download endpoint or modify downloaded files. If testing the manual release check, verify it uses the configured GitHub release path and opens notes only after the user selects them.

## Isolated Chrome ownership smoke check (optional)

This focused harness is supplemental. The registered-host Chrome ownership matrix remains a required Q2 release gate.

Chrome removed the `--load-extension` command-line path in Chrome 137. The opt-in harness loads the unpacked VESPER extension into an isolated Chrome for Testing profile with CDP `Extensions.loadUnpacked`; a CFT-only check under the normal browser sandbox returns the expected ID. In this Codex sandbox, the extension page and service worker fail to launch with `errorCode 49`, before native messaging. `--no-sandbox` and sandbox-disabled diagnostics are not acceptance evidence. Run the full registered-host matrix in a disposable Windows environment where Chrome's normal renderer sandbox can launch; never use the regular profile.

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
| First Flight install | Start Setup and watch the full intro through completion. Try Skip intro. Repeat with a fast install, a failure, cancellation, and Windows animations turned off. | Installation starts immediately and reports real progress. The eight pictures and butterflies lead to the publisher wordmark. Completion remains actionable while the intro finishes. Skip intro works, and the intro plays regardless of the Windows animation setting. Failure or cancellation appears immediately. |
| Chrome capture and fallback | Set Browser downloads to Send to VESPER automatically. Start a direct file download and a safe ZIP from a redirecting API endpoint. Repeat with Use my sign-in off, a never-capture rule, a rules-refresh timeout, and VESPER/native messaging unavailable. | Only fresh downloads observed in `onCreated` with zero bytes already received may be considered for automatic capture, and only replayable GETs with proof of source identity may transfer. Chrome pauses only after native transfer readiness and is canceled only after verified VESPER completion. A never-capture match or unavailable policy keeps the URL and any consented cookies out of the native request. Any Chrome-owned download, including a file Chrome finishes or starts receiving before acceptance, remains Chrome-owned and is never imported into VESPER or labeled completed by VESPER. |
| Video sign-in | In a fresh profile, open the media picker before and after granting Chrome's sign-in permission once. Repeat on a supported site with Use my sign-in off. Try a file download from an HTTPS host without file sign-in approval. | The preference starts on. Opening a supported site's picker can use the current site's HTTPS session once Chrome grants access. Off disables cookie use. Files still require exact-host approval. |
| Butterfly motion | Switch between Animate and Off on a playing video. | Off stops butterfly and picker motion while keeping the button and picker usable. Animate restores motion. |
| Native messaging and reconnect | Submit the same item twice from the picker, close and reopen VESPER, then submit a new item. | Replies stay associated with the originating request. Repeated requests do not create duplicate jobs. Reconnect preserves authorization and leaves Chrome-owned downloads in Chrome. |
| Smart Select | On an ordinary webpage, open Smart Select from the VESPER context menu. Add a reusable file link dynamically, filter, select a subset, and choose Review in VESPER. Uncheck one link in desktop review and choose a folder. Exercise a page-created download separately using the site's own button. | Only the confirmed links go to the chosen folder. A page-created download that cannot be replayed stays with Chrome. |
| YouTube watch, Shorts, and hover preview | Open a public watch page and a Short. On the homepage, hover several video cards and wait for each preview to start without moving the pointer; open VESPER's picker, navigate away, and repeat. | Each picker stays bound to the card/page whose player started, including delayed hover previews. Repeated interactions create no stale or duplicate item. Only currently usable formats are offered. |
| Instagram Reels and TikTok feed | Try a public Reel and feed video, then navigate between items while playback or discovery is active. | Any offered media belongs to the current page. Unsupported or protected media receives an honest fallback; no duplicate, stale selection, or false success appears. |
| Media choices and subtitles | On a supported direct-file item with multiple formats/tracks/captions, inspect the desktop and browser pickers; choose video quality, audio-only, and one or more subtitle languages. Keep embedding off once, then opt in for SRT/WebVTT MP4 and Matroska outputs. Include an invalid caption and a selection that exceeds available track slots. Play the result and open sidecars. | The selected tracks and format match the output. Embedding is opt-in. Unsupported or excess captions follow the documented sidecar path, with a clear result. |
| HLS discontinuity playback | Use a permitted MPEG-TS HLS sample with a compatible timestamp discontinuity, then a sample that changes its program or codec profile. Repeat resume from a committed segment boundary and inspect output with a player and media probe. | Compatible TS epochs keep continuous timestamps and packet continuity. Changed or unverifiable layouts/configurations and fMP4 discontinuities refuse safely before unsafe bytes commit. Resume does not refetch committed segments or publish a corrupt file. |
| Queue completion actions | In a disposable Windows session, test open-folder and sleep settings separately. Complete a queue, cancel the sleep prompt, let a separate disposable run time out, and add new work during the countdown. | Actions default off. The folder opens for the latest completed native download. Sleep starts only after an explicit click; timeout, Cancel, new work, settings changes, or shutdown do not sleep the PC. |
| Per-site rules | Add an exact-host audio-only, folder, and never-capture rule. Test subdomains, direct paste, mixed-host batches, explicit folder override, browser submission, redirects, and unavailable native policy refresh. | Hosts match exactly. A manually chosen folder wins; mixed-host batches use the global folder. Never-capture keeps automatic downloads in Chrome before the URL or cookies reach native code; manual submission remains available. |
| Expired link and retry | Pause or queue a supported short-lived media link until it expires, then resume or retry. Repeat with a browser-owned transfer. | VESPER explains the failure and offers a safe retry/reselection path. It does not reuse unverifiable partial media, report a false completion, or cancel Chrome before the replacement is accepted. |
| Music artwork and tags | Download an authorized supported music item with source-provided artwork and metadata; open the result in a Windows music player and inspect tags with an independent format reader. Repeat with no valid artwork. | The player shows the matching cover, title, artist, and album where that output format supports them. Available track/disc/date values are preserved where supported. Missing artwork never causes a fabricated image or a failed audio download. |
