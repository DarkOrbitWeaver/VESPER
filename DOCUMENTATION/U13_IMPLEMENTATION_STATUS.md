# VESPER U13 — Media UX and browser repair

This update modifies **the uploaded VESPER-source-20261111-005352.zip**, not the older U12 archive.

## Implemented in source
- Replaced the raw native `MediaDiscoveryForm` with VESPER's popup chrome, painted buttons and choices; branded source/thumbnail area; compact failure and inspection states; conditional exact custom-format chooser; state-aware Queue/Download actions. Real quality entries are derived from discovered representations, not hard-coded 8K promises.
- Added an opt-in, bounded, HTTPS-only thumbnail fetch method through the resolver's existing redirect, IP and timeout safeguards. Only JPEG/PNG/GIF up to 768KB are considered; image dimensions are bounded. Missing artwork shows the normal video glyph.
- Preserved original webpage, provider, media ID, selected quality, thumbnail reference, and known source expiry through `AddRequest` → durable `DownloadJob` → SQLite migration version 2 → `JobSnapshot` and Download Details. “View original” is a Details action, not a crowded row context menu.
- Mixed direct files and media pages are accepted in one pasted batch; `.mpd` manifests are routed to media discovery; the batch limit is explicit (200 links) instead of silently truncating to 25. Per-item errors are reported before partial acceptance.
- Browser Settings no longer exposes normal-use Connect/Disconnect buttons or calls a registry entry a live browser connection. Installed releases register the native helper on startup if repair is needed; `Install.ps1` already registers it as part of installation. Repair is a low-prominence option. Extension approval is still controlled by the browser and user.
- The Chrome extension defaults to handling selected ordinary file types after the user installs/enables it. It can be disabled in the extension. Chrome's own file is canceled only after VESPER confirms acceptance of that file. Friendly error copy now points to repair rather than a removed Connect button.
- Release and Run-Tests scripts invoke the media resolver test project in addition to engine and browser logic tests.
- Added a provenance persistence regression test and bridge protocol tests for browser-assisted discovery hints.

## IMPORTANT: Not claimed as complete
- YouTube and other frequently changing commercial sites still have *unverified live* extractors. This release cannot promise it downloads the submitted YouTube Mix.
- The existing transfer engine still does not implement generic DASH/fMP4 assembly or safe video+audio muxing. The picker deliberately refuses to queue an output it cannot finish. No yt-dlp, Python, ffmpeg, external browser or custom DRM bypass was bundled.
- Automatic refresh of expired signed media URLs (including signed URL identity revalidation for partially completed jobs) is **not implemented**. The original page and expiry are stored; the user can see the origin in Details. Do not claim automatic refresh/recovery without identity-proof integration.
- The extension now exposes **Find formats in VESPER**, which opens the same native resolver/picker as pasted links and passes bounded browser-discovered media hints. The original butterfly quick-quality path still exists as a separate shortcut; full behavioral parity, sign-in scenarios and installed-browser integration need Windows/browser testing.
- The existing PowerShell release package still expects users to load the unpacked developer extension until a published browser store listing exists.

## Windows verification required

Run from the extracted solution root:

```powershell
 dotnet build VESPER.slnx -c Release
 dotnet test VESPER.slnx -c Release
 node --test tests/extension/*.test.mjs
```

Then inspect the media window at 100%, 125%, 150% and 200% DPI; test dark/light themes, keyboard navigation, poster fetch, mix/direct-file batches, SQLite v1→v2 upgrade, installed browser handshake, and protected/expired media. **This workspace has no .NET SDK or Windows GUI; no new Windows build/test result is claimed.**

## Verification performed in this environment (2026-11-11)
- `node --test tests/extension/*.test.mjs`: **6 passed, 0 failed**.
- `node --check` on background.js, content.js, popup.js: **passed**.
- The ZIP package is generated without git/IDE output or a redundant parent directory.
- No .NET SDK, C# compiler, Windows GUI or live website/browser session is available here; the new C# tests are provided **but not executed**. The user's prior clean build and 449 passing .NET tests were from the incoming ZIP **before** these changes.
