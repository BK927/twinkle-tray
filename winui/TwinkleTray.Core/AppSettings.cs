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
}

public sealed class MonitorSettings
{
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public bool Hidden { get; set; }
    public int MinBrightness { get; set; }
    public int MaxBrightness { get; set; } = 100;
    public bool ShowContrast { get; set; }
}

public sealed class ScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Time { get; set; } = "20:00";
    public int Brightness { get; set; } = 50;
    public string MonitorId { get; set; } = "all";
}

public sealed class HotkeyBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public uint Modifiers { get; set; } = 6;
    public uint VirtualKey { get; set; } = 0x26;
    public string Action { get; set; } = "increase";
    public string MonitorId { get; set; } = "all";
    public int Step { get; set; } = 5;
}
