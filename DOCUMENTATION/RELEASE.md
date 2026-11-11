# VESPER 1.0.0 — release notes, status and verification

**Status: release candidate.** The engine, storage, bridge, HLS and extension logic are verified by automated tests that
were actually run. The WinForms UI, tray, native host and Chrome behaviour were **compiled but never run** (the work was
done on Linux). Do not treat VESPER as "ready to replace your downloader" until the Windows checklist below passes.
The executables are **not code-signed**; Windows SmartScreen may warn on first run.

## What was actually run

| Check | Result | Where / how |
|---|---|---|
| `dotnet build VESPER.slnx -c Release -p:EnableWindowsTargeting=true` | 0 errors, 0 warnings | .NET SDK 10.0.404 on Linux x64 (WinForms compiled, not executed) |
| `dotnet test tests/Vesper.Tests -c Release` | **129 passed, 0 failed** (run three times in a row on the final code, no flakes) | Linux x64, 2 cores, real sockets/files via a Kestrel fault server |
| `node --test tests/extension` | **6 passed, 0 failed** | real `background.js` in a Node VM with a mocked `chrome` API |
| Extension scripts | `node --check` clean; `manifest.json` valid | Node |
| Windows runtime of app / host / installer scripts | **not run** | needs Windows 11 |

Test coverage that matters most (all in `tests/Vesper.Tests`): byte-exact sequential/chunked/unknown-length downloads;
resume after mid-stream disconnect; servers without ranges (replay-verify); changed ETag / changed tail; lying `Content-Range`;
429/503 + `Retry-After`; redirect loops; 404 vs expired link; HTML (captive portal) refusal; wrong gzip; malicious file names;
no-overwrite; duplicate/idempotent submissions; private-address refusal; trusted-hash match/mismatch (Suspect, not published);
Rescue R0 / R1 (match and mismatch) / R1b (corrupted stored piece repaired) / R4 (partial untouched); remove vs delete vs discard;
**simulated process death** at `checkpoint.beforeFlush`, `afterFlush`, `afterCommit`, `finalize.beforeMove`, `afterMove`;
full-disk stop; lost database rebuilt from recovery manifests; corrupt DB quarantined; newer-schema DB left untouched;
40 concurrent jobs with a handle-leak check; zero-byte file behind a server that rejects ranges; 10 simultaneous browser requests; repeated pause/resume; HLS plain/AES-128/master/DRM/live/crash-resume;
native-messaging framing, validation (no destination/program fields, header-injection, credentials only with consent) and a real named-pipe round trip.

## Measured (sandbox, not Windows) — `dotnet test --filter Category=Benchmark`

Loopback, 96 MiB file, server and client on the same 2-core Linux VM, so the *server* is the bottleneck:
sequential **59 MB/s**, up to 4 connections **89 MB/s**; process working set grew by ≤ 21 MiB while downloading 96 MiB (buffers are bounded).
These numbers say nothing about real networks and **must not be compared** with FDM/IDM. No startup, idle-CPU, scrolling, menu-latency
or GDI measurements exist yet: run `scripts\Measure-Performance.ps1` on Windows. For a fair FDM/IDM comparison use one controlled
server (same file, same link, same machine, cold cache, ≥ 5 runs each, record median and spread) — not done.

## Feature status

Legend: **V** implemented and verified by tests that ran · **C** implemented, compiles, *not yet verified* · **P** partial · **N** not implemented · **X** intentionally unsupported.

| Area | Status | Notes |
|---|---|---|
| HTTP/HTTPS sequential, chunked, unknown length, redirects, timeouts, stall detection | V | |
| Pause/resume, restart recovery, retained progress | V | |
| Retry with backoff + jitter, `Retry-After`, give-up after N fruitless attempts | V | |
| Segmented download with per-response range validation, fallback to one connection | V | loopback only; real-network gain unmeasured |
| Adaptive connection count | P | advisor unit-tested; live ramp observed only on loopback |
| Global / per-job bandwidth limits | P | limiter unit-tested; end-to-end throttling of a live transfer not tested |
| Priorities, queue order, per-host limits, fairness | C | logic written, no dedicated scheduler tests |
| Schedule window (start/pause) | P | window maths tested; engine pause/resume at boundaries not tested |
| Disk-space pre-check, full-disk handling | P | simulated full disk tested; pre-check on a real small volume not |
| Safe publish, collision handling, Mark-of-the-Web | V / C | publish V; MOTW write is Windows-only (C) |
| Crash recovery at every durability boundary | V | |
| Rescue R0 / R1 / R1b / R4, Replace Link dialog | V (engine) / C (dialog) | |
| Verify integrity | V | honest "no trusted checksum" wording |
| Remove vs Delete file vs Start over | V (engine) / C (confirmations) | delete uses the Recycle Bin |
| SQLite store, migrations, corruption quarantine, manifests | V | |
| Native-messaging protocol + pipe server/client | V (Linux) / C (host exe, Windows) | |
| Chrome extension: picker logic | V | |
| Chrome extension: butterfly overlay, popup, context menus, opt-in takeover, sign-in option | C | needs a real Chrome |
| HLS VOD: plain, AES-128, master selection, DRM/live refusal, resume | V | |
| HLS fragmented MP4 output | C | parser tested; no fMP4 end-to-end test |
| DASH | N | detected and refused with an explanation |
| HLS with separate audio rendition | X | needs a muxer; refused |
| WinForms main window, menus, details, add/rescue/quick/settings dialogs, tray, notifications, drag-drop, clipboard offer, sleep prevention | C | |
| Light/dark, density, DPI, high contrast, accessibility | C | pre-existing prototype behaviour; not re-tested |
| Illustrated icon (7 sizes in ICO) | V (sizes) / C (taskbar legibility) | |
| Proxy (direct / system / custom) | C | private-address check is weaker with a proxy |
| Packaging, install, uninstall, upgrade/rollback scripts | C | PowerShell, not run |
| Torrents, media trimming/cropping, mirrors, authorized site discovery, completion power actions | N | isolated future work; not advertised |
| Free-space warning in the extension, link-expired and Rescue prompts in the browser | X | the desktop app shows these; the extension only knows "sent / not sent" |

