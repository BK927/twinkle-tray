using System.Globalization;
using System.Text.Json;

namespace TwinkleTray.WinUI.Services;

/// <summary>Reuses the upstream translation files with English and caller-provided fallbacks.</summary>
public static class LocalizationService
{
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
        if (_translations.TryGetValue(key, out var translated) && !string.IsNullOrWhiteSpace(translated))
            return translated;
        if (CurrentLanguage.StartsWith("ko", StringComparison.OrdinalIgnoreCase) && Korean.TryGetValue(key, out translated))
            return translated;
        return English.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english) ? english : fallback;
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
        ["NATIVE_ABOUT"] = "정보",
        ["NATIVE_GENERAL_DESCRIPTION"] = "Twinkle Tray의 모양과 동작을 설정합니다. 변경 사항은 자동으로 저장됩니다.",
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
        ["NATIVE_PORT_TITLE"] = "Twinkle Tray · WinUI 3",
        ["NATIVE_PORT_DESCRIPTION"] = "원본 Twinkle Tray의 구성과 번역을 바탕으로 만든 네이티브 Windows 앱입니다.",
        ["NATIVE_IMPLEMENTED_TITLE"] = "현재 구현된 기능",
        ["NATIVE_IMPLEMENTED"] = "DDC/CI 및 WMI 밝기 조절, 지원되는 모니터의 대비, 트레이 패널, 디스플레이별 설정, 예약 변경, 전역 단축키 및 유휴 상태 밝기 조절.",
        ["NATIVE_PENDING_TITLE"] = "아직 이식되지 않은 기능",
        ["NATIVE_PENDING"] = "고급 HDR/SDR 조절, 감마 조절, 조도 센서, 앱별 프로필 및 원본의 일부 고급 DDC/CI 기능은 아직 포함되어 있지 않습니다.",
        ["NATIVE_UPSTREAM"] = "원본 프로젝트",
        ["NATIVE_FORK"] = "WinUI 3 포크",
        ["NATIVE_ATTRIBUTION"] = "Twinkle Tray 원작: Xander Frangos 및 기여자. 원본 MIT 라이선스와 저작권 고지를 유지합니다.",
        ["NATIVE_SAVED"] = "변경 사항은 자동으로 저장됩니다.",
        ["NATIVE_SUPPORTS_BRIGHTNESS"] = "밝기 조절 가능",
        ["NATIVE_NO_BRIGHTNESS"] = "밝기 조절을 지원하지 않음"
    };
}
