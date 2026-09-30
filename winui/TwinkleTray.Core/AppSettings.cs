namespace TwinkleTray.Core;

public sealed class AppSettings
{
    public string Theme { get; set; } = "system";
    public string Language { get; set; } = "system";
    public bool LinkedBrightness { get; set; }
    public bool RunAtStartup { get; set; }
    public int ScrollStep { get; set; } = 5;
    public bool IdleEnabled { get; set; }
    public int IdleMinutes { get; set; } = 10;
    public int IdleBrightness { get; set; } = 20;
    public Dictionary<string, MonitorSettings> Monitors { get; set; } = new();
    public List<ScheduleEntry> Schedule { get; set; } = new();
    public List<HotkeyBinding> Hotkeys { get; set; } = new();
    public bool RestoreBrightnessAtStartup { get; set; }
    public bool CheckScheduleAtStartup { get; set; } = true;
    public bool SmoothTransitions { get; set; }
    public bool ScheduleInterpolation { get; set; }
    public int TransitionSeconds { get; set; } = 10;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public bool HotkeysBreakLinkedLevels { get; set; } = true;
    public bool ShowOverlay { get; set; } = true;
    public int OverlayTimeoutSeconds { get; set; } = 3;
    public string OverlayPolicy { get; set; } = "safe";
    public string WindowsStyle { get; set; } = "system";
    public bool UseAcrylic { get; set; } = true;
    public bool InvertScroll { get; set; }
    public bool TrayScrollEnabled { get; set; } = true;
    public int TrayScrollStep { get; set; } = 2;
    public string TrayIcon { get; set; } = "fluent";
    public bool PollBrightness { get; set; }
    public int PollSeconds { get; set; } = 30;
    public bool DisableOnLockScreen { get; set; } = true;
    public int IdleSeconds { get; set; }
    public bool IdleCheckFullscreen { get; set; }
    public bool IdleCheckMedia { get; set; }
    public int IdleRestoreSeconds { get; set; }
    public int WakeRestoreSeconds { get; set; }
    public int HardwareRestoreSeconds { get; set; } = 2;
    public int UpdateIntervalMilliseconds { get; set; } = 90;
    public int VcpReadDelayMilliseconds { get; set; }
    public bool DisableAutoRefresh { get; set; }
    public bool DisableAutoApply { get; set; }
    public bool HideClosedLid { get; set; }
    public bool ThemeNotifications { get; set; } = true;
    public bool PowerNotifications { get; set; } = true;
    public bool DisableWmi { get; set; }
    public bool DisableDdc { get; set; }
    public bool DisableAppleStudio { get; set; }
    public bool Logging { get; set; } = true;
    public bool UseSoftwareBrightnessFallback { get; set; }
    public int PowerOffValue { get; set; } = 5;
    public string PowerOffMode { get; set; } = "ddc";
    public bool CheckForUpdates { get; set; }
    public string UpdateChannel { get; set; } = "stable";
    public bool UdpEnabled { get; set; }
    public bool UdpRemote { get; set; }
    public int UdpPort { get; set; } = 14715;
    public string UdpKey { get; set; } = Guid.NewGuid().ToString("N");
    public SensorSettings Sensor { get; set; } = new();
    public List<AppProfile> Profiles { get; set; } = new();
    public Dictionary<string, double> LastBrightness { get; set; } = new();
    public Dictionary<string, string> ImportIdentityMap { get; set; } = new();
    public string ImportedUpstreamJson { get; set; } = "";
    public string ImportedKnownDisplaysJson { get; set; } = "";
    public List<string> ImportUnmappedMonitorIds { get; set; } = new();
    public List<string> ImportWarnings { get; set; } = new();
}

public sealed record MonitorSettings
{
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public bool Hidden { get; set; }
    public int MinBrightness { get; set; }
    public int MaxBrightness { get; set; } = 100;
    public bool ShowContrast { get; set; }
    public bool ShowName { get; set; } = true;
    public bool ShowValue { get; set; } = true;
    public string IconGlyph { get; set; } = "";
    public string MainControl { get; set; } = "brightness";
    public bool SoftwareFallback { get; set; }
    public bool ExtendMinimum { get; set; }
    public int ExtendMinimumBreakpoint { get; set; } = 20;
    public List<CalibrationPoint> Calibration { get; set; } = new();
    public byte BrightnessVcp { get; set; } = 0x10;
    public Dictionary<byte, FeatureSettings> Features { get; set; } = new();
    public bool SkipRestore { get; set; }
    public bool ForceHdr { get; set; }
}

public sealed class ScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Time { get; set; } = "20:00";
    public int Brightness { get; set; } = 50;
    public string MonitorId { get; set; } = "all";
    public string Event { get; set; } = "time";
    public int OffsetMinutes { get; set; }
    public Dictionary<string, int> IndividualBrightness { get; set; } = new();
}

public sealed class HotkeyBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public uint Modifiers { get; set; } = 6;
    public uint VirtualKey { get; set; } = 0x26;
    public string NativeKey { get; set; } = "";
    public string Action { get; set; } = "increase";
    public string MonitorId { get; set; } = "all";
    public int Step { get; set; } = 5;
    public List<HotkeyAction> Actions { get; set; } = new();
}

public sealed class CalibrationPoint
{
    public double Input { get; set; }
    public double Output { get; set; }
}

public sealed class FeatureSettings
{
    public bool Enabled { get; set; }
    public string Name { get; set; } = "";
    public string IconType { get; set; } = "windows";
    public string IconGlyph { get; set; } = "\uE897";
    public string IconText { get; set; } = "";
    public string IconPath { get; set; } = "";
    public int Min { get; set; }
    public int Max { get; set; } = 100;
    public bool LinkedToBrightness { get; set; }
    public int MaxVisual { get; set; } = 100;
}

public sealed class HotkeyAction
{
    public string Type { get; set; } = "offset";
    public string MonitorId { get; set; } = "all";
    public double Value { get; set; }
    public List<double> Values { get; set; } = new();
    public byte Vcp { get; set; } = 0x10;
    public string ProfileId { get; set; } = "";
    public string Target { get; set; } = "brightness";
}

public sealed class SensorSettings
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "windows";
    public string Endpoint { get; set; } = "http://127.0.0.1:4444";
    public string HardwareId { get; set; } = "";
    public int PollSeconds { get; set; } = 2;
    public double FakeLux { get; set; } = 50;
    public Dictionary<string, SensorMonitorSettings> Monitors { get; set; } = new();
}

public sealed class SensorMonitorSettings
{
    public bool Enabled { get; set; }
    public double MinLux { get; set; } = 5;
    public double MaxLux { get; set; } = 250;
    public int MinBrightness { get; set; }
    public int MaxBrightness { get; set; } = 100;
}

public sealed class AppProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; }
    public bool RestorePrevious { get; set; } = true;
    public bool ShowInTray { get; set; } = true;
    public string OverlayType { get; set; } = "normal";
    public Dictionary<string, int> Brightness { get; set; } = new();
}
