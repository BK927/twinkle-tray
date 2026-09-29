# Twinkle Tray Native

A native Windows brightness controller built with C# and WinUI 3, based on [Twinkle Tray](https://github.com/xanderfrangos/twinkle-tray) by Xander Frangos and contributors.

Twinkle Tray Native keeps the familiar tray workflow and monitor controls while running without Electron. This is an independent community fork maintained by BK927, with its own releases and updates.

**[Build and run](#build)** · [Release channel](https://github.com/BK927/twinkle-tray-native/releases) · [한국어 사용 안내](winui/README.md) · [Features and compatibility](winui/PORT-CHECKLIST.md) · [Report an issue](https://github.com/BK927/twinkle-tray-native/issues)

## Platform support

- **Windows x64**: Windows 10 version 2004 (build 19041) or later, and Windows 11. Physical-device validation so far used Windows 11 x64 and two external monitors.
- **Not supported in this release**: macOS, Apple hardware and Windows ARM64 execution. Retained Apple/ARM64 implementation or historical build results do not establish supported runtime behavior.
- Available brightness, contrast and HDR controls depend on the display and connection. See the [support boundaries](winui/PORT-CHECKLIST.md) before relying on a particular hardware feature.

## Features

- Tray brightness sliders with linked or independent monitor levels, mouse-wheel control, names, ordering and visibility.
- DDC/CI and WMI brightness, contrast and custom monitor features; HDR SDR-white-level control, gamma dimming and calibration where supported.
- Time and solar-event schedules, app profiles, multi-action hotkeys, idle dimming and ambient-light integration.
- Native settings, a separate brightness overlay, light/dark themes and responsive layouts.
- Original Twinkle Tray settings import, command-line and authenticated UDP control, portable updates and diagnostic tools.

The [port checklist](winui/PORT-CHECKLIST.md) distinguishes implemented features from actual device tests. The original Electron source is retained in this repository.

## Try 0.3.0

**A packaged 0.3.0 release has not been published yet.** Build the x64 application from source using the steps below. Once a release is published, its portable ZIP will be available from this project's [release channel](https://github.com/BK927/twinkle-tray-native/releases). Keep the complete output folder, including DLLs, `Assets` and `Localization`, together and run `TwinkleTray.WinUI.exe`; the executable keeps its existing filename for compatibility.

To try the interface without changing real monitor brightness:

```powershell
.\TwinkleTray.WinUI.exe --demo
.\TwinkleTray.WinUI.exe --demo --settings
```

The current development version is **0.3.0**. When moving from the earlier 0.2.x “Twinkle Tray · WinUI 3” builds, close the old app and install the new build manually once. Existing settings remain at `%APPDATA%\TwinkleTray.WinUI\settings.json`; there is no need to move or rename them. Automatic updating across the repository rename is not guaranteed for old binaries. [Packaging and upgrades](winui/PACKAGING.md) explains the preserved compatibility identifiers.

## Build

Install the .NET 10 SDK on Windows, then run from the repository root:

```powershell
.\winui\build.ps1 -Architecture x64
```

The self-contained application is published to `winui/artifacts/win-x64/`. Node.js and Electron are not needed for this native build. Detailed CLI, import, packaging and verification instructions are in [winui/README.md](winui/README.md).

## Verification

The **local 0.3.0 Windows x64 run** passed 46 core tests, 11 settings pages, 44 simulated runtime assertions (including 20 automation regressions), 144 layout cases, 36 interaction/popup checks and 11 IPC groups. Fourteen preview images were generated; UI fixtures made no hardware or user-settings writes. Representative images were visually reviewed for the Windows 11 bottom toolbar, numbers beside sliders, one linked slider and compact extra controls.

The original panel was compared against its source; an installed upstream panel was not inspected live. Actual high-contrast and separate Windows DPI sessions remain untested. This is local verification, not a published release or a completed CI result for these changes. [UI verification](winui/UI-POLISH.md) records the fresh evidence and limits; [physical-device results](winui/VERIFICATION.md) preserve the separate 0.2.1 tests.

## 한국어

**Twinkle Tray Native**는 원본 Twinkle Tray의 트레이 사용 방식과 모니터 설정을 유지하면서 C#과 WinUI 3로 옮긴 Windows x64용 커뮤니티 포크입니다. **0.3.0 배포본은 아직 게시되지 않았습니다.** 현재는 위 빌드 방법으로 x64 앱을 만든 뒤 `TwinkleTray.WinUI.exe`를 실행할 수 있으며, 배포본이 게시되면 이 저장소의 릴리스 페이지에서 제공됩니다. macOS·Apple 하드웨어·ARM64 실행은 현재 지원 범위에 포함하지 않습니다.

0.2.x에서 처음 전환할 때는 기존 앱을 종료하고 새 빌드를 수동으로 설치하세요. 기존 설정 경로와 파일 형식은 유지합니다. 사용법과 검증 범위는 [한국어 안내](winui/README.md)에 정리했습니다.

## Original project and license

Twinkle Tray was created by **Xander Frangos and contributors**. This fork preserves the original source, assets, translations, copyright notices and [MIT license](LICENSE). The untouched original README is kept in [docs/README.upstream.md](docs/README.upstream.md); its downloads and installation instructions refer to the original Electron application.

The upstream reference is [`e3d5bb0`](https://github.com/xanderfrangos/twinkle-tray/commit/e3d5bb0bde75f7ef1a6ae450b314090c804e0f8e). Additional SunCalc attribution is retained in [ThirdPartyNotices.txt](winui/TwinkleTray.Core/ThirdPartyNotices.txt). Changes for this native fork are maintained in `winui/`.
