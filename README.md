# VESPER

VESPER is a native Windows download manager built with C#/.NET 10 and WinForms, with a Manifest V3 Chrome companion. It manages direct HTTP and HTTPS downloads, supported media formats, durable history, and browser handoff. The engine runs in-process and does not bundle or invoke yt-dlp or Python.

## What it does

- Queue, pause, resume, retry, and recover downloads after restart. VESPER validates partial data and publishes completed files without overwriting an existing file.
- Discover supported media and select available formats. VESPER can natively remux compatible separate HTTP video and audio tracks without re-encoding. M4A and original audio choices remain available.
- Send direct links and supported player hints from Chrome. Depending on the connection, downloads can use VESPER's native route or keep Chrome's transfer and connection.
- Install per-user with First Flight. Setup verifies its payload and supports repair, upgrade rollback, and uninstall. The browser extension is enabled separately in Chrome.

The interface keeps its compact botanical design and light and dark themes.

## Browser permissions

The extension matches HTTP and HTTPS pages in all frames so it can detect direct media links and sources inside embedded players on arbitrary sites. Background request listeners filter to relevant media and resource request types. Cookie access is optional and no cookie is read unless **Use site sign-in** is enabled in the extension settings.

VESPER has no telemetry or account service. Network requests are made for user-submitted links and supported automatic browser handoffs when that integration is enabled.

## Engineering continuation

For the current cross-audit roadmap and restart instructions, use [Master TODO](DOCUMENTATION/MASTER-TODO.md), [Next-chat handoff](DOCUMENTATION/NEXT-CHAT-HANDOFF.md), the [audit reconciliation ledger](DOCUMENTATION/AUDIT-RECONCILIATION.md), the [AAA-quality release gates](DOCUMENTATION/QUALITY-RELEASE-GATES.md) and the detailed [status register](DOCUMENTATION/STATUS.tsv). Original audit reconciliation and release gates are **not yet signed off**; historical release notes are not current build certification. Run `scripts/Verify-EngineeringBacklog.ps1` when updating the audit tracker.

## Build and test

Use Windows 11 x64 with the .NET 10 SDK and Node.js for the extension tests.

```powershell
.\scripts\Run-Tests.ps1
.\scripts\Build-Release.ps1 -InstallerOnly -AllowUnsigned  # isolated development build only
```

`-InstallerOnly` builds the self-contained First Flight installer at `release/setup-<version>/VESPER-Setup.exe` and writes its SHA-256 checksum beside it. Users can run the EXE directly. For portable builds, use `-PortableOnly`; add `-SelfContained` to include the .NET runtime in that ZIP. Shipping builds require an Authenticode certificate and verified signatures. Use `-AllowUnsigned` for isolated development builds. Packaging leaves Windows and browser registration alone.

## Limits and verification

VESPER does not bypass DRM, sign-in gates, or site JavaScript challenges. Its native remuxer copies compatible, validated tracks without re-encoding; the optional "plays everywhere" MP4 and M4A outputs use the Windows Media Foundation decoders and encoders already on the PC, where the necessary codecs are available; decoder probing and actual playback must still be verified on the target Windows machine. VESPER can merge compatible separate video and audio renditions for completed HLS and clear DASH plans. Live HLS recording currently requires a single usable rendition with audio included; separate live audio/video renditions are not supported. The single source of truth for what the media engine can and cannot do is [MEDIA_ENGINE_LIMITATIONS.md](MEDIA_ENGINE_LIMITATIONS.md); site compatibility depends on the current site implementation.

See [current build and release information](DOCUMENTATION/RELEASE.md), and [current media limits](MEDIA_ENGINE_LIMITATIONS.md). Clean-machine installation, mixed-DPI interaction, live VPN/site coverage, and matched comparisons with IDM, FDM, and yt-dlp still need manual runs.
