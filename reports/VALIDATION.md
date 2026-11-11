# Executed validation

Linux x64 / .NET SDK 10.0.404 / 2026-11-11. These are actual executed results, not the user's historical baseline.

| Operation | Actual result |
|---|---|
| Exact baseline engine tests | 161 passed |
| Exact baseline resolver tests | 294 passed |
| Baseline extension tests | 6 passed |
| Updated full solution Release cross-build | Passed; 0 compiler warnings, 0 errors |
| Updated engine tests, Release | 211 passed; 2 Windows DPAPI cases explicitly skipped |
| Updated resolver tests, Release | 314 passed |
| Updated extension tests | 6 passed |
| Updated developer benchmark Release build | Passed; 0 warnings, 0 errors |
| Total updated automated tests | **531 passed, 2 skipped, 0 failed** |
| Public complete HLS transfer | Passed; independently probed/fully decoded with current FFmpeg tool |
| Public complete DASH audio transfer | Passed; independently probed/fully decoded |
| Public-domain Archive collection | 12 entries enumerated; exactly 2 explicitly selected jobs queued; no chapter bodies downloaded |
| Live YouTube attempts | Failed with `BrowserContextRequired`; no download verified |
| Live Vimeo sample | Resolver returned `DrmProtected`; no transfer verified |

Commands executed from the repository root:

- `dotnet build VESPER.slnx -c Release -p:EnableWindowsTargeting=true --nologo -v minimal`
- `dotnet test tests/Vesper.Tests -c Release --nologo -v minimal --logger trx`
- `dotnet test tests/Vesper.MediaResolver.Tests -c Release --nologo -v minimal --logger trx`
- `node --test tests/extension/background.test.mjs`
- `dotnet build src/Vesper.MediaResolver.Benchmarks -c Release --nologo -v minimal`

Build/test stdout and TRX evidence: `validation/`. Live checks and measurements: `benchmarks/` and `BENCHMARKS.md`.
The test SDK emits a non-failing `VSTEST_ARTIFACTS_DIRECTORY` note; its existing package version was not changed or suppressed.

Not executed: actual WinForms UI/extension/native-host registration, DPAPI round-trip on Windows, PowerShell release/source scripts and the added remote Windows CI workflow.
Run `scripts/Run-Tests.ps1` on Windows to execute the DPAPI cases as well as both full regression projects.
No blanket live-site support, universal decoding, separate-stream muxing, or yt-dlp speed parity is asserted.
