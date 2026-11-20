# VESPER — build and release information

**Development status: not approved for public release.** This file is packaged with the portable ZIP. It describes the current distribution rules without claiming that an arbitrary build passed acceptance testing. Find the exact installed build identity in VESPER's About information.

## Installing and connecting Chrome

Run `VESPER-Setup.exe` to install First Flight for your Windows account. The EXE contains the application and its runtime. It verifies its packaged files, supports repair, and retains downloaded files and app data during ordinary uninstall. Installer rollback, upgrade while running, and clean-machine behavior still require final acceptance testing.

The portable ZIP does **not** register the native Chrome messaging helper. A portable installation can run VESPER directly, but Chrome integration is unavailable until the helper is explicitly configured. The extension currently requires a separate manual development installation; a published Chrome Web Store channel and its permissions review are prerequisites for broad distribution.

Releases must use signed executables. The build script refuses unsigned packaging unless the developer explicitly opts into `-AllowUnsigned`. Such builds are **for isolated development and testing**, not distribution to ordinary users.

## What VESPER can do

VESPER manages HTTP/HTTPS downloads, pause/resume, conservative recovery, and collision-safe publication. It supports native media discovery, selected HLS/DASH sources, compatible remux and Windows-codec conversion where available. Transfer integrity takes precedence over claiming universal media compatibility.

**Cancel** stops an unfinished VESPER download and removes only its owned unfinished files and list entry; **Pause** keeps progress. Downloads already started or completed by Chrome remain Chrome-owned. If safe native takeover is impossible, VESPER must leave the Chrome download alone and explain why.

VESPER does not bypass DRM, site challenges, or sign-in restrictions. Some media outputs, subtitles, streaming discontinuities, and site-specific flows remain unsupported or partially tested. No telemetry, accounts, or cloud storage are required.

## Verification and remaining release gates

Before public distribution, the exact candidate needs signed-file and browser-channel verification, Windows and Chrome handoff and cancellation checks, clean install/repair/rollback/uninstall, mixed-DPI visuals, media playback and codec checks, and reproducible performance and security evidence. The owner has deferred the optional Q5 accessibility gate.

Current engineering decisions, supported-media limits, audit status, and the Q0–Q10 acceptance criteria are maintained in the source repository: `DOCUMENTATION/STATUS.tsv`, `MEDIA_ENGINE_LIMITATIONS.md`, `DOCUMENTATION/AUDIT-RECONCILIATION.md`, and `DOCUMENTATION/QUALITY-RELEASE-GATES.md`.

Consult the [engineering handoff in the source repository](https://github.com/DarkOrbitWeaver/VESPER/blob/main/DOCUMENTATION/NEXT-CHAT-HANDOFF.md) for current checkpoints and historical evidence.
