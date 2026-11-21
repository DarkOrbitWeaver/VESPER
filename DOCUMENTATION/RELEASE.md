# Install and build VESPER

VESPER is under development. Public release is pending.

## Install

Run the standalone `VESPER-Setup.exe` on Windows 11 x64. To connect an unpacked development extension, open Browser settings in VESPER, choose **Open extension files**, then use **Load unpacked** at `chrome://extensions` with Developer mode enabled. Select the extension folder VESPER opened.

The installer runs per user. Ordinary uninstall keeps downloads and app data.

## Build from source

Use the .NET 10 SDK and Node.js on Windows.

```powershell
.\scripts\Run-Tests.ps1
.\scripts\Build-Release.ps1 -InstallerOnly -AllowUnsigned
```

The build writes the offline installer and its SHA-256 checksum to `release/setup-<version>/`. Unsigned builds are for local testing. Use Authenticode signing for public distribution.

See [media support](../MEDIA_ENGINE_LIMITATIONS.md) for available formats, clipping, and conversion.
