namespace TwinkleTray.Core;

/// <summary>Returns a validated copy, leaving the editable settings object unchanged.</summary>
public static class SettingsNormalizer
{
    public static AppSettings Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AppSettings
        {
            Theme = settings.Theme?.ToLowerInvariant() is "light" or "dark" ? settings.Theme.ToLowerInvariant() : "system",
            Language = string.IsNullOrWhiteSpace(settings.Language) ? "system" : settings.Language.Trim(),
            LinkedBrightness = settings.LinkedBrightness,
            RunAtStartup = settings.RunAtStartup,
            ScrollStep = Math.Clamp(settings.ScrollStep, 1, 100),
            IdleEnabled = settings.IdleEnabled,
            IdleMinutes = Math.Clamp(settings.IdleMinutes, 1, 1440),
            IdleBrightness = Math.Clamp(settings.IdleBrightness, 0, 100),
            Monitors = (settings.Monitors ?? new()).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => NormalizeMonitor(pair.Value)),
            Schedule = (settings.Schedule ?? new()).Where(entry => entry is not null).Select(entry => new ScheduleEntry
            {
                Id = NormalizeId(entry.Id),
                Enabled = entry.Enabled,
                Time = entry.Time ?? "",
                Brightness = Math.Clamp(entry.Brightness, 0, 100),
                MonitorId = NormalizeTarget(entry.MonitorId)
            }).ToList(),
            Hotkeys = (settings.Hotkeys ?? new()).Where(binding => binding is not null).Select(binding => new HotkeyBinding
            {
                Id = NormalizeId(binding.Id),
                Enabled = binding.Enabled && binding.Action is "increase" or "decrease" or "power" && binding.VirtualKey is > 0 and <= 255,
                Modifiers = binding.Modifiers & 0x000F,
                VirtualKey = binding.VirtualKey,
                Action = binding.Action ?? "",
                MonitorId = NormalizeTarget(binding.MonitorId),
                Step = Math.Clamp(binding.Step, 1, 100)
            }).ToList()
        };
    }

    private static MonitorSettings NormalizeMonitor(MonitorSettings monitor)
    {
        var (min, max) = BrightnessMath.NormalizeRange(monitor.MinBrightness, monitor.MaxBrightness);
        return new MonitorSettings
        {
            Name = monitor.Name ?? "",
            Order = monitor.Order,
            Hidden = monitor.Hidden,
            MinBrightness = min,
            MaxBrightness = max,
            ShowContrast = monitor.ShowContrast
        };
    }

    private static string NormalizeId(string? id) => string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
    private static string NormalizeTarget(string? target) => string.IsNullOrWhiteSpace(target) ? "all" : target;
}
