# VESPER 1.0.0 release status

VESPER is a Windows release candidate. The source and isolated browser workflows have been exercised on Windows, but clean-machine setup and several real-device checks remain. The setup and app executables are unsigned; Windows SmartScreen may warn on first run.

## Verification run on 2026-11-12

| Check | Result |
|---|---|
| Engine, bridge, and storage tests | 275 passed |
| Media resolver tests | 328 passed |
| Setup tests | 14 passed |
| Desktop persistence and grid tests | 4 passed |
| Chrome extension tests | 40 passed |
| Release apphost builds for `98ee2db` and the current worktree | 0 warnings, 0 errors |
| 10,000-row preview | 5.38 ms current grid-draw p95; GDI count change after 20 main windows: 0 |
| Paired startup, idle, and throughput comparison | All gates passed; details below |
| First Flight setup executable | Launched; all 254 staged payload files passed verification |
| Isolated Chrome run | Motion, fullscreen, player switching, overlay cleanup, and byte-for-byte local Chrome fallback download passed; native picker replies were simulated |

The .NET suites ran as separate projects. The 2026-11-12 performance comparison used a framework-dependent apphost and the installed .NET 10.0.15 runtime, built with SDK 11.0.100. Its machine query could not read processor model or RAM through CIM. Raw performance data is in the ignored `release/performance-20261112-212025` directory. The current revision was `62ed91f` with uncommitted changes.

The normal test runner could not restore `Vesper.MediaResolver.Tests` because this environment blocks access to NuGet. Its existing Release test assembly, built before the final nonfunctional warning/comment wording edit, was run directly with VSTest and passed 328 tests; the current resolver library then built with zero warnings. Other suites ran through `Run-Tests.ps1`. NuGet vulnerability-feed warnings on the restored projects are environmental.

## Performance comparison

The script compared 20 startup launches and five 96 MiB loopback transfer runs per revision. It also measured one 60-second idle window and preview paint time for 10,000 rows.

| Metric | Baseline `98ee2db` | Current worktree | Gate |
|---|---:|---:|---|
| Startup median | 498.47 ms | 494.07 ms | pass; at most 30 ms regression allowed |
| Idle CPU | 0% | 0% | recorded |
| Idle private memory median | 21.60 MiB | 21.66 MiB | pass; at most 12 MiB regression allowed |
| Idle working set median | 70.70 MiB | 71.96 MiB | recorded |
| Grid draw p95 | 5.23 ms | 5.38 ms | pass; maximum 16.7 ms |
| Loopback throughput, one connection | 36.71 MiB/s | 35.40 MiB/s | pass; 96.4% of baseline |
| Loopback throughput, four connections | 78.66 MiB/s | 78.71 MiB/s | pass; 100.1% of baseline |

These are local regression measurements, not a real-network result or a comparison with IDM, FDM, or yt-dlp. Long-session memory behavior and matched third-party runs remain unmeasured.

## Release build

Run on Windows 11 x64 with the .NET 10 SDK and Node.js:

```powershell
.\scripts\Run-Tests.ps1
.\scripts\Build-Release.ps1 -Version 1.0.0
```

The build produces `release/setup-1.0.0/VESPER-Setup.exe` and `release/VESPER-1.0.0.zip`. First Flight is a self-contained per-user installer. It verifies its staged payload, supports repair and uninstall, and journals upgrades for rollback. Uninstall keeps downloads, settings, and history. Extension enablement is a separate Chrome action. The portable ZIP does not register the native helper; use `-PortableOnly` to skip setup and `-SelfContained` to bundle the .NET runtime in the ZIP.

## Current behavior and limits

- Direct HTTP and HTTPS downloads support pause/resume, retries, restart recovery, collision-safe publication, and the Rescue workflow.
- Media discovery and format selection cover supported providers. The in-process C# remuxer joins compatible separate HTTP tracks without transcoding. The existing M4A and original-audio choices remain available.
- The Chrome extension can send links and supported media hints to VESPER, or retain Chrome-owned transfers when its connection is needed. The isolated test simulated native picker replies, so installed native-host handoff still needs a live test.
- VESPER does not run yt-dlp, Python, or an external downloader. It does not bypass DRM, sign-in gates, or site JavaScript challenges. Live capture and separate HLS audio renditions are not supported. Native remuxing is limited to compatible validated tracks.
- The extension uses all HTTP/HTTPS hosts and frames to detect direct links and embedded player sources. Background request handling filters to relevant request types. Cookie access is optional and defaults off.
- System-proxy routing selects against the original destination, then pins the request target to a validated public address. The wrapper exposes only `IWebProxy`, so it cannot use .NET's multi-proxy PAC fallback path; if the first route fails, the next PAC route is not tried automatically. The HTTP CONNECT target and original Host are covered by local proxy tests. .NET derives TLS SNI from that Host header, but this machine's SSPI failure prevented an end-to-end TLS handshake check.

## Still needs manual verification

- Install on a clean Windows account, then exercise repair, upgrade rollback, uninstall, and removal of the native-host registration. The setup UI and payload were checked, but this full flow has not been run on a clean machine.
- Cold-launch the installed native helper and test the installed extension with real picker replies and browser-owned transfers.
- Move the window across mixed-DPI monitors. Check light and dark themes, keyboard focus, high contrast, and screen-reader behavior.
- Test live VPN and site coverage. Compare VESPER, IDM, FDM, and yt-dlp on matched links with cold cache and repeated runs.
- Exercise system PAC proxy selection and failover on a network that provides multiple proxy routes; restore automatic fallback for that path.
