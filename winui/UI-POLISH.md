# Twinkle Tray Native: UI layout and verification

## 0.3.2 system appearance and entrance

- The flyout retains a native border without a title bar so DWM can supply its normal border, shadow and rounding. The previous XAML-drawn rectangular outline is removed. The native frame follows light/dark content; the system chooses its border color.
- System mode resolves the Windows app theme and listens for `UISettings.ColorValuesChanged`; explicit Light/Dark overrides remain available. Standard WinUI controls retain the system accent palette. Desktop Acrylic remains the transient backdrop, with the OS controlling transparency and accessibility fallbacks.
- Initial monitor discovery completes before the first startup panel. Opening lays out a DWM-cloaked window, waits for stable arranged bounds, and reveals it once. Activation is requested within the initiating call and keyboard focus waits until preparation completes. A cancelled opening cannot reveal itself later. Generic DWM transitions are disabled to avoid combining two motion effects.
- A 167 ms Fast Out / Slow In content entrance moves 8 DIP and fades in. It is omitted when Windows disables animations or high contrast is active. It does not scale or stretch the native window and does not claim to reproduce a private Windows Shell animation.
- Refresh leaves monitor-name opacity intact and reserves space for its progress indicator. Native controls still show their disabled state.

The 0.3.2 Windows x64 run passed **54 core tests, 11 settings pages, 44 runtime assertions, 144 layout cases, 59 interaction/popup checks and 11 IPC groups**, with 14 previews and zero hardware/user-settings writes. It completed at **2026-09-29 11:48:09.5517957 UTC**; evidence is in `artifacts/test-results/20260929T114458986-058f34149d9940bb9298062a9099fe10/`. Native checks passed for border/theme attributes, actual cloak release and stable bounds, animation completion, cancellation, system accent retention and Acrylic selection. The native probe gained foreground at 8ms and the flyout was observed hidden at 42ms. The ZIP and unsigned MSIX contain the exact tested executable and three managed DLLs, checked by SHA-256.

XAML preview PNGs capture content only: DWM borders, shadows, desktop Acrylic and motion require separate visual observation. Changing the actual Windows theme, accent or animation preference is outside this isolated run. Representative dark two-display, linked and light multi-display content previews were reviewed; this is not an assessment of animation smoothness or live system-frame appearance.

