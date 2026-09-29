using System.Globalization;
using System.Text.Json;

namespace TwinkleTray.WinUI.Services;

/// <summary>Reuses the upstream translation files with English and caller-provided fallbacks.</summary>
public static class LocalizationService
{
    public const string ProductName = "Twinkle Tray Native";
    private static readonly Dictionary<string, string> English = Load("en");
    private static Dictionary<string, string> _translations = English;
    public static string CurrentLanguage { get; private set; } = "en";

    public static void Configure(string language)
    {
        var requested = string.IsNullOrWhiteSpace(language) || language == "system"
            ? CultureInfo.CurrentUICulture.Name
            : language;
        // Language values are settings data, never paths.
        if (requested.Any(c => !char.IsLetterOrDigit(c) && c != '-')) requested = "en";
        var translations = Load(requested);
        if (translations.Count == 0 && requested.Contains('-'))
        {
            requested = requested.Split('-')[0];
            translations = Load(requested);
        }
        CurrentLanguage = requested;
        _translations = translations.Count > 0 ? translations : English;
    }

    public static string Get(string key, string fallback)
    {
        string value;
        if (_translations.TryGetValue(key, out var translated) && !string.IsNullOrWhiteSpace(translated))
            value = translated;
        else if (CurrentLanguage.StartsWith("ko", StringComparison.OrdinalIgnoreCase) && Korean.TryGetValue(key, out translated))
            value = translated;
        else value = English.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english) ? english : fallback;

