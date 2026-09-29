# UI polish · 0.2.2

The native port retains the upstream navigation order, monitor controls and settings format. This release focuses on consistent Windows styling, smaller-window layouts and editing continuity.

## Layout and interaction

- Shared system-theme styles use 14 DIP body text, 13 DIP secondary text, 28 DIP page headings and 32 DIP toolbar buttons. Theme and high-contrast brushes retain the Windows accent and contrast choices; no fonts are installed.
- The tray remains 360 DIP wide. Its rendered content determines the height, bounded by the display work area. Long names have tooltips, large device lists scroll, and routine value refreshes retain existing slider controls.
- Settings begin at 1040×740 DIP. Below 960 DIP the navigation becomes compact; below 600 DIP of row space, controls move under their labels. Related settings have section headings, and monitor, schedule and profile editors have collapsible summary cards.
- Rebuilding a settings page preserves expansion, scroll position, focused editor and uncommitted text. Invalid field values have nearby error messages and do not replace the last valid saved value.
- The brightness overlay shares the panel's visual conventions and bounds its content to the display work area. Unsupported backdrops and high contrast use a solid background.

## Verified 0.2.2 result

The Windows x64 native run passed **11 settings pages and 41 simulated runtime assertions**, including 20 automation regressions. The UI report passed **144 layout cases and 24 interaction/popup checks**, and generated **11 preview PNGs**. The independent core suite also passed **46/46 tests**, and the final integration report passed **11 IPC test groups**. These checks used isolated fixtures; UI hardware writes and user-settings writes were both zero.

<!-- 0.2.2 RELEASE EVIDENCE: update this paragraph and count references together when rerunning verification. -->
The complete run finished at **2026-09-29 08:23:37 UTC**. Evidence is in `artifacts/test-results/20260929T082103256-2b6a96d7ea6449edb7cd5dd7ae451a36/{smoke-test,ui-layout-test,integration-test}.json`; all three reports have `Passed: true`, and the UI report contains no errors.

The 144 layout cases comprise 132 populated page/language/theme/viewport combinations, 11 empty pages and a visible inline-validation error. The 24 checks comprise six editing/validation checks and 18 tray/overlay checks: light/dark × 1/6/12 displays × popup bounds/scrolling, retained tray controls and OSD bounds/scrolling. The editing cases verify that invalid input cannot update settings, correction commits once, expansion survives refresh, page navigation preserves focus/selection/scroll, and both single and immediate consecutive refreshes retain the editor state and unsaved draft.

The observed Windows host rasterization scale was **1.5 (150%)** throughout. Requested preview scales of **100%, 125%, 150% and 200%** produced approximately **1.5, 1.875, 2.25 and 3 pixels per DIP**, respectively; pixel rounding is recorded per image. These are relative bitmap renderings, **not four Windows DPI sessions**. High contrast was observed as off and was not changed. Heading/window foreground-background observations cover Korean/English light/dark themes; they are not a complete accessibility audit.

## Repeatable verification

Run the existing native integration command after publishing:

```powershell
./winui/test-app.ps1 -AppPath ./winui/artifacts/win-x64/TwinkleTray.WinUI.exe
```

The smoke run uses isolated demo instances and fresh in-memory fixtures. In addition to runtime and IPC checks, it produces `ui-layout-test.json` for settings layouts, state-preservation checks, input validation and tray/overlay layouts. Preview PNGs are kept under `test-fixtures/ui-previews/`; neither these fixtures nor test reports are shipped in the portable ZIP or MSIX.

The layout matrix covers all eleven settings pages, Korean and English, light and dark themes, and 640×480, 1040×740 and 1440×900 DIP viewports. Populated fixtures include long names and multiple monitors; a separate empty-state pass verifies the same pages without devices or saved entries.

Representative renderings request `RenderTargetBitmap` outputs at 100%, 125%, 150% and 200% relative to the current host render scale. These are **render-scale simulations**, not native Windows sessions at those DPI settings. Each preview records its DIP dimensions, actual pixel dimensions, host rasterization scale and effective pixels per DIP. The report also records whether Windows high contrast was active. Bounds checks verify arranged geometry and scroll access; they do not establish glyph quality or complete screen-reader compatibility. PNGs also require visual review.

The isolated smoke run has a 180-second timeout. A `progress.json` file beside its preview images records the current stage so a slow run can be distinguished from a stalled one. Popup checks compare actual native window bounds with the work area, compare arranged XAML against the actual client size, and move the real scroll viewport when content overflows.

## Compatibility

The UI changes do not alter brightness scheduling, monitor write APIs, CLI/UDP interfaces or the saved settings schema. Existing settings load without migration. Device-specific results from 0.2.1 remain in [VERIFICATION.md](VERIFICATION.md); UI tests do not repeat real monitor writes. Actual DPI switching, a Windows high-contrast session, Windows 10, Apple displays and ARM64 execution remain separate acceptance environments.
