# VESPER — WinForms UI milestone U0/U1 (2026-11-09)

## What was inspected

- `VESPER.slnx` / `VESPER.csproj`: existing single-project .NET 10 WinForms solution, no external NuGet packages.
- `Form1.cs`, `Form1.Designer.cs`, `Program.cs`: original solution was a default empty WinForms application.
- Handoff: `README_START_HERE.md`, `01_CURRENT_DECISIONS.md`, `02_WINFORMS_UI_CONTRACT.md`, v8.3 screenshots and HTML, v9 fork notes and screenshots, `ASSETS_USAGE.md`, architecture v5, and engine invariants.

The handoff's framework selection was superseded by the current user's **WinForms** decision. The download-engine invariants remain authoritative when the engine milestone begins.

## Source layout

- `VESPER/Form1.cs` — native resizeable main window, compact masthead, toolbar, footer, commands, context menu, filtering and data projection. `Form1.Designer.cs` remains unchanged.
- `VESPER/DownloadGrid.cs` — **virtual-mode DataGridView**, six columns with owner drawing. Native scrolling/keyboard navigation and accessible cell value text. Checkbox cell toggles selection; file-type icon opens Details; right-click and ellipsis invoke one shared state-aware menu.
- `VESPER/VesperModels.cs` — **in-memory demo records** and theme/density preferences. This is deliberately not a transfer-state database.
- `VESPER/AddDownloadForm.cs` — compact HTTP(S) URL entry, on-demand per-item destination row, native folder picker, Download + queue chevron. Submitting creates a simulated queued row only.
- `VESPER/SettingsForm.cs` — left nav with Downloads, Transfers, Appearance; Save/Cancel working for session-only preferences. v9 botanicals opt in through Appearance. Motion is a preference placeholder only; no animations yet.
- `VESPER/DownloadDetailsForm.cs` — separate state-sensitive window. Completed/missing do not display fake transfer speed/progress or ETA; active/paused may show explicitly labeled sample metrics.
- `VESPER/Assets/` — supplied production transparent PNG assets and icon alternatives, plus an illustrated `ICO` / `PNG` candidate derived by isolating and cleaning the *provided* concept illustration.

## Design choices

- Clean **v8.3** is the default. Optional v9 botanical branch art is placed **inside the native client area**, above the list, not on rows or the 30px footer.
- Native Windows caption/title bar is preserved (no borderless form).
- No new frameworks, NuGet dependencies, WebView or browser UI.
- List uses 34 / 46 / 58 logical-pixel baseline densities; a font-height minimum is applied to avoid clipping at larger text sizes. Layout needs validation on physical Windows displays.
- Two theme palettes (light/dark) via a single theme structure. In `System` mode the app reads the current Windows apps-light preference; no registry writes.
- The illustrated icon is **a candidate**; on Windows verify 16/24/32px taskbar, Explorer, High Contrast and light/dark themes. The prepared original simplified icons remain available as fallback. The illustrated candidate is now referenced by `ApplicationIcon` and main Form icon to honor user preference, but it has **not** been accepted by a real Windows visual test.

## Functional vs simulated

### Working UI behavior (no engine needed)
- Search, filter, sort, checkbox selection and header check-all.
- Selection-only batch toolbar; simulated pause/resume and list record removal.
- State-aware context menu, Details dialog, edit demo display name, explicit copy of entered URLs.
- Add Download URL validation and optional per-item folder picking; queue option; simulated queue row.
- Settings navigation, Save/Cancel, appearance/theme/density, category folders setting, hide-missing setting and in-session transfer preference values.
- Native title bar, resizing, footer sample disclaimer and file-type-icon details launch.

### **Not implemented / never claimed**
- Real downloads, network traffic, HTTP validation, concurrency, resume, progress persistence or Rescue.
- File presence checks, verified Open, Open Folder, Locate, Delete file, or auto-run anything.
- Applied bandwidth caps, category subfolder creation, automatic retries, torrent handling.
- Persisted settings across restarts, download history database, OS taskbar progress, motion animations.
- Engine/server/crash/fault tests, startup/idle performance measurements, and Windows DPI/visual acceptance.

For demo-only rows, *Delete file*, *Open* and *Show folder* are disabled by design. *Remove from list* only removes session data, and explicitly says no file is touched. A Replace Link demo only records a URL; it does **not** claim source identity validation or retained-byte reuse.

## Windows build instructions

On Windows 11 with Visual Studio 2022/2026 or .NET 10 SDK plus the Windows Forms workload installed:

```powershell
cd VESPER
# Open VESPER.slnx in Visual Studio, or run:
dotnet restore VESPER.slnx
dotnet build VESPER.slnx -c Release
```

Run in Visual Studio, or launch the compiled `bin\Release\net10.0-windows\VESPER.exe`.

The source in this package was produced in a **Linux container without `dotnet` or Windows desktop runtime**. No real build or GUI execution has been performed here. The provided `STATIC_QA_RESULTS.txt` is a static sanity check only, **not a substitute for a Windows compile or tests**.

## Suggested next pass after your Windows build

1. Capture a v8.3 light screenshot at 1222px and 830px wide; compare against reference.
2. Test each density at 100, 125, 150 and 200% scaling, with Windows text-size changes.
3. Test both themes, Windows High Contrast, keyboard focus and Narrator accessibility, filter selection state and context menu positioning.
4. Verify taskbar and Explorer icons at 16, 24, 32 and 48px; refine the illustrated ICO if necessary.
5. Check GDI objects, task manager working set, idle CPU, resize/flicker, scroll behavior with 10,000 in-memory demo rows.
6. Only once the UI is accepted, begin engine **G2/G3** gates using deterministic HTTP fault tests and invariants; keep the engine separate from this demonstration model.

Nothing in this UI milestone demonstrates engine correctness or permits actual file operations.
