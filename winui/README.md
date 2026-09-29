# Twinkle Tray · WinUI 3 · 0.2.1

Twinkle Tray의 트레이 패널, 설정 구성과 주요 동작을 C#과 WinUI 3로 옮긴 커뮤니티 포크입니다. 밝기 조절뿐 아니라 고급 DDC/CI, HDR/감마, 시간 예약, 앱별 프로필, 조도 센서, 설정 가져오기와 업데이트 기능을 포함합니다. 원본 Electron 소스는 저장소에 보존하고 네이티브 앱은 `winui/`에 분리했습니다.

**구현된 기능과 실제 장치에서 검증한 범위는 다릅니다.** WinUI 컨트롤을 사용하므로 원본과 픽셀 단위로 같지는 않습니다. 항목별 원본 대응, 차이점과 미검증 사항은 [이식 체크리스트](PORT-CHECKLIST.md), 하드웨어별 동작은 [Windows 백엔드 문서](TwinkleTray.Hardware/README.md)에 정리했습니다.

- 포크: [BK927/twinkle-tray · winui3](https://github.com/BK927/twinkle-tray/tree/winui3)
- 원본: [xanderfrangos/twinkle-tray](https://github.com/xanderfrangos/twinkle-tray)
- 이식 기준: [`e3d5bb0`](https://github.com/xanderfrangos/twinkle-tray/commit/e3d5bb0bde75f7ef1a6ae450b314090c804e0f8e)
- 원본 이미지·아이콘·번역·저작권 표시와 [MIT 라이선스](../LICENSE)를 유지합니다. 태양 위치 계산의 별도 고지는 [ThirdPartyNotices.txt](TwinkleTray.Core/ThirdPartyNotices.txt)에 포함됩니다.

## 빌드와 실행

Windows 10 버전 2004(빌드 19041) 이상 또는 Windows 11이 필요합니다. 빌드에는 **.NET 10 SDK**와 NuGet 패키지를 복원할 인터넷 연결이 필요하며, 네이티브 앱을 빌드할 때 Node.js나 Electron은 사용하지 않습니다. `global.json`은 안정 버전 .NET 10 SDK를 선택하고 설치된 SDK 파일은 수정하지 않습니다.

저장소 루트에서 PowerShell로 실행하세요.

```powershell
# 코어 테스트 후 x64 Release 빌드
.\winui\build.ps1

# 실제 모니터를 변경하지 않는 데모
.\winui\artifacts\win-x64\TwinkleTray.WinUI.exe --demo

# 데모 설정 화면
.\winui\artifacts\win-x64\TwinkleTray.WinUI.exe --demo --settings

# 일반 실행
.\winui\artifacts\win-x64\TwinkleTray.WinUI.exe

# x64와 ARM64를 함께 빌드
.\winui\build.ps1 -Architecture x64,ARM64
```

결과는 `winui/artifacts/win-x64/` 또는 `winui/artifacts/win-arm64/`에 생성됩니다. .NET과 Windows App SDK 런타임을 포함한 자체 포함 빌드입니다. 실행 파일만 복사하지 말고 **DLL, Assets, Localization을 포함한 폴더 전체**를 함께 옮기세요. ARM64 결과물은 ARM64 Windows에서 사용하며, 실제 ARM64 장치 실행은 아직 검증하지 않았습니다.

## 구현된 기능

| 범위 | 0.2.1 구현 |
| --- | --- |
| 트레이·패널 | 모니터별 슬라이더, 밝기 연동, 휠 조절, 이름·순서·숨김, 새로 고침, 전원 제어, 시작 프로그램 등록 |
| 외형 | 시스템·밝은·어두운 테마, Windows 10/11 스타일, 아크릴, 원본 트레이 아이콘, 별도의 밝기 OSD와 표시 시간·전체 화면 정책 |
| 하드웨어 밝기 | 외부 모니터 DDC/CI, 내장 화면 WMI, Windows 고수준 API 대체 경로, Apple Studio Display HID |
| 고급 DDC/CI | 지원 기능 조회, 대비·볼륨·음소거·입력·전원·색상·사용자 VCP, 기능별 범위·밝기 연동·표시 아이콘, 밝기 VCP 코드 재정의 |
| 보정·HDR·감마 | 최소·최대 및 다중 지점 보정, HDR의 SDR 화이트 레벨, 감마 밝기, 하드웨어 제어가 없을 때 소프트웨어 대체, 하드웨어 최소 밝기 아래로 확장 |
| 시간별 밝기 | 고정 시각, 모니터별 값, 일출·일몰 등 태양 위치 이벤트와 오프셋, 하루 일정 사이 보간, 부드러운 전환 |
| 단축키 | 순서대로 실행하는 여러 동작, 설정·증감·순환·전원·프로필·새로 고침·패널·VCP, 전용 밝기 키, 모니터 선택과 연동 해제 |
| 유휴·복원 | 분·초 단위 유휴 감지, 전체 화면·미디어 재생 예외, 지연 복원, 잠금 화면 억제, 절전 복귀·장치 변경 후 복원, 닫힌 노트북 화면 숨김 |
| 앱별 프로필 | 실행 파일 경로에 따른 밝기·OSD 설정, 이전 밝기 복원, 트레이에서 수동 적용 |
| 조도 센서 | Windows 센서, Yoctopuce 허브, 모의 센서, 모니터별 조도·밝기 범위 |
| 외부 제어 | 명령줄, 사용자·세션별 단일 인스턴스와 파이프, 키 인증 UDP 서버·클라이언트 |
| 설정·배포 | Electron 설정과 별도 known-displays 가져오기, ID 매핑, 내보내기·백업·초기화, 진단, 업데이트 확인·검증·설치, 포터블 ZIP·MSIX 패키징 |

모니터 기능은 연결 방식과 펌웨어에 따라 달라집니다. 지원되지 않는 외부 모니터는 모니터 자체 메뉴에서 DDC/CI를 켜고 새로 고침해 보세요. HDR/감마와 Apple HID의 조건, 감마 복원 방식은 [하드웨어 문서](TwinkleTray.Hardware/README.md)를 참고하세요. 검증한 두 모니터 밖의 장치, 노트북·Apple 화면·조도 센서의 동작은 아래 검증 범위와 구분합니다.

프로필 경로는 쉼표로 나눈 문자열을 대소문자 구분 없이 포함하는 실행 파일에 적용하며, 여러 프로필이 일치하면 마지막 활성 프로필을 선택합니다. 자동 제어 우선순위는 유휴 밝기, 실행 중인 앱의 프로필, 조도 센서, 시간 예약 순입니다. 부드러운 전환은 초 단위 지속 시간을 사용하므로 원본의 단계별 속도 설정을 가져올 때 근사 변환 사실을 표시합니다.

## 명령줄

```powershell
$app = '.\winui\artifacts\win-x64\TwinkleTray.WinUI.exe'
& $app --List
& $app --MonitorNum=1 --Set=65
& $app --MonitorID="UID2353" --Offset=-10 --Overlay
& $app --All --Set=50
& $app --UseTime
& $app --Panel
& $app --settings
& $app --background
& $app --help

# 설정 > 고급에서 UDP를 켜고 앱을 실행한 뒤 사용
& $app --UDP --List
& $app --UDP --All --Set=50
& $app --UDP --UseTime
```

밝기·VCP 명령에는 선택자 하나(`--All`, `--MonitorNum`, `--MonitorID`)와 동작 하나(`--Set`, `--Offset`, `--VCP`)가 필요합니다. 번호는 `--List`에 표시된 순서의 1부터 시작하며, ID는 전체 ID 또는 모니터 하나에만 일치하는 부분 문자열입니다. 목록은 JSON으로 출력하고 모호한 선택이나 잘못된 옵션은 오류로 반환합니다.

`--UseTime`은 현재 시간 예약을 적용합니다. `--Overlay`는 별도 OSD를, `--settings`는 설정 창을 엽니다. 실행 중인 앱이 있으면 해당 인스턴스가 명령을 처리합니다. 소프트웨어 감마의 수명을 유지해야 하는 명령은 필요하면 앱을 알림 영역에 남깁니다.

`--VCP=0x10:50`은 **MCCS 원시 값**이며 보정된 밝기 비율이 아닙니다. 일반 밝기 조절에는 보정 범위를 반영하는 `--Set`·`--Offset`을 사용하세요. UDP는 저장된 포트와 키로 로컬 서버에 접속합니다. `--demo`와 `--UDP`를 함께 사용할 수 없습니다.

## 원본 설정과 기존 밝기 가져오기

설정의 **고급** 페이지에서 Electron의 `settings.json`을 선택합니다. 원본과 네이티브 모니터 ID가 다를 수 있으므로 가져오기 결과에 표시된 원본 ID를 현재 모니터에 매핑한 뒤 다시 적용하세요. 매핑되지 않은 값은 `unresolved:` 식별자로 보존하며 실제 모니터에 적용하지 않습니다. 원본 JSON과 지원되지 않은 항목의 보고서도 남깁니다.

원본의 마지막 밝기는 별도 `known-displays.json`에 저장됩니다. 고급 페이지의 별도 가져오기 기능으로 이 파일도 선택하면 매핑된 밝기를 복원 설정으로 옮길 수 있습니다. 서로 다른 디스플레이 기록이 같은 대상에 매핑되면 충돌을 보고하고 해당 값은 적용하지 않습니다. 원본 모델·인스턴스 ID의 별칭은 같은 대상에 매핑할 수 있습니다.

가져오기는 원본 Electron 파일을 수정하지 않습니다. 기존 네이티브 설정은 교체 전에 백업하며, 가져온 시작 프로그램 설정을 자동으로 활성화하지 않습니다. 내보낸 파일에는 UDP 인증 키가 포함됩니다.

- 네이티브 설정: `%APPDATA%\TwinkleTray.WinUI\settings.json`
- 오류 로그: `%LOCALAPPDATA%\TwinkleTray.WinUI\errors.log`
- 손상된 설정 JSON: 조용히 덮어쓰지 않고 원본과 가능한 복구 사본을 보존합니다.
- 데모: 모의 모니터와 메모리 내 설정을 사용하며 실제 밝기나 저장된 환경설정을 변경하지 않습니다.

## ZIP·MSIX 패키지와 업데이트

```powershell
# 빌드한 x64 앱의 포터블 ZIP만 생성
.\winui\package.ps1 -Architecture x64 -Version 0.2.1 -SkipMsix

# ZIP과 MSIX 생성 — Windows SDK의 makeappx.exe 필요
.\winui\package.ps1 -Architecture x64 -Version 0.2.1
.\winui\package.ps1 -Architecture ARM64 -Version 0.2.1
```

패키지와 아키텍처별 SHA-256 목록은 `winui/artifacts/packages/`에 생성됩니다. 기본 MSIX는 **서명되지 않은 검증용 패키지**입니다. 실제 설치·배포에는 패키지 Publisher와 일치하는 신뢰된 인증서로 서명해야 합니다. 스크립트는 `-Publisher`와 `-CertificatePath`를 지원하며, 인증서를 자동으로 신뢰 목록에 설치하지 않습니다. 서명된 설치·시작 프로그램·제거 동작은 별도 검증 대상입니다. 프리뷰에서 정식 버전으로의 MSIX 업그레이드에는 숫자 리비전 관리가 필요합니다. `-MsixRevision`과 배포 방법은 [패키징 문서](PACKAGING.md)를 참고하세요.

포터블 앱의 업데이트는 BK927 저장소의 `winui-v…` 릴리스와 아키텍처에 맞는 ZIP을 사용합니다. 업데이트 화면에서 확인·다운로드·설치를 실행하며 SHA-256 확인 후 별도 준비 폴더에서 설치하고 실패 시 기존 파일 복구를 시도합니다. 릴리스에는 ZIP과 `SHA256SUMS.txt`가 필요합니다. MSIX 설치본은 해당 패키지 배포 경로로 업데이트하며, 원본 Microsoft Store 앱의 배포 ID를 공유하지 않습니다.

## 검증

0.2.1 자동 검증의 대상은 Windows x64입니다. 이번에 찾은 오류와 실물 모니터 시험 범위는 [자동 검증 보고서](VERIFICATION.md)를 참고하세요.

```powershell
# WinUI·하드웨어와 독립적인 코어 테스트
dotnet run --project .\winui\TwinkleTray.Core.Tests --configuration Release

# 실제 모니터를 쓰지 않는 GUI·IPC 통합 검사
.\winui\test-app.ps1 -AppPath '.\winui\artifacts\win-x64\TwinkleTray.WinUI.exe'

# 장치 읽기 전용 진단
dotnet run --project .\winui\TwinkleTray.Hardware\Diagnostics --configuration Release -- --read-only
```

통합 스크립트는 새로 생성된 GUI 검사 보고서를 확인하고 숨겨진 데모 인스턴스로 명령을 검사합니다. 다른 데모 인스턴스를 먼저 닫아 주세요. 각 프로세스 대기에는 제한 시간이 있으며 스크립트가 직접 시작한 프로세스만 종료합니다. 결과는 `winui/artifacts/test-results/`에 남고 `-TestOutput`으로 위치를 바꿀 수 있습니다.

현재 확인한 결과:

| 검사 | 확인된 범위 |
| --- | --- |
| 코어 | **46/46 통과**: 보정·일정·태양 위치·센서 곡선·프로필·설정 복구·가져오기·SemVer·CLI |
| 확장 GUI·런타임 | **설정 페이지 11개와 런타임 검사 41개 통과**. 런타임 검사에는 자동 제어 회귀 20개가 포함됩니다. 감마·HDR·최소 밝기 확장·다중 단축키·프로필·전환 취소·지연 복원·UDP 인증과 잘못된 입력·업데이트 파일 복사/롤백을 모의 환경에서 검사했습니다. |
| 데모 IPC | **통합 검사 11개 그룹 통과**. 시작·목록·밝기 60/65·ID별 제어·범위 제한·잘못된 명령 후 정상 복구·도움말·시간 예약·OSD·설정 창을 확인했습니다. |
| 빌드 | **0.2.1 x64 Release 배포 빌드에서 경고·오류 0개**를 확인했습니다. 이전 0.2.0의 ARM64 빌드는 통과했으나 0.2.1 ARM64 재빌드와 실제 ARM64 장치 실행은 아직 미검증입니다. |
| 실제 장치 DDC/CI | Windows 11 x64의 LG ULTRAGEAR+에서 밝기 **100→98→100**, 대비 **70→72→70**, AOC Q32V3WG5에서 밝기 **100→98→100**, 대비 **50→52→50**을 확인했습니다. 각 변경의 재조회·원값 복원·다른 화면 값 유지가 통과했습니다. 초기 핸들 조회 재시도 수정 후 새 프로세스 3회 모두 첫 조회에서 두 화면의 DDC/CI를 정상 인식했습니다. |
| 실제 장치 감마 | 같은 두 화면에 각각 **98% 감마 밝기**를 요청한 뒤 원본 감마 램프의 **768개 값 모두 정확히 복원**하고 다른 화면의 램프가 유지됨을 확인했습니다. 이 검사는 앱 종료·핫플러그 등 자동 복원 수명주기 검증을 포함하지 않습니다. |
| MSIX·업데이트 | **서명되지 않은 MSIX 시험 패키지 구성 검증, 시험 설치 폴더의 파일 교체와 실패 시 롤백 통과**. 모의 HTTP 응답으로 릴리스 선택·다운로드·체크섬·압축 해제·잘못된 파일 거부도 통과했습니다. 실제 릴리스 서버와 부모 프로세스 종료·재시작 전체 흐름, 서명된 MSIX 설치는 미검증입니다. |

<!-- RELEASE EVIDENCE: update this paragraph and count references together when rerunning verification. -->
위 GUI·IPC 결과는 2026-09-29 06:41:02 UTC에 완료한 `winui/artifacts/test-results/20260929T064038744-0407eee76f434b2fb704e08b9b3a46f6/`의 `smoke-test.json`, `integration-test.json`, `automation-regression.json`을 기준으로 합니다. 통합 보고서에는 실행 파일과 WinUI·Core·Hardware DLL의 SHA-256도 기록했습니다. 모의 화면 2개를 사용했고 하드웨어 쓰기 횟수는 0입니다. 한국어 일반·DDC/CI·프로필 편집·조도 센서 설정을 기본 창 크기에서 확인하고 밝기 패널도 확인했습니다. `--demo --settings`로 처음 여는 설정 창의 표시·크기 문제 역시 수정 후 정상 표시를 확인했습니다. 영어·밝은 테마·고대비·여러 DPI의 전체 조합 검사는 아직 수행하지 않았습니다.

실물 검증 범위는 Windows 11 x64에 연결된 위 외부 모니터 2대입니다. 전원·입력·HDR/SDR 화이트 레벨 변경은 수행하지 않았습니다. 노트북 WMI·전용 밝기 키·덮개 상태, Apple 화면, 실제 조도 센서, 절전·핫플러그 복원, 앱 종료·제어 경로 변경·핫플러그에 따른 감마 자동 복원, ARM64 실행은 추가 검증 대상입니다. [이식 체크리스트](PORT-CHECKLIST.md)의 미검증 항목을 구현 누락과 혼동하지 마세요.

[WinUI CI](../.github/workflows/winui.yml)는 x64·ARM64 빌드, 코어 검사, 읽기 전용 진단과 ZIP·MSIX 패키징을 수행합니다. GUI·IPC 검사는 수동 실행에서 선택하며, 대화형 데스크톱이 없으면 생략됩니다. CI 구성 자체가 모든 실행의 성공 증거는 아닙니다.

## 빌드 문제와 소스 구성

XAML 컴파일·패키지 복원에서 260자 경로 오류가 발생하면 저장소와 NuGet 캐시를 짧고 쓰기 가능한 경로에 두세요. 다음 설정은 현재 PowerShell 세션에만 적용됩니다.

```powershell
$env:NUGET_PACKAGES = 'C:\nuget'
.\winui\build.ps1
```

| 프로젝트 | 역할 |
| --- | --- |
| `TwinkleTray.Core` | 설정·보정·태양 위치·일정·센서 곡선·프로필·가져오기·버전 비교·CLI |
| `TwinkleTray.Core.Tests` | 외부 테스트 패키지가 필요 없는 실행형 테스트 |
| `TwinkleTray.Hardware` | DDC/CI·WMI·HDR·감마·Apple HID·센서와 Windows 상태 관찰 |
| `TwinkleTray.WinUI` | 패널·설정·OSD·트레이·단축키·자동화·IPC·UDP·업데이트 |

## English

This community C#/WinUI 3 port implements Twinkle Tray's main controls and advanced DDC/CI, HDR SDR-white-level adjustment, gamma dimming, calibration, solar schedules, multi-action hotkeys, app profiles, ambient sensors, Electron settings/known-display import, UDP control and portable updates. The original Electron sources, assets, translations and license remain intact.

Build on Windows 10 build 19041 or later with the .NET 10 SDK: `./winui/build.ps1 -Architecture x64,ARM64`. Run with `--demo` for simulated monitors, `--settings` for preferences, or `--help` for CLI usage. Distribute the whole self-contained output directory or follow [PACKAGING.md](PACKAGING.md); default MSIX packages are unsigned and require trusted signing before deployment.

**Implemented does not mean tested on every device.** All 46 core tests, 11 settings pages, 41 simulated runtime assertions (including 20 automation regressions) and 11 IPC test groups passed, including fixture update copying and rollback. The 0.2.1 x64 Release build passed; the ARM64 build passed for 0.2.0 and needs revalidation for 0.2.1. Physical tests covered LG ULTRAGEAR+ and AOC Q32V3WG5 on Windows 11 x64: small DDC brightness/contrast changes, exact restoration, and 98% gamma requests with all 768 original ramp values restored and the other display unchanged. Gamma restoration on shutdown/hotplug, Apple hardware, ARM64 execution, signed MSIX installation and the full download/process-restart upgrade flow remain unverified. See the [verification report](VERIFICATION.md), [source audit and validation checklist](PORT-CHECKLIST.md) and [hardware documentation](TwinkleTray.Hardware/README.md) for precise limits.

Twinkle Tray was created by Xander Frangos and contributors. This community port retains their copyright and the repository's MIT license.
