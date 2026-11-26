# Install and build VESPER

VESPER is under development. Public release is pending.

## Install

Run the standalone `VESPER-Setup.exe` on Windows 11 x64. To connect an unpacked development extension, open Browser settings in VESPER, choose **Open extension files**, then use **Load unpacked** at `chrome://extensions` with Developer mode enabled. Select the extension folder VESPER opened.

The installer runs per user. Ordinary uninstall keeps downloads and app data.

## Development preview versioning

Every user-testable change milestone must increment the shared release version in `Directory.Build.props` and the numeric `version` and display `version_name` in `extension/manifest.json`. Increment the numeric patch version even for previews (for example, 1.0.4-preview.1 to 1.0.5-preview.1); changing only the preview suffix does not trigger a Chromium extension version increase.

Package the desktop, native helper and extension together after verification. Report the exact installer path and extension version. Pushing source alone does not update an installed extension. For an unpacked installation, install the matching desktop build, reload the existing extension at `chrome://extensions`, then refresh the pages being tested. Preserve the extension manifest key and existing browser integration identity. Never claim an installed build has been updated without checking it.

## Build from source

Use the .NET 10 SDK and Node.js on Windows.

```powershell
.\scripts\Run-Tests.ps1
.\scripts\Build-Release.ps1 -InstallerOnly -AllowUnsigned
```

The build writes the offline installer and its SHA-256 checksum to `release/setup-<version>/`. Unsigned builds are for local testing. Use Authenticode signing for public distribution.

See [media support](../MEDIA_ENGINE_LIMITATIONS.md) for available formats, clipping, and conversion.