        // Only current-app labels are branded. Attribution and upstream import text
        // retain the original project's name, and the upstream JSON stays untouched.
        if (!ProductLabelKeys.Contains(key) || value.Contains(ProductName, StringComparison.Ordinal)) return value;
        foreach (var name in UpstreamProductNames) value = value.Replace(name, ProductName, StringComparison.Ordinal);
        return value;
    }

    private static readonly HashSet<string> ProductLabelKeys = new(StringComparer.Ordinal)
    {
        "INTRO_TITLE", "INTRO_INSTRUCTIONS", "SETTINGS_TITLE",
        "SETTINGS_GENERAL_BRIGHTNESS_STARTUP_DESC", "SETTINGS_GENERAL_ACRYLIC_DESC", "SETTINGS_GENERAL_MICA_DESC",
        "SETTINGS_GENERAL_DIS_MONITOR_FEATURES_DESC", "SETTINGS_GENERAL_DIS_OVERLAY_DESC",
        "SETTINGS_FEATURES_CUR_BRIGHTNESS_DESC", "SETTINGS_FEATURES_POWER_WARNING", "SETTINGS_FEATURES_ADD_DESC",
        "SETTINGS_FEATURES_VCP_LIST_DESC", "SETTINGS_TIME_STARTUP_DESC", "SETTINGS_UPDATES_VERSION", "SETTINGS_UPDATES_AVAILABLE"
    };

    private static readonly string[] UpstreamProductNames =
        ["Twinkle Tray", "توينكل تراي", "টুইঙ্কল ট্রে", "ट्विंकल ट्रे", "توینکڵ ترەی", "ட்விங்கிள் தட்டு", "ถาด Twinkle"];

    public static IReadOnlyList<(string Code, string Name)> AvailableLanguages()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Localization");
        try
        {
            if (Directory.Exists(path))
            {
                var languages = Directory.EnumerateFiles(path, "*.json").Select(file =>
                {
                    var code = Path.GetFileNameWithoutExtension(file);
                    return (Code: code, Name: Load(code).GetValueOrDefault("LANGUAGE", code));
                }).OrderBy(language => language.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                if (languages.Length > 0) return languages;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return [("en", "English"), ("ko", "한국어")];
    }

    private static Dictionary<string, string> Load(string language)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Localization", language + ".json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static readonly Dictionary<string, string> Korean = new()
    {
        ["NATIVE_MORE"] = "더 보기",
        ["NATIVE_APPEARANCE"] = "모양과 언어",
        ["NATIVE_BRIGHTNESS_SHORTCUTS"] = "밝기 연동과 스크롤",
        ["NATIVE_OVERLAY_SECTION"] = "밝기 알림",
        ["NATIVE_STARTUP_RESTORE"] = "시작 및 복원",
        ["NATIVE_DISABLED"] = "사용 안 함",
        ["NATIVE_MINUTES"] = "분",
        ["NATIVE_ACTIONS"] = "개 동작",
        ["NATIVE_FEATURES"] = "개 기능",
        ["NATIVE_NUMBER_RANGE"] = "{0}부터 {1} 사이의 숫자를 입력하세요.",
        ["NATIVE_DUPLICATE_CALIBRATION"] = "각 보정 지점에는 서로 다른 입력값을 사용하세요.",
        ["NATIVE_MIN_BELOW_MAX"] = "최솟값은 최댓값보다 작아야 합니다.",
        ["NATIVE_MAX_ABOVE_MIN"] = "최댓값은 최솟값보다 커야 합니다.",
        ["SETTINGS_MONITORS_EXTEND_MINIMUM_TITLE"] = "최소 밝기 확장",
        ["SETTINGS_MONITORS_EXTEND_MINIMUM_BREAKPOINT"] = "소프트웨어 밝기 조절을 사용하는 슬라이더 구간(%)",
        ["SETTINGS_HOTKEYS_NATIVE_BRIGHTNESS_WARN"] = "전용 밝기 키를 누르면 Windows가 내장 화면의 밝기를 직접 조절합니다. 이 단축키의 대상으로 내장 화면을 선택하지 마세요.",
        ["NATIVE_KEY_Up"] = "위쪽",
        ["NATIVE_KEY_Down"] = "아래쪽",
        ["NATIVE_KEY_Left"] = "왼쪽",
        ["NATIVE_KEY_Right"] = "오른쪽",
        ["NATIVE_KEY_PageUp"] = "이전 페이지",
        ["NATIVE_KEY_PageDown"] = "다음 페이지",
        ["NATIVE_KEY_Home"] = "처음(Home)",
        ["NATIVE_KEY_End"] = "끝(End)",
        ["NATIVE_KEY_Space"] = "스페이스",
        ["NATIVE_KEY_Enter"] = "엔터",
        ["NATIVE_KEY_Back"] = "백스페이스",
        ["NATIVE_KEY_Tab"] = "탭",
        ["NATIVE_KEY_Escape"] = "이스케이프(Esc)",
        ["NATIVE_KEY_Delete"] = "삭제(Delete)",
        ["NATIVE_KEY_Insert"] = "삽입(Insert)",
        ["NATIVE_KEY_Pause"] = "일시 정지(Pause)",
        ["NATIVE_KEY_CapitalLock"] = "대문자 잠금(Caps Lock)",
        ["NATIVE_KEY_Print"] = "인쇄",
        ["NATIVE_KEY_Snapshot"] = "화면 캡처(Print Screen)",
        ["NATIVE_KEY_NumberKeyLock"] = "숫자 잠금(Num Lock)",
        ["NATIVE_KEY_Scroll"] = "스크롤 잠금(Scroll Lock)",
        ["NATIVE_KEY_LeftButton"] = "마우스 왼쪽 버튼",
        ["NATIVE_KEY_RightButton"] = "마우스 오른쪽 버튼",
        ["NATIVE_KEY_MiddleButton"] = "마우스 가운데 버튼",
        ["NATIVE_KEY_XButton1"] = "마우스 추가 버튼 1",
        ["NATIVE_KEY_XButton2"] = "마우스 추가 버튼 2",
        ["NATIVE_KEY_Cancel"] = "취소",
        ["NATIVE_KEY_Clear"] = "지우기",
        ["NATIVE_KEY_Select"] = "선택",
        ["NATIVE_KEY_Execute"] = "실행",
        ["NATIVE_KEY_Help"] = "도움말",
        ["NATIVE_KEY_Application"] = "메뉴 키",
        ["NATIVE_KEY_Sleep"] = "절전",
        ["NATIVE_KEY_Multiply"] = "숫자 패드 곱하기",
        ["NATIVE_KEY_Add"] = "숫자 패드 더하기",
        ["NATIVE_KEY_Separator"] = "숫자 패드 구분 기호",
        ["NATIVE_KEY_Subtract"] = "숫자 패드 빼기",
        ["NATIVE_KEY_Decimal"] = "숫자 패드 소수점",
        ["NATIVE_KEY_Divide"] = "숫자 패드 나누기",
        ["NATIVE_KEYPAD"] = "숫자 패드",
        ["NATIVE_KEY_VolumeMute"] = "음소거",
        ["NATIVE_KEY_VolumeDown"] = "볼륨 낮추기",
        ["NATIVE_KEY_VolumeUp"] = "볼륨 높이기",
        ["NATIVE_KEY_MediaNextTrack"] = "다음 트랙",
        ["NATIVE_KEY_MediaPreviousTrack"] = "이전 트랙",
        ["NATIVE_KEY_MediaStop"] = "재생 중지",
        ["NATIVE_KEY_MediaPlayPause"] = "재생·일시 정지",
        ["NATIVE_KEY_GoBack"] = "뒤로",
        ["NATIVE_KEY_GoForward"] = "앞으로",
        ["NATIVE_KEY_Refresh"] = "새로 고침",
        ["NATIVE_KEY_Stop"] = "중지",
        ["NATIVE_KEY_Search"] = "검색",
        ["NATIVE_KEY_Favorites"] = "즐겨찾기",
        ["NATIVE_KEY_GoHome"] = "홈페이지",
        ["NATIVE_KEY_Convert"] = "입력 변환",
        ["NATIVE_KEY_NonConvert"] = "변환 취소",
        ["NATIVE_KEY_Accept"] = "확정",
        ["NATIVE_KEY_ModeChange"] = "입력 모드 변경",
        ["NATIVE_KEY_Kana"] = "가나 입력",
        ["NATIVE_KEY_Hangul"] = "한/영 전환",
        ["NATIVE_KEY_Hanja"] = "한자 변환",
        ["NATIVE_KEY_Kanji"] = "한자 입력",
        ["NATIVE_KEY_BrightnessUp"] = "밝기 높이기(Fn)",
        ["NATIVE_KEY_BrightnessDown"] = "밝기 낮추기(Fn)",
        ["NATIVE_ABOUT"] = "정보",
        ["NATIVE_ADVANCED"] = "고급",
        ["NATIVE_ADVANCED_DESC"] = "모니터 감지, 원격 명령, 설정 가져오기·내보내기 및 진단을 관리합니다.",
        ["NATIVE_SHOW_OVERLAY"] = "밝기 알림 표시",
        ["NATIVE_OVERLAY_TIMEOUT"] = "밝기 알림 표시 시간(초)",
        ["NATIVE_IDLE_SECONDS"] = "추가 대기 시간(초)",
        ["NATIVE_IDLE_RESTORE_DELAY"] = "유휴 상태 복원 지연(초)",
        ["NATIVE_FULL_FEATURES"] = "하드웨어 밝기 및 DDC/CI 기능, HDR의 SDR 밝기, 소프트웨어 밝기 조절, 모니터 밝기 보정, 태양 위치 예약, 다중 동작 단축키, 앱 프로필, 주변 조도 센서 및 유휴 상태 밝기 조절.",
        ["NATIVE_HARDWARE_SUPPORT"] = "하드웨어 지원",
        ["NATIVE_SUPPORTED_PLATFORMS"] = "지원 플랫폼",
        ["NATIVE_SUPPORTED_PLATFORMS_DESC"] = "Windows x64를 지원합니다. macOS, Apple 하드웨어 및 Windows ARM64 실행은 현재 지원하지 않습니다.",
        ["NATIVE_HARDWARE_SUPPORT_DESC"] = "사용할 수 있는 기능은 디스플레이와 센서가 지원하는 기능에 따라 달라집니다. 모니터 자체 설정에서 DDC/CI를 켜면 하드웨어 제어 기능을 사용할 수 있습니다.",
        ["NATIVE_POLL_SECONDS"] = "값 읽기 간격(초)",
        ["NATIVE_MAIN_CONTROL"] = "기본 밝기 조절 방식",
        ["NATIVE_GAMMA"] = "소프트웨어 밝기 조절(감마)",
        ["NATIVE_SOFTWARE_FALLBACK"] = "하드웨어 밝기 조절이 불가능하면 소프트웨어 밝기 조절 사용",
        ["NATIVE_FORCE_HDR"] = "HDR 감지에 실패해도 SDR 밝기 조절 표시",
        ["NATIVE_SKIP_RESTORE"] = "자동 밝기 복원에서 제외",
        ["NATIVE_BRIGHTNESS_VCP"] = "밝기 VCP 코드",
        ["NATIVE_QUERY_FEATURES"] = "지원되는 DDC/CI 기능 읽기",
        ["NATIVE_NO_REPORTED_FEATURES"] = "이 디스플레이가 보고한 기능 정보가 없습니다.",
        ["NATIVE_INVALID_VCP"] = "0x00부터 0xFF 사이의 VCP 코드를 입력하세요.",
        ["NATIVE_CALIBRATION"] = "밝기 보정",
        ["NATIVE_INPUT"] = "입력",
        ["NATIVE_OUTPUT"] = "출력",
        ["NATIVE_PROFILE_RESTORE"] = "앱에서 벗어나면 이전 밝기 복원",
        ["NATIVE_APPLY_PROFILE"] = "프로필 적용",
        ["NATIVE_SENSOR_HARDWARE"] = "센서 하드웨어 ID(비우면 첫 번째 센서 사용)",
        ["NATIVE_INVALID_SENSOR_ID"] = "Yocto 센서 ID는 영문, 숫자, 하이픈, 밑줄을 사용하며, 일련번호와 기능명은 점 하나로 구분할 수 있습니다.",
        ["NATIVE_QUERY_SENSORS"] = "연결된 센서 찾기",
        ["NATIVE_HARDWARE_DELAY"] = "하드웨어 변경 후 감지 지연(초)",
        ["NATIVE_VCP_DELAY"] = "VCP 값 읽기 지연(밀리초)",
        ["NATIVE_SHOW_NAME"] = "슬라이더에 모니터 이름 표시",
        ["NATIVE_SHOW_VALUE"] = "슬라이더에 밝기 값 표시",
        ["NATIVE_SLIDER_GLYPH"] = "슬라이더 아이콘(선택 사항, 문자 입력)",
        ["NATIVE_INVALID_ENDPOINT"] = "http:// 또는 https://로 시작하는 센서 주소를 입력하세요.",
        ["NATIVE_MIN_LUX"] = "최소 주변 조도(Lux)",
        ["NATIVE_MAX_LUX"] = "최대 주변 조도(Lux)",
        ["NATIVE_MIN_BRIGHTNESS"] = "최소 밝기",
        ["NATIVE_MAX_BRIGHTNESS"] = "최대 밝기",
        ["NATIVE_WMI"] = "Windows 내장 디스플레이 감지(WMI)",
        ["NATIVE_DDC"] = "외부 디스플레이 감지(DDC/CI)",
        ["NATIVE_APPLE"] = "Apple Studio Display 감지",
        ["NATIVE_AUTO_REFRESH"] = "하드웨어 변경 후 디스플레이 새로 고침",
        ["NATIVE_WAKE_DELAY"] = "절전 해제 후 복원 지연(초)",
        ["NATIVE_UDP_ENABLED"] = "UDP 명령 서버 사용",
        ["NATIVE_UDP_REMOTE"] = "다른 컴퓨터의 연결 허용",
        ["NATIVE_PORT"] = "포트",
        ["NATIVE_UDP_KEY"] = "인증 키",
        ["NATIVE_KEY_REQUIRED"] = "UDP 인증 키는 비워 둘 수 없습니다.",
        ["NATIVE_SETTINGS_TRANSFER"] = "설정 가져오기 및 내보내기",
        ["NATIVE_IMPORT"] = "설정 가져오기",
        ["NATIVE_IMPORT_DISPLAYS"] = "known-displays.json 가져오기",
        ["NATIVE_HOTKEY_SOURCE"] = "단축키 종류",
        ["NATIVE_STANDARD_KEY"] = "일반 키보드 단축키",
        ["NATIVE_IMAGE"] = "이미지",
        ["NATIVE_WINDOWS_GLYPH"] = "Windows 아이콘(유니코드 문자)",
        ["NATIVE_ICON_PATH"] = "로컬 아이콘 경로(.png, .jpg, .ico)",
        ["NATIVE_EXPORT"] = "설정 내보내기",
        ["NATIVE_RESET_CONFIRM"] = "Twinkle Tray Native의 모든 설정을 기본값으로 초기화할까요?",
        ["NATIVE_UNMAPPED"] = "연결하지 않음",
        ["NATIVE_APPLY_MAPPINGS"] = "디스플레이 연결 적용",
        ["NATIVE_LOGGING"] = "진단 로그 저장",
        ["NATIVE_OPEN_LOGS"] = "로그 폴더 열기",
        ["NATIVE_CHECK_UPDATES"] = "업데이트 확인",
        ["NATIVE_INSTALL_UPDATE"] = "업데이트 다운로드 및 설치",
        ["NATIVE_RELEASES"] = "릴리스 기록",
        ["NATIVE_SMOOTH_TRANSITIONS"] = "밝기 부드럽게 변경",
        ["NATIVE_TRANSITION_SECONDS"] = "밝기 변경 시간(초)",
        ["NATIVE_SCHEDULE_EVENT"] = "예약 기준",
        ["NATIVE_SOLAR_OFFSET"] = "기준 시각으로부터 차이(분)",
        ["NATIVE_DUPLICATE_HOTKEY"] = "다른 단축키가 같은 키 조합을 사용하고 있습니다. 하나만 등록할 수 있습니다.",
        ["NATIVE_SHOW_PANEL"] = "밝기 패널 표시",
        ["NATIVE_SET_VCP"] = "VCP 원시 값 설정",
        ["NATIVE_CHOOSE_PROFILE"] = "프로필 선택",
        ["NATIVE_INVALID_VALUES"] = "대상 범위 내의 숫자를 쉼표로 구분하여 입력하세요.",
        ["NATIVE_VALUES_REQUIRED"] = "순환할 값을 하나 이상 입력하세요.",
        ["NATIVE_SOLAR_dawn"] = "새벽",
        ["NATIVE_SOLAR_sunrise"] = "일출",
        ["NATIVE_SOLAR_sunriseEnd"] = "일출 종료",
        ["NATIVE_SOLAR_goldenHourEnd"] = "아침 골든아워 종료",
        ["NATIVE_SOLAR_solarNoon"] = "태양 정오",
        ["NATIVE_SOLAR_goldenHour"] = "저녁 골든아워",
        ["NATIVE_SOLAR_sunsetStart"] = "일몰 시작",
        ["NATIVE_SOLAR_sunset"] = "일몰",
        ["NATIVE_SOLAR_dusk"] = "황혼",
        ["NATIVE_SOLAR_nauticalDawn"] = "항해 새벽",
        ["NATIVE_SOLAR_nauticalDusk"] = "항해 황혼",
        ["NATIVE_SOLAR_nightEnd"] = "밤 종료",
        ["NATIVE_SOLAR_night"] = "밤",
        ["NATIVE_SOLAR_nadir"] = "태양 자정",
        ["NATIVE_GENERAL_DESCRIPTION"] = "Twinkle Tray Native의 모양과 동작을 설정합니다. 변경 사항은 자동으로 저장됩니다.",
        ["NATIVE_LINK_DESCRIPTION"] = "밝기 패널에서 모든 디스플레이의 밝기를 함께 조절합니다.",
        ["NATIVE_SCROLL_STEP"] = "밝기 조절 간격",
        ["NATIVE_SCROLL_DESCRIPTION"] = "슬라이더 위에서 마우스 휠을 움직일 때 변경할 밝기입니다.",
        ["NATIVE_MONITORS_DESCRIPTION"] = "디스플레이 이름, 표시 순서와 밝기 범위를 설정합니다.",
        ["NATIVE_DISPLAY_NAME"] = "표시 이름",
        ["NATIVE_HIDE"] = "밝기 패널에서 숨기기",
        ["NATIVE_SHOW_CONTRAST"] = "대비 슬라이더 표시",
        ["NATIVE_CONTRAST_UNAVAILABLE"] = "이 디스플레이에서는 대비 조절을 사용할 수 없습니다.",
        ["NATIVE_MOVE_UP"] = "위로",
        ["NATIVE_MOVE_DOWN"] = "아래로",
        ["NATIVE_NORMALIZE_DESCRIPTION"] = "밝기 패널의 0–100%를 아래의 실제 모니터 밝기 범위에 맞춥니다.",
        ["NATIVE_ENABLED"] = "사용",
        ["NATIVE_TIME_DESCRIPTION"] = "매일 지정한 현지 시간에 선택한 디스플레이의 밝기를 변경합니다.",
        ["NATIVE_NO_SCHEDULE"] = "예약된 밝기 변경이 없습니다. 시간을 추가하여 시작하세요.",
        ["NATIVE_TIME"] = "시간",
        ["NATIVE_INVALID_TIME"] = "저장된 시간이 올바르지 않습니다. 예약을 사용하려면 올바른 시간을 선택하세요.",
        ["NATIVE_INVALID_HOTKEY"] = "단축키를 사용하려면 올바른 키와 동작을 선택하세요.",
        ["NATIVE_DISPLAYS"] = "디스플레이",
        ["NATIVE_DISCONNECTED"] = "연결되지 않은 디스플레이",
        ["NATIVE_NO_HOTKEYS"] = "등록된 단축키가 없습니다. 단축키를 추가하여 시작하세요.",
        ["NATIVE_KEY"] = "키",
        ["NATIVE_MODIFIERS"] = "보조 키",
        ["NATIVE_HOTKEY_HINT"] = "보조 키와 키, 동작을 선택한 다음 단축키를 켜세요. 다른 앱이 사용 중인 조합은 등록되지 않을 수 있습니다.",
        ["NATIVE_IDLE_MINUTES"] = "대기 시간(분)",
        ["NATIVE_IDLE_BRIGHTNESS"] = "유휴 상태의 밝기",
        ["NATIVE_IDLE_RESTORE"] = "다시 마우스나 키보드를 사용하면 이전 밝기로 돌아갑니다.",
        ["NATIVE_PORT_TITLE"] = "Twinkle Tray Native",
        ["NATIVE_PORT_DESCRIPTION"] = "원본 Twinkle Tray의 구성과 번역을 바탕으로 만든 네이티브 Windows 앱입니다.",
        ["NATIVE_IMPLEMENTED_TITLE"] = "현재 구현된 기능",
        ["NATIVE_IMPLEMENTED"] = "DDC/CI 및 WMI 밝기 조절, 지원되는 모니터의 대비, 트레이 패널, 디스플레이별 설정, 예약 변경, 전역 단축키 및 유휴 상태 밝기 조절.",
        ["NATIVE_PENDING_TITLE"] = "아직 이식되지 않은 기능",
        ["NATIVE_PENDING"] = "고급 HDR/SDR 조절, 감마 조절, 조도 센서, 앱별 프로필 및 원본의 일부 고급 DDC/CI 기능은 아직 포함되어 있지 않습니다.",
        ["NATIVE_UPSTREAM"] = "원본 프로젝트",
        ["NATIVE_FORK"] = "Twinkle Tray Native 프로젝트",
        ["NATIVE_ATTRIBUTION"] = "Twinkle Tray 원작: Xander Frangos 및 기여자. 원본 MIT 라이선스와 저작권 고지를 유지합니다.",
        ["NATIVE_SAVED"] = "변경 사항은 자동으로 저장됩니다.",
        ["NATIVE_SUPPORTS_BRIGHTNESS"] = "밝기 조절 가능",
        ["NATIVE_NO_BRIGHTNESS"] = "밝기 조절을 지원하지 않음"
    };
}
