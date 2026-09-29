# Twinkle Tray · WinUI 3

Twinkle Tray의 패널 구성과 동작을 최대한 유지하면서 C#과 WinUI 3로 다시 구현하는 커뮤니티 포크입니다. **현재는 초기 이식 버전이며 원본의 모든 기능을 제공하지 않습니다.** 원본 Electron 소스는 그대로 보관하고 네이티브 앱은 이 `winui/` 폴더에 분리했습니다.

A community C#/WinUI 3 port that preserves Twinkle Tray's panel layout and core behavior. **This is an initial implementation with incomplete upstream feature parity.** The original Electron application remains in the repository; the native application lives under `winui/`.

- Fork: [BK927/twinkle-tray, `winui3` branch](https://github.com/BK927/twinkle-tray/tree/winui3)
- Upstream: [xanderfrangos/twinkle-tray](https://github.com/xanderfrangos/twinkle-tray)
- Upstream baseline: [`e3d5bb0`](https://github.com/xanderfrangos/twinkle-tray/commit/e3d5bb0bde75f7ef1a6ae450b314090c804e0f8e)
- Original assets, translations, attribution, and [MIT license](../LICENSE) are retained.

## 빠른 시작 · Quick start

Windows 10 버전 2004(빌드 19041) 이상 또는 Windows 11이 필요합니다. 빌드에는 **.NET 10 SDK**와 NuGet 패키지를 내려받을 인터넷 연결이 필요합니다. 이 네이티브 앱의 빌드에 Node.js나 Electron은 사용하지 않습니다.

Requires Windows 10 version 2004/build 19041 or later, including Windows 11. To build, install the **.NET 10 SDK** and allow NuGet package restore. Node.js and Electron are not required for the native build.

`winui/global.json` selects a stable .NET 10 SDK with feature-band roll-forward, without changing any installed SDK files.

Run from the repository root in PowerShell:

```powershell
# Run the core tests and publish a Release x64 build.
.\winui\build.ps1

# Preview the panel with simulated monitors; no monitor settings are changed.
.\winui\artifacts\win-x64\TwinkleTray.WinUI.exe --demo

# Start the normal application.
.\winui\artifacts\win-x64\TwinkleTray.WinUI.exe
```

빌드 결과는 `winui/artifacts/win-x64/`에 생성됩니다. 실행 파일, DLL, `Assets`, `Localization`을 포함한 **폴더 전체**를 함께 배포해야 합니다. .NET 및 Windows App SDK 런타임이 포함된 포터블 빌드이며 별도의 MSIX 설치 프로그램은 아직 없습니다.

The complete portable build is in `winui/artifacts/win-x64/`. Keep the **entire folder** together: the executable depends on its accompanying DLLs, `Assets`, and `Localization` files. The publish output includes the .NET and Windows App SDK runtimes. An MSIX installer is not provided yet.

```powershell
# Cross-publish for Windows ARM64. Run this output on an ARM64 device.
.\winui\build.ps1 -Architecture ARM64

# Publish both architectures, running core tests once.
.\winui\build.ps1 -Architecture x64,ARM64
```

ARM64 publishing is configured; ARM64 execution has not been verified on a physical device. For unsupported-display messages, enable DDC/CI in the monitor's on-screen menu and refresh the display list. Available controls depend on the monitor, connection, and driver.

ARM64 빌드 설정은 포함되어 있지만 실제 ARM64 장치 실행은 검증하지 않았습니다. 외부 모니터가 지원되지 않는 것으로 표시되면 모니터 메뉴에서 DDC/CI를 활성화하고 새로 고침을 누르세요. 사용 가능한 기능은 모니터·연결 방식·드라이버에 따라 다릅니다.

## 기능 이식 현황 · Feature parity

“구현 / Implemented”는 코드에 포함되었다는 뜻이며 모든 장치에서 동작을 검증했다는 뜻은 아닙니다.

“Implemented” describes the current code, not a guarantee that every device has been tested.

| Feature / 기능 | Current native implementation / 현재 상태 |
| --- | --- |
| Tray and brightness panel / 트레이·밝기 패널 | Native tray icon, per-monitor sliders, linked levels, wheel adjustment, refresh, power button, and settings shortcut. Upstream layout and icons are reused where practical. |
| Appearance / 외형 | System/light/dark themes, native WinUI controls and acrylic where supported. The layout is inspired by upstream; it is not a pixel-identical reproduction. |
| External brightness / 외부 모니터 밝기 | DDC/CI implementation, device discovery, and normalized 0–100 controls. |
| Built-in display / 내장 화면 | WMI brightness implementation, subject to driver support. |
| Contrast and power / 대비·전원 | Contrast slider on compatible DDC/CI monitors; power-off through VCP `0xD6`. |
| Per-monitor settings / 모니터별 설정 | Custom name, ordering, hiding, minimum/maximum brightness calibration, optional contrast slider. |
| Scheduled adjustments / 시간별 밝기 | Daily local-time entries, per-monitor or all-visible-monitor targets. Upstream's advanced transition behavior is not reproduced. |
| Hotkeys / 전역 단축키 | Brightness up/down and power-off with monitor targets; conflicts are reported. |
| Idle dimming / 유휴 상태 | Dim after a configured idle interval and restore on activity. |
| Startup / 시작 프로그램 | Optional current-user Windows startup entry using `--background`. |
| Languages / 언어 | Reuses upstream translation files, with English fallbacks and Korean text for native-only settings. Some new messages remain English. |
| Command line / 명령줄 | `--List`, `--All`, `--MonitorNum`, `--MonitorID`, `--Set`, `--Offset`, `--VCP`, and `--Panel`; commands reach the running instance through a current-user pipe. |
| Overlay / 화면 표시 | `--Overlay` currently opens the regular panel. A dedicated upstream-style OSD is **not implemented**. |
| Advanced DDC/CI / 고급 DDC/CI | Raw VCP commands are available. The complete upstream advanced-controls UI and device-specific behavior are **not ported**. |
| HDR/SDR, gamma, ambient light / HDR·감마·조도 센서 | **Not ported / 미구현.** |
| App profiles / 앱별 프로필 | **Not ported / 미구현.** |
| Updates, Store/MSIX / 자동 업데이트·스토어 배포 | **Not ported / 미구현.** Current delivery is a portable directory. |
| Upstream settings import / 원본 설정 가져오기 | **Not implemented / 미구현.** Native preferences use a separate file. |

## 명령줄 · Command line

```powershell
$app = '.\winui\artifacts\win-x64\TwinkleTray.WinUI.exe'
& $app --List
& $app --MonitorNum=1 --Set=65
& $app --MonitorID="UID2353" --Offset=-10 --Panel
& $app --All --Set=50
& $app --Panel
& $app --background
& $app --help
```

밝기 또는 VCP 명령에는 모니터 선택자 하나와 동작 하나가 필요합니다. `--MonitorNum`은 1부터 시작하며 `--List`에 표시된 순서를 따릅니다. `--MonitorID`는 전체 ID 또는 하나의 모니터에만 일치하는 부분 문자열을 허용합니다. 애매한 선택이나 충돌하는 옵션은 오류로 처리합니다. 앱이 실행 중이면 해당 인스턴스에 명령을 전달합니다.

A brightness or VCP command requires exactly one selector and one action. Monitor numbers are 1-based and follow `--List` order. IDs accept a full ID or an unambiguous partial match. Ambiguous selectors and conflicting arguments fail explicitly. A running instance receives the command; otherwise a standalone command runs and exits unless a panel is requested.

`--VCP=0x10:50` sends a **raw MCCS value**, not a calibrated brightness percentage. VCP support and value ranges are monitor-specific. The `--Set` and `--Offset` options use the configured brightness calibration instead.

`--VCP=0x10:50`은 보정된 밝기 비율이 아닌 **MCCS 원시 값**을 전송합니다. 일반 밝기 조절에는 모니터별 밝기 범위를 반영하는 `--Set` 또는 `--Offset`을 사용하세요.

## 설정과 문제 확인 · Settings and troubleshooting

- Native preferences: `%APPDATA%\TwinkleTray.WinUI\settings.json`.
- Error log: `%LOCALAPPDATA%\TwinkleTray.WinUI\errors.log`.
- Corrupt JSON is not silently reset: the original remains in place and a timestamped recovery copy is attempted before an error is surfaced.
- Demo mode uses simulated monitors and temporary in-memory preferences. It does not change physical monitor brightness or save native preferences.
- Upstream Electron preferences are neither imported nor overwritten.

설정은 자동 저장됩니다. 손상된 JSON은 조용히 덮어쓰지 않고 원본과 가능한 경우 복구 사본을 보존합니다. 데모 모드는 실제 모니터 밝기나 저장된 설정을 변경하지 않습니다. 기존 Electron 버전과 설정 파일을 공유하지 않습니다.

If restore or XAML compilation reports a path longer than **260 characters**, use a shorter checkout path and a short writable NuGet cache path, then build again. For example, after creating a suitable directory:

```powershell
$env:NUGET_PACKAGES = 'C:\nuget'
.\winui\build.ps1
```

이 환경 변수는 현재 PowerShell 세션에만 적용됩니다. 스크립트는 설치된 .NET SDK를 수정하지 않습니다. 260자 경로 오류가 발생하면 짧고 쓰기 가능한 경로를 사용하세요.

This environment variable applies to the current PowerShell session. The build script never modifies the installed SDK. Use a cache path writable by your account.

## 검증 · Validation

```powershell
# Hardware-independent tests: normalization, settings recovery, schedules, CLI validation.
dotnet run --project .\winui\TwinkleTray.Core.Tests --configuration Release

# Native GUI and IPC integration tests using two simulated displays.
.\winui\test-app.ps1 -AppPath '.\winui\artifacts\win-x64\TwinkleTray.WinUI.exe'
```

The integration script verifies a newly written startup report with six settings pages, then starts a hidden demo instance and checks monitor listing, set-all, a per-monitor offset, invalid-monitor errors, and help. Each process wait is limited to 30 seconds. Logs and results are retained under `winui/artifacts/test-results/`; `-TestOutput` overrides this location. Close other demo instances first. The script only stops the processes it starts.

통합 검사 스크립트는 새로 작성된 GUI 시작 보고서와 설정 페이지 6개를 확인한 다음, 숨겨진 데모 인스턴스를 실행해 목록·밝기 일괄 설정·개별 증감·오류 응답·도움말을 검사합니다. 다른 데모 인스턴스는 먼저 닫아 주세요. 각 프로세스의 대기 시간은 최대 30초이며 자신이 실행한 프로세스만 종료합니다. 결과와 로그는 `winui/artifacts/test-results/`에 남습니다.

Verified: **14/14 core tests**, a **Release build with zero warnings and zero errors**, and successful **x64 and ARM64 publishing**. All **five x64 integration-test groups passed**: fresh native startup with six settings pages, demo listing, IPC brightness updates (60/65), invalid-monitor failure, and help. The Korean brightness panel and General, Monitors, and Hotkeys settings were visually inspected. Read-only hardware discovery succeeded with two external monitors; real brightness, contrast, and power writes have **not** been exercised. ARM64 runtime behavior remains untested.

**코어 테스트 14개**, **경고·오류가 없는 Release 빌드**, **x64·ARM64 배포 빌드**를 확인했습니다. **x64 통합 검사 5개 항목도 모두 통과**했습니다. 설정 페이지 6개를 포함한 GUI 시작, 데모 목록, IPC 밝기 변경 결과 60/65, 잘못된 모니터에 대한 오류 응답, 도움말을 검사했습니다. 한국어 밝기 패널과 일반·모니터·단축키 설정 화면도 육안으로 확인했습니다. 외부 모니터 두 대의 읽기 전용 탐지도 확인했으며 실제 밝기·대비·전원 변경과 ARM64 장치 실행은 검증하지 않았습니다.

The [WinUI workflow](../.github/workflows/winui.yml) builds self-contained x64 and ARM64 outputs and runs the core tests. GUI/IPC integration testing is opt-in on manual runs because an interactive desktop is not guaranteed on hosted runners; it uses simulated monitors and skips when no interactive desktop is available. The upstream Electron workflow remains separate.

## 소스 구성 · Source layout

| Project | Responsibility |
| --- | --- |
| `TwinkleTray.Core` | Settings, calibration math, daily schedules, command-line parsing; independent of WinUI. |
| `TwinkleTray.Core.Tests` | Package-free executable tests; nonzero exit code on failure. |
| `TwinkleTray.Hardware` | Serialized DDC/CI and WMI access, monitor identity and discovery, native handle ownership. |
| `TwinkleTray.WinUI` | Native panel/settings windows, tray, hotkeys, idle handling, startup integration, localization, and single-instance IPC. |

Twinkle Tray was created by Xander Frangos and contributors. This community port retains their copyright and the repository's MIT license.
