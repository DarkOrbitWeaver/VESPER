# VESPER — stable v8.3 WinForms UI correction (U2)

**Open:** `VESPER/VESPER.slnx` in Visual Studio on Windows 11 with the .NET 10 desktop workload.

**To preview/edit the main form in Visual Studio:** in Solution Explorer, expand `Form1.cs` and choose **View Designer** (Shift+F7). The masthead, buttons, search/filter/sort toolbar, list, and footer now come from `Form1.Designer.cs` instead of an empty designer form. The sample rows are provided by `DownloadGrid` and in-memory demo records at construction time. This is actual WinForms UI, not HTML.

U2 is focused on **design/layout only**. The real download engine, file opening, rescue, deletion, persistence and transfer capabilities are deliberately absent.

Changes from U1:
- Main controls are designer-backed. Runtime wiring remains in `Form1.cs`.
- Stable v8.3 spacing, labeled Show/Sort controls, native search surround, restrained colors and proportioned list columns.
- Faint stable header foliage; stronger v9 decoration remains **opt-in** in Settings.
- Settings Appearance note text moves *below* the motion chooser; long descriptions wrap and no longer require a horizontal scrollbar at supported window widths.
- Settings footer simplified, with Save settings / Cancel and no oversized demo text.
- Asset lookup supports the compiled app and Visual Studio designer's alternate working directory.
- All sample-data operations remain separate from actual disk/network operations.

**Important verification limit:** Prepared in a Linux environment without .NET SDK or the Windows WinForms design host. Static checks passed, but this ZIP has **not** been compiled or visually exercised in Windows. Please build and open **View Designer**; a designer exception or compiler error screenshot is the most useful next input if the environment exposes an issue.

See `DOCUMENTATION/U2_DESIGNER_AND_VISUAL_CHECKLIST.md` and `DOCUMENTATION/U2_STATIC_QA_RESULTS.txt`.


## Media-engine reliability update

This working branch updates the existing WinForms/engine solution, not a replacement application.
See [implementation and executed checks](MEDIA_ENGINE_REPORT.md), [verified compatibility](MEDIA_ENGINE_COMPATIBILITY.md),
[remaining limitations](MEDIA_ENGINE_LIMITATIONS.md), and [raw benchmark methodology/results](reports/BENCHMARKS.md).
The update deliberately does not claim yt-dlp parity, live YouTube success, or a general separate-track muxer.