## Known limitations

* The Quick Download window has no thumbnail (the browser picker does); the extension cannot display desktop-side errors after hand-off.
* Download destinations cannot be changed after a job started. Changing the default folder never moves existing files.
* "Resume anyway" exists only for servers with no validator at all, and always asks first.
* With a proxy, private-network blocking cannot inspect the resolved target. Network/proxy options apply after restart.
* The recovery manifest deliberately omits the query string of the link, so a rebuilt entry needs *Replace link*.
* HTTP/2 multiplexing was not measured and is not used.

## Install / upgrade / rollback / uninstall

* **Install:** unzip, run `Install.ps1` (per user, no admin). **Upgrade:** run the new `Install.ps1`; the old folder is kept as
  `VESPER.previous`; the database is copied to `vesper.db.bak-v<N>` before any schema migration.
* **Rollback:** exit VESPER, swap `VESPER` and `VESPER.previous` under `%LOCALAPPDATA%\Programs`. A database created by a *newer*
  schema is refused (never modified) with an explanation; restore the `.bak-v<N>` copy if you must go back.
* **Uninstall:** `Uninstall.ps1` removes the program, shortcut and browser registration and keeps `%LOCALAPPDATA%\VESPER`
  (history, settings, recovery data) unless `-RemoveData` is confirmed. Downloaded files are never deleted.
* **Deployment choice:** framework-dependent by default (small; needs the .NET 10 Desktop Runtime); `-SelfContained` bundles the runtime.
  Startup and size were not measured, so this is a default, not a conclusion.

## Manual Windows 11 checklist (nothing below has been executed)

**Build/package**
- [ ] `.\scripts\Run-Tests.ps1` passes on Windows; `.\scripts\Build-Release.ps1` produces `release\VESPER-1.0.0.zip`; `Install.ps1` works without elevation.
- [ ] Explorer, taskbar, Alt-Tab and Start show the illustrated icon at 100/125/150/200 % on light **and** dark taskbars.

**Engine on a real network**
- [ ] Small and multi-GB downloads match a known SHA-256; Wi-Fi off/on, sleep/resume and reboot mid-download resume correctly.
- [ ] Kill VESPER with Task Manager mid-download and mid-finish; restart; file is byte-exact and published once.
- [ ] Disconnect an external drive mid-download: job says *Needs attention*, data kept; reconnect and *Try again* completes.
- [ ] Expired signed link → *Needs a new link* → Replace link with a fresh link: R0 reuses data; a different file is rejected and the partial is unchanged.
- [ ] Antivirus lock at publish: job retries, never reports completed early. Cloud-placeholder files are not deleted.
- [ ] Segmented speed vs FDM/IDM on the *same* link, ≥ 5 runs, cold cache; record median and spread.

**UI**
- [ ] Row click changes nothing; checkbox selects; file icon opens Details; dots and right-click show the same menu; first right-click is instant.
- [ ] Menus: rounded, full-width hover, hand cursor, submenu adjacent, correct on a second monitor and near screen edges.
- [ ] Details for each state shows only relevant actions; Locate file for a missing file; Needs-link shows Replace link.
- [ ] Add Download: Queue and Download behave as specified; duplicate link is reported; bad folder is rejected.
- [ ] Settings persist across restart and change engine limits; Browser page installs/removes the connection.
- [ ] Densities 34/46/58 px; DPI 100–200 %; high contrast; keyboard and screen reader; long file names; tiny/huge windows.
- [ ] Closing the window with active downloads goes to the tray; *Exit* from the tray asks before pausing.
- [ ] Idle CPU ≈ 0, no timers while idle, handle/GDI counts stable (`Measure-Performance.ps1`), 10,000 history rows scroll smoothly.

**Browser**
- [ ] Load unpacked; Settings → Browser → Install connection; popup says *Connected*. Send a link: Quick Download appears, Cancel/Queue/Download work.
- [ ] VESPER closed: sending starts it and the request succeeds. Host unregistered: clear *connection isn't installed* message.
- [ ] Butterfly appears on a page with a direct MP4 and on an HLS player, not on blob-only/DRM players; no duplicates after resize, fullscreen, SPA navigation.
- [ ] Opt-in takeover: Chrome's own download is cancelled only after VESPER accepted; if VESPER refuses, Chrome keeps downloading.
- [ ] "Use my sign-in" sends only that site's cookies, only when ticked.

## Security and privacy summary

No telemetry, accounts, uploads or remote control. The only network traffic is the downloads you start (and the one probe to a replacement link).
The browser can only ask VESPER to add or describe http(s) links; it cannot choose files, folders, programs or actions.
The native-messaging host is registered per user for one fixed extension ID; the pipe accepts only the same Windows user.
Cookies/Authorization are optional, same-origin-only, in-memory only. Logs redact query strings and your profile path.
Details: `DOCUMENTATION/ENGINEERING.md`.
