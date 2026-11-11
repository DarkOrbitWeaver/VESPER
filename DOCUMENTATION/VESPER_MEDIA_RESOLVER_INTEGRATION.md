# VESPER native media discovery integration (U12)

## What is integrated

- Claude Sonnet 5.5 High's `Vesper.MediaResolver` source is now a referenced C#/.NET 10 library. No yt-dlp, Python, bundled muxer or browser runtime.
- Paste a recognized single video-page URL in **Add download** and select **Find video**. The on-demand native resolver retrieves media formats. Choose Best, Balanced, Smallest, Audio only, or a custom *available* resolution.
- Recognized multiple video pages entered on separate lines use a bounded (25-item) selected batch. Related playlist browsing is offered when the resolved item includes an actual collection link. The app does not silently enumerate a YouTube Mix.
- The picker filters out incompatible representations. Supported *one-stream* HTTP/HLS plans go through the existing VESPER transfer engine, queue and history. Browser-extension transfer handoff continues to work as before.
- Existing extension is optional for the native-paste workflow; it may help for browser-specific pages.
- Resolver's original test project and fixtures are included in the main solution.

## Important restrictions

This is **not** yt-dlp-level verified compatibility. Claude's adapter implementations have not been live-tested or compiled in this environment. Some website adaptations may be incomplete or become obsolete.

- The existing transfer engine currently lacks DASH segment-plan execution and audio/video muxing. The picker does not queue those plans as if they worked. This also limits available high-resolution formats.
- Browser-assisted discovery is not yet using the new resolver message schema. The extension continues its existing direct/HLS discovery and handoff.
- HLS outputs use the engine's own TS/fMP4 file-extension selection.
- Media requiring sensitive authorization headers, origin headers or an unsupported processing helper is intentionally not queued by the native picker.
- Dynamic playlists and feeds may be incomplete; the UI limits actions to up to 25 listed items with user selection.
- No DRM circumvention, automated authentication bypass or stealth crawling.

## Build / verification on Windows

Open `VESPER.slnx` with .NET 10 installed, restore NuGet packages, then:

```powershell
dotnet build VESPER.slnx -c Debug
dotnet test VESPER.slnx -c Debug
```

Test the native workflow with legally accessible direct MP4 and HLS VOD media first. Then test specific public platform adapters one by one. Do not rely on successful static/source checks as proof of live compatibility.

## Rollback

Remove `MediaDiscoveryForm.cs`, the project references to `Vesper.MediaResolver` in `VESPER.csproj` / `VESPER.slnx`, and the new `src/Vesper.MediaResolver/` and `tests/Vesper.MediaResolver.Tests/` directories. Restore AddDownloadForm and Form1 from the preceding U11 ZIP. No persistent database migration was introduced.