Implementation guidance: [Windows theming](https://learn.microsoft.com/en-us/windows/apps/develop/ui/theming), [Acrylic for transient surfaces](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic), [Fluent timing](https://learn.microsoft.com/en-us/windows/apps/design/motion/timing-and-easing), [DWM attributes](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute).

On a shared desktop Windows may deny a background test process's foreground request. An unchanged 0.3.1 binary and the initial 0.3.2 runs both reproduced that denial with the same external foreground window. For an interactive rerun, set `TWINKLETRAY_SMOKE_INTERACTIVE=1` in the test process environment, run `test-app.ps1`, and click **Start focus checks** in the uniquely titled **Twinkle Tray Native — focus test ready** window when it appears. The optional gate times out after 60 seconds and exists only in isolated smoke mode. The foreground/dismissal assertions remain unchanged and must still observe actual native focus. No foreground-lock setting or input-queue workaround is used. The gate remains open during the checks and is closed in cleanup.

Twinkle Tray Native retains the upstream navigation order, monitor controls and settings format. Earlier runs below remain historical evidence. Local reports are distinct from the version-specific results in [GitHub Actions](https://github.com/BK927/twinkle-tray-native/actions) and packages in [releases](https://github.com/BK927/twinkle-tray-native/releases).

## Layout and interaction

- Shared system-theme styles use 14 DIP body text, 13 DIP secondary text, 28 DIP page headings and 32 DIP toolbar buttons. Theme and high-contrast brushes retain the Windows accent and contrast choices; no fonts are installed.
- The tray remains 360 DIP wide. Its rendered content determines the height, bounded by the display work area. Long names have tooltips, large device lists scroll, and routine value refreshes retain existing slider controls.
- Windows 11 style places its toolbar below the monitor controls; Windows 10 style keeps the toolbar above them. The flyout forms one coherent surface without a separate footer fill or hard divider. Brightness numbers use 14 DIP native bordered TextBoxes beside their sliders, and power is available from More. Linked mode shows one brightness slider, while additional monitor features use a compact layout. Unlinking restores the individual controls.
- Settings begin at 1040×740 DIP. Below 960 DIP the navigation becomes compact; below 600 DIP of row space, controls move under their labels. Related settings have section headings, and monitor, schedule and profile editors have collapsible summary cards.
- Rebuilding a settings page preserves expansion, scroll position, focused editor and uncommitted text. Invalid field values have nearby error messages and do not replace the last valid saved value.
- The brightness overlay shares the panel's visual conventions and bounds its content to the display work area. Unsupported backdrops and high contrast use a solid background.

## 0.3.1 lifecycle and input changes

- Demo substitutes simulated display hardware and keeps settings isolated, while using the production flyout lifecycle: outside interaction dismisses it and it stays out of Alt+Tab. Only the isolated smoke harness may temporarily hold a window open to measure controls; ordinary demo mode does not bypass dismissal.
- Positioning uses the actual notification-area icon bounds, including keyboard activation. The panel restores an available control's focus, and Escape dismisses it and returns focus to the tray.
- A pointer-gesture token identifies a click that both dismisses the panel and activates its tray icon. The activation from that same click is consumed. A new click is independent; there is no fixed 350 ms suppression interval.
- Inside the panel, wheel brightness adjustment belongs to sliders. Labels, numeric editors and selection controls do not propagate wheel input into brightness changes. Partial deltas accumulate per target until a full **120-unit detent** is available, preserving high-resolution input and direction changes.
- Slider dragging sends updates throughout the gesture at a bounded rate, instead of waiting solely for release. The native UI test supplied continuing slider inputs and observed an update before the sequence ended, followed by the final value.
- Settings editor restoration waits for card animations and stable content/viewport geometry before releasing the focus-scroll guard. The saved 160 DIP scroll position returned to exactly 160 DIP, with the selected text and focus retained. Closed windows also ignore late theme/backdrop and queued layout work.

## Local 0.3.1 result

The Windows x64 run passed **54 core tests, 11 settings pages, 44 simulated runtime assertions (including 20 automation regressions), 144 layout cases, 53 interaction/popup checks and 11 IPC groups**. It generated **14 preview PNGs**. The UI report has no errors, zero hardware writes and zero user-settings writes.

<!-- 0.3.1 RELEASE EVIDENCE: update this paragraph and count references together when rerunning verification. -->
The complete run finished at **2026-09-29 11:05:01.7682965 UTC**. Evidence is in `artifacts/test-results/20260929T110241471-1a85b9a69aa44967b05d8f227bbac56e/{smoke-test,ui-layout-test,integration-test}.json`; all three reports have `Passed: true`.

The 53 UI checks include the retained settings/layout/numeric-control checks, continuous slider updates, gesture-state cases and native flyout lifecycle checks. Their input sources are deliberately distinguished:

| Coverage | Method and observed result |
| --- | --- |
| Dismissal when another window activates | A real owned probe window used `Window.Activate`. Its foreground activation was observed at 9ms, before the subsequent explicit foreground request; the panel hid within 109ms. Actual panel deactivation and probe activation events were recorded. No external mouse click or synthetic deactivation event was used. |
| More popup focus and dismissal | A real `MenuFlyout` opened, received keyboard focus and closed while its parent remained open. No menu command or hardware action was invoked. |
| Gesture suppression and immediate reopening | Pure token-state cases and simulated requests through production methods verified release-before-activation, consuming the same click once and allowing unrelated/new activations. These are not physical tray-click tests. |
| Keyboard opening and dismissal | Actual XAML focus selected the first enabled control and survived refresh/reopening. The production keyboard-dismiss method hid the panel and discarded an unsaved draft; physical Escape was not injected. Cyclic navigation and switcher exclusion were configuration checks, not a physical Tab/Alt+Tab sequence. |
| Icon-anchor placement on another display | A synthetic anchor selected a different display from the cursor among two available displays. The actual native window matched that display and fit its work area at 450×303 pixels. The cursor was not moved; this does not test physical notification-icon activation. |
| Wheel and dragging | The 54-test core suite covers 120-unit wheel-delta accumulation. Continuing values through the native slider path produced throttled updates before the gesture ended. Physical pointer dragging and end-to-end wheel routing were not injected in this run. |

Computer Use could not find the production-equivalent demo tool window as a target after two inventory checks, so separate pointer/physical-key verification was not performed. **Physical tray clicks, Escape and end-to-end wheel routing remain unverified.** The old 0.3.0 name-row wheel check below does not validate the new slider-only routing.

Representative new previews of two displays, linked mode, Windows 10 style and six displays in the light theme were visually reviewed; no additional clipping was observed in those captures. The ZIP/MSIX package executable and three managed assemblies also matched the verified build's SHA-256 values.

All 14 previews report an observed host rasterization scale of **1.5 (150%)**. The requested 100/125/150/200% outputs are relative bitmap scales, not separate Windows DPI sessions. Actual high contrast was off. Native DPI changes, high contrast and an installed upstream live-panel comparison remain unverified; the upstream comparison is source-based.

## Historical local 0.3.0 result

The Windows x64 run passed **46 core tests, 11 settings pages, 44 simulated runtime assertions (including 20 automation regressions), 144 layout cases, 36 interaction/popup checks and 11 IPC groups**. It generated **14 preview PNGs**. The UI report records zero hardware writes, zero user-settings writes and no errors.

<!-- 0.3.0 RELEASE EVIDENCE: update this paragraph and count references together when rerunning verification. -->
The complete run finished at **2026-09-29 09:31:42 UTC**. Evidence is in `artifacts/test-results/20260929T092859437-f5d0177dedf34b74bdce2455a08d9499/{smoke-test,ui-layout-test,integration-test}.json`; all three reports have `Passed: true`.

The 36 UI checks retain the six settings-editing checks and 18 tray/OSD layout checks from the earlier run, then add twelve tray checks: Windows 11/10 toolbar placement, numeric-field placement, per-monitor numeric editing, invalid-input recovery, range limits, draft/selection retention during value refresh, write suppression during refresh, linked editing, unlinking, compact-feature numeric editing isolated to its VCP control, and a pending input-source selection blocked while refresh is active. The 144 settings layout cases retain the same language/theme/viewport and empty/error matrix described below.

Representative new captures were visually reviewed for the Windows 11 bottom toolbar, values beside sliders, the single linked slider and compact extra controls. The upstream comparison used its source; **an installed upstream panel was not inspected live**. No pixel-for-pixel or comprehensive glyph-quality claim follows from this review.

A separate 0.3.0 interactive demo check confirmed wheel input over the slider (72→67) and monitor-name row (67→72), numeric entry of 63 with Enter while the other display stayed at 48, and F5 refresh preserving the values. These observations used simulated displays and did not write real monitor hardware. Row-wide wheel handling is historical behavior and is intentionally replaced by slider-only handling in 0.3.1.

Every 0.3.0 preview reports an observed host rasterization scale of **1.5 (150%)**. Requested 100/125/150/200% outputs are relative bitmap scales, not separate Windows DPI sessions. Actual high contrast was off and was not exercised. These results do not establish 0.3.1 environment coverage.

## Historical 0.2.2 result

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

The smoke run uses isolated demo instances and fresh in-memory fixtures. Measurement may temporarily keep the flyout open through a smoke-only seam; lifecycle acceptance must exercise the ordinary dismissal path with that hold released. In addition to runtime and IPC checks, it produces `ui-layout-test.json` for settings layouts, state-preservation checks, input validation and tray/overlay layouts. Preview PNGs are kept under `test-fixtures/ui-previews/`; neither these fixtures nor test reports are shipped in the portable ZIP or MSIX.

The layout matrix covers all eleven settings pages, Korean and English, light and dark themes, and 640×480, 1040×740 and 1440×900 DIP viewports. Populated fixtures include long names and multiple monitors; a separate empty-state pass verifies the same pages without devices or saved entries.

Representative renderings request `RenderTargetBitmap` outputs at 100%, 125%, 150% and 200% relative to the current host render scale. These are **render-scale simulations**, not native Windows sessions at those DPI settings. Each preview records its DIP dimensions, actual pixel dimensions, host rasterization scale and effective pixels per DIP. The report also records whether Windows high contrast was active. Bounds checks verify arranged geometry and scroll access; they do not establish glyph quality or complete screen-reader compatibility. PNGs also require visual review.

The isolated smoke run has a 240-second timeout. A `progress.json` file beside its preview images records the current stage so a slow run can be distinguished from a stalled one. Popup checks compare actual native window bounds with the work area, compare arranged XAML against the actual client size, and move the real scroll viewport when content overflows.

## Compatibility

The UI changes do not alter brightness scheduling, monitor write APIs, CLI/UDP interfaces or the saved settings schema. Existing settings load without migration. Device-specific results from 0.2.1 remain in [VERIFICATION.md](VERIFICATION.md); UI tests do not repeat real monitor writes. Actual DPI switching, a Windows high-contrast session and Windows 10 require separate acceptance. The supported release target is Windows x64; macOS, Apple hardware and Windows ARM64 execution are outside the current support scope.
