# VESPER U2 — designer and visual acceptance checklist

## Main form designer
1. Open `VESPER.slnx`, build, then right-click `Form1.cs` > **View Designer** (Shift+F7).
2. Confirm designer shows the moth / VESPER masthead, + Add download, Settings, search, **Show** and **Sort**, six-column download grid and a 30px footer. It must NOT be an empty 800×450 form.
3. Check that the seven **sample** records are present on design surface. If Visual Studio doesn't render owner-drawn rows, the controls and columns must still be designer-visible; runtime is the definitive visual test.
4. Move/resize a control in the designer and save. Reopen designer to confirm no duplicate columns or controls are created.
5. Compare **running form** at 1222×850 and 830×520 (reference images are in the provided handoff, `STABLE_v8.3/`). Keep Windows native title bar.
6. Confirm responsive toolbar labels and search field do not overlap, and grid has no horizontal scrollbar.

## Settings
1. Open Settings > Appearance at default and narrow supported widths.
2. Verify the Motion explanation is below the selector, not in the same line.
3. Verify **no horizontal scrollbar** and no text spilling past the right pane at 100% and 125% scaling.
4. Verify Downloads and Transfers settings pages, Save, Cancel, and theme/density changes.
5. Verify page navigation does not accumulate GDI fonts or move content horizontally.

## Other visual and behavior checks
- Default light v8.3 matches the handoff; darker v8.3 colors work; v9 accents stay opt-in.
- Check the 34/46/58px density choices; at Extra Compact filename subtitle and list ETA are absent.
- Check checkbox-only selection, context menu, Add Download dialog and Details dialog (all demo behavior).
- No real disk mutation or download activity. Delete file remains disabled.
- Measure actual idle CPU, handle count and memory on Windows before making performance claims.

## Environment caveat
Generated here without .NET SDK / Windows UI runtime. **A passing static source scan does not mean the project builds or Visual Studio Designer loads.**
