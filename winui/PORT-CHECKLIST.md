# WinUI 3 port 0.2.2: source audit and validation checklist

This is a feature audit, not a claim that every monitor or Windows configuration has been tested. The reference is upstream commit [`e3d5bb0bde75f7ef1a6ae450b314090c804e0f8e`](https://github.com/xanderfrangos/twinkle-tray/tree/e3d5bb0bde75f7ef1a6ae450b314090c804e0f8e). Electron sources are preserved alongside the native implementation.

**한국어:** 원본의 사용자 기능을 기준으로 이식 범위와 검증 범위를 구분했습니다. `Implemented`는 코드가 있다는 뜻이며 실제 장치 검증을 뜻하지 않습니다. 아래 차이점과 미검증 항목이 남아 있으므로 픽셀 단위 동일성이나 모든 하드웨어에서의 완전한 동작을 보장하는 표가 아닙니다.

**Verification scope:** The **0.2.2** Windows x64 core, native GUI, UI and IPC results are recorded below. Physical-device results and the linked x64/ARM64 CI build remain explicitly identified as the **0.2.1 historical baseline**. The 0.2.2 UI run did not repeat hardware writes or change Windows DPI/high-contrast settings.

## 0.2.2 UI changes

The existing navigation and settings schema are retained. Settings rows adapt to narrower windows, related options have section headings, and monitor/schedule/profile editors use collapsible summary cards. Page rebuilds preserve expansion, scrolling, focused text and selection; inline field validation retains the last valid saved value. The tray and overlay use content-based sizing, shared system-theme styles and solid backgrounds when backdrops are unavailable or high contrast is active. Korean labels and key names were improved. **144 actual arranged-layout cases and 24 interaction/popup checks passed** in the 0.2.2 native x64 app. See [UI-POLISH.md](UI-POLISH.md) for the evidence, repeatable checks and limits.

Status meanings:

- **Verified core**: automated tests cover the stated platform-independent behavior.
- **Implemented**: native runtime/UI code exists; device-specific behavior still needs the indicated checks.
- **Partial / difference**: an intentional platform change or a known compatibility gap is described explicitly.
- **Pending validation**: do not infer a passed test from the presence of a smoke-test case.

## Source inventory

The audit examined these upstream entry points rather than only the README feature list:

| Source | Scope |
| --- | --- |
| [`src/electron.js`](../src/electron.js) | Default settings, tray, hotkeys, gamma/SDR routing, app profiles, idle and resume, schedules, IPC/UDP, updates and diagnostics |
| [`src/components/SettingsWindow.jsx`](../src/components/SettingsWindow.jsx) | General, time, monitor, feature, shortcut, profile, light-sensor, update and debug controls |
| [`src/components/BrightnessPanel.jsx`](../src/components/BrightnessPanel.jsx), [`Slider.jsx`](../src/components/Slider.jsx) | Tray flyout, monitor sliders, linked levels, monitor-feature controls |
| [`src/components/MonitorFeatures.jsx`](../src/components/MonitorFeatures.jsx) | Brightness VCP overrides, contrast, volume, power, inputs and custom MCCS controls |
| [`src/Utils.js`](../src/Utils.js) | CLI grammar, legacy schedule conversion and piecewise calibration |
| [`src/light-sensor/light-sensor.js`](../src/light-sensor/light-sensor.js) | Sensor providers, lux thresholds and monitor curves |
| [`src/modules`](../src/modules) and [`src/monitor-rules.json`](../src/monitor-rules.json) | Windows monitor APIs, HDR/gamma algorithms, Apple protocol and model overrides |
| [`src/localization`](../src/localization) | Existing translations reused by the native application |

## Appearance and everyday controls

| User feature | Native implementation | Status / verification boundary |
| --- | --- | --- |
| Notification-area app, click-to-open flyout, outside-click dismissal | `TrayService`, `MainWindow` | Implemented; 11 settings pages passed 0.2.2 startup smoke. Light/dark flyout and OSD work-area bounds, real scrolling and persistent slider controls passed with 1/6/12 simulated displays. Historical 0.2.1 direct initial `--demo --settings` display also passed after the root-loaded sizing fix. Outside-click behavior is not part of the new automated layout matrix. |
| Per-display brightness, percentage, name and monitor icon | `MainWindow`, `BrightnessControl` | Implemented. Native controls preserve the original arrangement; typography, animation and spacing are WinUI behavior. |
| Linked monitor sliders; independent levels | `AppController.TrySetAsync`, linked settings | Implemented; simulated set-all/per-monitor offset produced the expected 60/65 levels, and linked-hotkey runtime regression passed. |
| Monitor name, order and hidden displays | `MonitorSettings`, monitor settings page | Implemented. Native stable IDs can differ from Electron IDs. |
| Brightness minimum/maximum and multipoint calibration | `BrightnessCalibration` | Verified core: endpoint mapping, inverse, duplicate points, plateaus and clamping. |
| Slider name/value visibility and glyph | `MonitorSettings`, `MainWindow` | Implemented native customization; these are additions, not claimed imports of upstream settings. |
| Light/dark/system theme; Windows 10/11 appearance; acrylic | `MainWindow`, settings UI, `OverlayWindow` | Implemented approximation. Korean/English light/dark layout combinations passed in 0.2.2; observed heading/background colors were checked. Actual high contrast and Windows 10 remain unverified. Electron CSS and its native-animation settings are not reproduced byte-for-byte. |
| Original tray icon choices and system-theme updates | `TrayService`, original icon assets | Implemented. Windows notification area controls icon placement/visibility. |
| Tray mouse-wheel and slider wheel controls, inversion, step size | `TrayService`, `MainWindow` | Implemented; low-level wheel-hook behavior needs an interactive desktop. |
| Dedicated brightness OSD, timeout, safe/aggressive policy, per-profile suppression | `OverlayWindow`, automation controller | Implemented as a separate WinUI window. It is not a pixel-identical copy of the Electron OSD. Exclusive fullscreen behavior remains Windows/application-dependent. |
| Startup registration, background mode, Explorer tray recreation | `StartupService`, `Program`, `TrayService` | Implemented. Startup enable/disable and packaged startup need explicit environment validation. |
| Languages | `LocalizationService`, copied upstream JSON | Existing translations reused; new native labels have English fallbacks and Korean additions. New labels are not fully translated into every upstream language. |

## Hardware and DDC/CI features

| User feature | Native implementation | Status / verification boundary |
| --- | --- | --- |
| External display brightness/contrast | `MonitorService`, Windows physical-monitor APIs | Real writes, readback, peer isolation and exact restoration passed under Windows 11 x64: LG ULTRAGEAR+ brightness 100→98→100 and contrast 70→72→70; AOC Q32V3WG5 brightness 100→98→100 and contrast 50→52→50. Three fresh processes each recognized both displays through DDC/CI on the first query after the initial-handle retry fix. This does not establish behavior on other models. |
| Internal panel brightness | WMI provider, supported-level snapping | Implemented; requires a laptop/panel test. |
| High-level Windows brightness/contrast fallback | `MonitorService` | Implemented; driver-specific fallback unverified. |
| Monitor model brightness VCP exceptions and user override | `BrightnessVcpFor`, `BrightnessVcp` | Implemented; preserves upstream FUS087C/FUS06AB rules. Actual override writes need compatible hardware. |
| MCCS capabilities and contrast, volume, mute, inputs, power, color/custom codes | `VcpCapabilities`, feature page, `SetVcpAsync` | Parser/read path implemented; live capabilities and feature reads succeeded on the development displays. Input/power/color changes were not sent. |
| Feature limits, linked feature levels and custom labels | `FeatureSettings`, feature page and brightness routing | Implemented; linked scaling verified in core, runtime writes remain device-dependent. |
| Custom glyph, text and image indicators for DDC features | `FeatureSettings`, importer and flyout | Implemented; migration preserves upstream hex glyphs, indicator text and `iconPath`. Image rendering requires an existing absolute local image path. Upstream's visible indicator selector offers glyph/text; native image selection is an extension. |
| Hardware power-off, Windows display-off signal, both, or disabled | `PowerOffMode`, `PowerOffValue`, tray/controller | Implemented; firmware wake and multi-monitor power behavior unverified. |
| HDR detection and SDR white-level control | `DisplayColor`, `BrightnessControl` | Implemented from upstream Windows behavior. Read-only HDR mode/capability observation passed; actual SDR-white-level writes unverified. |
| Force-HDR override and SDR as main slider | `ForceHdr`, `MainControl=sdr` | Implemented; does not falsely change detected HDR status. Requires target-device test. |
| Gamma as main control / software fallback | `GammaController`, explicit gamma routing | Implemented; separate hardware and gamma capabilities. Gamma curve and mapping tested. Live 98% gamma requests passed separately on LG ULTRAGEAR+ and AOC Q32V3WG5 under Windows 11 x64, with all 768 original ramp values restored exactly and the peer display's ramp unchanged. |
| Extend minimum below physical backlight range | `GetExtendedLevels` / `FromExtendedLevels` | Verified core handoff between backlight and the upstream 20% gamma floor. Requires working gamma support and runtime hardware validation. |
| Preserve/restore existing gamma calibration on shutdown, route removal or hotplug | `GammaController`, monitor-service lifecycle | Implemented; restoration avoids a changed external ramp or a reassigned display. Controlled live restoration passed on the two displays above; automatic restoration during shutdown, route removal and hotplug remains unverified on physical devices. |
| Apple Studio Display brightness | Native HID implementation | Implemented protocol and ID matching. Needs an Apple display exposing the Windows HID interface; no driver replacement was performed. Upstream libusb/WinUSB-only configurations are not equivalent. |
| Hotplug, wake, last-known brightness, per-monitor skip, refresh delay, polling | Tray power/display events, persisted `LastBrightness`, controller | Implemented, including upstream's DEL41D9 automatic-restore exclusion. Real hotplug/resume/lock/lid sequences require interactive device tests. |
| Hide internal display while lid is closed | Lid notification and `HideClosedLid` | Implemented; requires a laptop test. |
| Driver/provider switches and VCP read delay | `HardwareOptions`, advanced settings | WMI, DDC and Apple switches plus delay implemented. See debug-specific differences below. |

## Automation and control interfaces

| User feature | Native implementation | Status / verification boundary |
| --- | --- | --- |
| Fixed-time schedule; per-monitor levels; startup evaluation | `ScheduleEvaluator`, controller | Verified core boundaries, invalid/disabled entries, midnight, long pause and per-monitor targeting. Runtime scheduling uses local wall-clock time. |
| Sunrise/sunset and other solar events, coordinates and offsets | `SolarCalculator`, schedule UI | Verified against all 14 upstream SunCalc 1.9 reference events within one second, plus polar/no-event and timezone cases. |
| Continuous daily interpolation | `ScheduleInterpolation`, evaluator | Verified across midnight and independent monitor timelines. |
| Smooth changes | `SmoothTransitions`, `TransitionSeconds` | Implemented. **Difference:** duration in seconds replaces Electron's brightness-dependent step-rate presets; importer reports the approximation. |
| Idle dim and restore; seconds/minutes; fullscreen/media exemptions | `DesktopEnvironment`, automation controller | Implemented; simulated manual change during delayed restoration and pause passed. Real idle/media/lock behavior still requires an interactive device test. |
| Automatic-control pause, lock suppression and disable-auto-apply | Controller priority/state handling | Implemented; simulated schedule/profile transition resumption after pause/lock, immediate pause/resume, schedule-to-idle restoration and manual brightness/VCP/gamma/profile/UseTime overrides passed. Real Windows lock/unlock, foreground changes and missed-schedule catch-up still need event-driven acceptance tests. |
| Multiple actions per hotkey: set, offset, cycle, power, profile, refresh, panel, VCP | `HotkeyAction`, `RegisterHotKey`, controller | Implemented; parser/import tests pass. Normal Win32 accelerator conflicts are reported; malformed action lists are retained disabled rather than becoming brightness commands. |
| Dedicated laptop brightness keys | `NativeKey`, tray raw-HID Consumer Control input | Implemented separately from `RegisterHotKey`, preserving BrightnessUp/Down on import. Internal-panel brightness offsets are skipped because Windows already handles them. Requires a compatible keyboard/laptop input test. |
| Linked-level behavior for hotkeys | `HotkeysBreakLinkedLevels`, controller | Implemented; linked-level runtime regression passed with simulated displays. |
| Cycle hotkeys | Controller cycle index | Implemented with upstream's initial advance before applying the first invocation; the first listed value follows when the cycle wraps. |
| Application profiles, comma-separated executable fragments, menu visibility, restore and OSD policy | `ProfileResolver`, profile UI/controller | Verified core matching: case-insensitive substring, last enabled match wins, blank fragments ignored. Focus/restore behavior needs interactive validation. |
| Windows, Yoctopuce and simulated ambient light | `AmbientLightService`, sensor UI, `SensorCurve` | Verified linear lux curve and invalid input handling. Provider implementations exist; no real light sensor was available for end-to-end validation. |
| CLI list, all/number/ID selectors, set, offset, VCP, panel and OSD | `CommandLine`, `CliRunner`, per-user/session named pipe | Eleven simulated integration groups passed, including set/offset, exact-ID isolation, range clamping, recovery after malformed input, invalid selector, help, schedule evaluation, dedicated OSD and settings-window commands. Output is structured JSON rather than upstream's colored text listing; `--settings` is a native addition. |
| CLI `--UseTime`, `--UDP` | Core parser and native transport integration | Core flag and incompatibility tests pass; IPC schedule evaluation passed. UDP listener authentication/malformed-input checks passed; CLI UDP uses normal saved server settings and rejects demo mode. Upstream has no separate UDP host/key CLI flags. |
| Authenticated UDP list/get/getvcp/set/setvcp/checktime/refresh | `UdpControlService`, controller | Implemented; simulated command routing, malformed JSON and wrong-key resilience passed. No claim of exact legacy response-shape compatibility for every property. |
| Multi-instance ownership and IPC isolation | `Program`, current-user/session named pipe | Implemented. Demo uses a separate instance namespace, simulated monitors and fresh in-memory settings. |

## Persistence, import, updates and deployment

| User feature | Native implementation | Status / verification boundary |
| --- | --- | --- |
| Persist settings, validate ranges and retain corrupt data | `SettingsStore`, `SettingsNormalizer` | Verified atomic replacement, round-trip, older/null fields and corrupt-file backup without silently replacing the original. |
| Import Electron settings and explicit monitor identity mapping | `UpstreamSettingsImporter`, advanced UI | Verified schema migration. Unmapped references become inert `unresolved:` IDs and are reported; original JSON is retained. No external user file is modified by the core importer. |
| Names/remaps/features/schedules/hotkeys/profiles/sensors and hardware flags | Importer | Tests cover mappings, zero brightness, legacy AM/PM/idle formats, action targets, source retention and unsupported accelerators. Model and instance aliases can map to the same native display; one source model key cannot currently fan out to multiple identical displays. |
| Previously remembered hardware levels | `ImportKnownDisplays`, native `LastBrightness` | Verified core import of the separately selected known-displays JSON, including zero/SDR levels, unmapped references, conflicting aliases and duplicate destination records. Raw JSON is preserved. Importing settings alone does not search for or read the separate file. |
| Native export/import/reset and pre-import backup | Advanced UI/controller | Implemented; file-picker/reset flows need interactive validation. Export includes the UDP key. |
| Update checking, release channel, release notes | `UpdateService`, updates UI, `SemanticVersion` | Implemented against BK927 WinUI release tags. [SemVer 2.0](https://semver.org/) precedence is tested for beta progression, stable promotion, numeric identifiers and ignored build metadata. Live newer-release flow unverified. |
| Download, checksum verification, staged installation and rollback | `UpdateService` | Implemented for portable installs. Fixture installation file replacement and forced-failure rollback passed. Release selection, download, checksum and extraction passed against a fixture HTTP handler. Live release download, parent-process shutdown and restart together remain unverified; this is not a verified atomic installer. |
| x64/ARM64 portable distribution | Build/publish/package scripts | The 0.2.2 native GUI/UI run passed on Windows x64. Historical 0.2.1 x64 and ARM64 build jobs passed for commit `704dac7` in [GitHub Actions run 36532678766](https://github.com/BK927/twinkle-tray/actions/runs/36532678766). This is not evidence of a 0.2.2 ARM64 build or any ARM64 hardware execution. |
| MSIX package and startup declaration | `package.ps1`, `StartupService` | Packaging support exists and an unsigned fixture passed package validation. Signing, trusted installation, packaged startup and deployment-channel update behavior need separate validation. This fork does not replace the upstream Microsoft Store identity. |
| Diagnostics, settings dump and logs | Advanced settings, read-only hardware probe | Implemented. Diagnostics should be reviewed before sharing because IDs, paths or settings can identify a local configuration. |

## Explicit platform and compatibility differences

- Electron-specific worker/renderer recreation, WMIC subprocess selection, Electron event-source switches, devtools console, GPU process preference and browser animation toggles are not one-to-one native settings. Unknown imported fields remain in `ImportedUpstreamJson` and the import report instead of being silently presented as migrated.
- Fine-grained debug switches such as disabling only high-level brightness, disabling only HDR detection, forcing the old accurate/fast DDC worker, manually overriding taskbar gap/edge, disabling throttling or individual upstream event-source strategies do not all have equivalent native switches.
- Windows monitor IDs and Electron model/instance IDs differ. Identity migration must be reviewed, especially for several displays of the same model or a display moved to a different port.
- Native settings defaults intentionally do not auto-enable login registration, remote UDP or hardware writes merely by opening a demo or importing a file. Import does not change login registration automatically.
- The 0.2.2 layout matrix exercises three DIP viewport sizes at an observed host scale of 1.5. Relative PNG capture scales do not exercise actual DPI switching. Safe OSD/fullscreen handling, third-party taskbar behavior, portrait displays, complete keyboard navigation, screen readers and an actual high-contrast session still require separate acceptance testing.
- Upstream analytics, its Store identity and its release installer are not reused. The native port has its own release/update channel.

## Validation record and release gate

See [VERIFICATION.md](VERIFICATION.md) for the Windows x64 0.2.1 acceptance scope, concrete hardware results and regression fixes.

### 0.2.2 Windows x64 GUI, UI and IPC verification

**46/46 core tests passed again.** The native smoke report passed all **11 settings pages and 41 runtime assertions**, including the existing 20 automation regressions. The separate UI report passed **144 arranged-layout cases and 24 interaction/popup checks**, and generated **11 preview PNGs**. The final integration report passed **11 IPC test groups**. Hardware writes and user-settings writes were both zero in the isolated UI fixtures.

<!-- 0.2.2 RELEASE EVIDENCE: update this paragraph and count references together when rerunning verification. -->
The complete run finished at **2026-09-29 08:23:37 UTC**. Evidence: `artifacts/test-results/20260929T082103256-2b6a96d7ea6449edb7cd5dd7ae451a36/{smoke-test,ui-layout-test,integration-test}.json`, each with `Passed: true`. The UI report contains no errors; the integration report records zero hardware writes.

- [x] All 11 settings pages × Korean/English × light/dark × 640×480, 1040×740 and 1440×900 DIP: 132 populated combinations, plus 11 empty pages and one visible inline-error layout.
- [x] Invalid URL input leaves the last valid setting unchanged and shows an inline error; valid correction commits once.
- [x] Stable profile-card expansion, page-navigation focus/selection/scroll, active editor selection after monitor refresh, and unsaved drafts across two immediate rebuilds.
- [x] Light/dark tray and OSD work-area/client bounds and actual scrolling with 1/6/12 simulated displays; routine value refresh keeps the tray control tree.
- [x] Eleven demo IPC groups covering command delivery, selectors, logical brightness/bounds, malformed-input recovery, help, schedule evaluation, OSD and settings-window commands.
- [x] Representative PNGs captured and actual dimensions/scale recorded; observed heading/window foreground-background contrast recorded for Korean/English light/dark.
- [ ] Separate native Windows 100/125/150/200% DPI sessions or cross-monitor DPI transitions. The host was **150% (1.5)**; capture requests of **100/125/150/200%** are relative to that scale, yielding approximately **1.5/1.875/2.25/3 pixels per DIP**.
- [ ] Actual Windows high-contrast session, complete keyboard/screen-reader accessibility and comprehensive glyph-raster quality acceptance. The report observed high contrast as off; it did not switch modes.

See [UI-POLISH.md](UI-POLISH.md) for how the native controls are exercised and the remaining environment limits.

### Historical 0.2.1 baseline

<!-- RELEASE EVIDENCE: update this paragraph and count references together when rerunning verification. -->
For the historical 0.2.1 audit, **46/46 core tests passed** on .NET 10. They cover the pure algorithms, schema, settings/known-display import, semantic versions and CLI, without reading user settings or writing monitor hardware. The expanded simulated run completed at **2026-09-29 06:41:02 UTC** and passed **11 settings pages, 41 runtime assertions (including 20 automation regressions) and 11 IPC test groups**, with two simulated displays and zero hardware writes. Evidence is in `artifacts/test-results/20260929T064038744-0407eee76f434b2fb704e08b9b3a46f6/{smoke-test,integration-test,automation-regression}.json`; the integration report records SHA-256 hashes for the executable and WinUI/Core/Hardware assemblies. Runtime assertions include fixture update file replacement and rollback after an induced failure. An unsigned MSIX fixture passed package validation; signed installation and the full release-download/process-restart update flow were not tested. See [PACKAGING.md](PACKAGING.md) for signing and MSIX revision details.

The historical 0.2.1 physical-device coverage is limited to the two external displays above on Windows 11 x64. DDC brightness/contrast readback and restoration, controlled gamma writes/restoration and peer isolation passed. No laptop, Apple display or ARM64 device was exercised. Power/input switching, SDR-white-level writes and automatic gamma restoration across device lifecycle events remain outside the verified scope.

Historical 0.2.1 acceptance record (checked items are not 0.2.2 results):

- [x] The 0.2.1 x64 Release publish passed with zero warnings/errors; runtime execution was tested on Windows x64.
- [x] The 0.2.1 x64 and ARM64 CI build jobs passed for `704dac7` in [run 36532678766](https://github.com/BK927/twinkle-tray/actions/runs/36532678766); this does not establish ARM64 runtime behavior.
- [x] Expanded `--smoke-test`: all 11 native settings pages and all 41 runtime assertions, including 20 automation regressions, passed in a newly written report.
- [x] Eleven demo IPC groups, plus UDP command routing/authentication/malformed-input runtime checks; includes selectors, `--UseTime`, logical brightness, bounds, invalid-command recovery and settings-window commands.
- [x] Korean flyout, General/DDC/CI settings, profile editor and sensors at normal size; direct initial settings-window display.
- [ ] Broader English/light-theme/high-contrast/DPI combinations, keyboard and accessibility checks.
- [x] Simulated nonblocking transitions, schedule/profile resumption after pause/lock, immediate pause/resume, schedule-to-idle restoration, immediate profile-restore cancellation, and manual brightness/VCP/gamma/profile/UseTime overrides.
- [ ] Real foreground profile exit, schedule catch-up and lock/unlock interaction under Windows events.
- [x] LG ULTRAGEAR+ and AOC Q32V3WG5 on Windows 11 x64: the DDC brightness/contrast changes listed above, exact readback/restoration and peer isolation.
- [x] Each of those displays: a 98% gamma request, exact restoration of all 768 original ramp values and an unchanged peer ramp.
- [x] Three fresh processes recognized both displays through DDC/CI on the first query after the initial-handle retry fix.
- [ ] Physical-device acceptance for WMI, HDR/SDR writes, Apple HID, sensors, lid, hotplug and wake; gamma automatic restoration on shutdown/route removal/hotplug; ARM64 execution.
- [x] Portable update file-copy and failure rollback against temporary fixture installations.
- [ ] Full live release download/update process, plus signed MSIX install/startup/uninstall on a test installation.

The 0.2.2 results above are separate from this historical baseline. Device-specific and deployment gaps remain open even when the native layout and interaction checks pass.

Unchecked means unverified, not necessarily unimplemented. Keep this document synchronized with the actual release evidence and retain unsupported details in the import report.
