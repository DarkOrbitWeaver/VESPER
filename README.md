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

VESPER has no telemetry or account service. Network requests are made for links and media discovery that the user submits.

## Build and test

Use Windows 11 x64 with the .NET 10 SDK and Node.js for the extension tests.

```powershell
.\scripts\Run-Tests.ps1
.\scripts\Build-Release.ps1 -Version 1.0.0
```

The build creates a self-contained setup executable and a portable ZIP. Add `-PortableOnly` to build only the ZIP; add `-SelfContained` to include the .NET runtime in that ZIP. Release executables are unsigned.

## Limits and verification

VESPER does not bypass DRM, sign-in gates, or site JavaScript challenges, and it does not transcode media. Its native remuxer accepts only compatible, validated tracks; live capture and separate HLS audio renditions are not supported. Site compatibility depends on the current site implementation.

See [release status and verification](DOCUMENTATION/RELEASE.md) and [current media limits](MEDIA_ENGINE_LIMITATIONS.md). Clean-machine installation, mixed-DPI interaction, live VPN/site coverage, and matched comparisons with IDM, FDM, and yt-dlp still need manual runs.
