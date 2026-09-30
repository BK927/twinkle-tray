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
            IdleMinutes = Math.Clamp(settings.IdleMinutes, settings.IdleSeconds > 0 ? 0 : 1, 1440),
            IdleBrightness = Math.Clamp(settings.IdleBrightness, 0, 100),
            RestoreBrightnessAtStartup = settings.RestoreBrightnessAtStartup,
            CheckScheduleAtStartup = settings.CheckScheduleAtStartup,
            SmoothTransitions = settings.SmoothTransitions,
            ScheduleInterpolation = settings.ScheduleInterpolation,
            TransitionSeconds = Math.Clamp(settings.TransitionSeconds, 0, 3600),
            Latitude = FiniteClamp(settings.Latitude, -90, 90),
            Longitude = FiniteClamp(settings.Longitude, -180, 180),
            HotkeysBreakLinkedLevels = settings.HotkeysBreakLinkedLevels,
            ShowOverlay = settings.ShowOverlay,
            OverlayTimeoutSeconds = Math.Clamp(settings.OverlayTimeoutSeconds, 1, 60),
            OverlayPolicy = settings.OverlayPolicy == "aggressive" ? "aggressive" : "safe",
            WindowsStyle = settings.WindowsStyle is "win10" or "win11" ? settings.WindowsStyle : "system",
            UseAcrylic = settings.UseAcrylic,
            InvertScroll = settings.InvertScroll,
            TrayScrollEnabled = settings.TrayScrollEnabled,
            TrayScrollStep = Math.Clamp(settings.TrayScrollStep, 1, 100),
            TrayIcon = settings.TrayIcon is "icon" or "mdl2" or "fluent" ? settings.TrayIcon : "fluent",
            PollBrightness = settings.PollBrightness,
            PollSeconds = Math.Clamp(settings.PollSeconds, 1, 3600),
            DisableOnLockScreen = settings.DisableOnLockScreen,
            IdleSeconds = Math.Clamp(settings.IdleSeconds, 0, 59),
            IdleCheckFullscreen = settings.IdleCheckFullscreen,
            IdleCheckMedia = settings.IdleCheckMedia,
            IdleRestoreSeconds = Math.Clamp(settings.IdleRestoreSeconds, 0, 3600),
            WakeRestoreSeconds = Math.Clamp(settings.WakeRestoreSeconds, 0, 3600),
            HardwareRestoreSeconds = Math.Clamp(settings.HardwareRestoreSeconds, 0, 60),
            UpdateIntervalMilliseconds = Math.Clamp(settings.UpdateIntervalMilliseconds, 16, 5000),
            VcpReadDelayMilliseconds = Math.Clamp(settings.VcpReadDelayMilliseconds, 0, 1000),
            DisableAutoRefresh = settings.DisableAutoRefresh,
            DisableAutoApply = settings.DisableAutoApply,
            HideClosedLid = settings.HideClosedLid,
            ThemeNotifications = settings.ThemeNotifications,
            PowerNotifications = settings.PowerNotifications,
            DisableWmi = settings.DisableWmi,
            DisableDdc = settings.DisableDdc,
            DisableAppleStudio = settings.DisableAppleStudio,
            Logging = settings.Logging,
            UseSoftwareBrightnessFallback = settings.UseSoftwareBrightnessFallback,
            PowerOffValue = Math.Clamp(settings.PowerOffValue, 1, 5),
            PowerOffMode = settings.PowerOffMode is "windows" or "both" or "none" ? settings.PowerOffMode : "ddc",
            CheckForUpdates = settings.CheckForUpdates,
            UpdateChannel = settings.UpdateChannel is "beta" ? "beta" : "stable",
            UdpEnabled = settings.UdpEnabled,
            UdpRemote = settings.UdpRemote,
            UdpPort = Math.Clamp(settings.UdpPort, 1, 65535),
            UdpKey = string.IsNullOrWhiteSpace(settings.UdpKey) ? Guid.NewGuid().ToString("N") : settings.UdpKey,
            Sensor = NormalizeSensor(settings.Sensor ?? new()),
            Profiles = (settings.Profiles ?? new()).Where(profile => profile is not null).Select(profile => new AppProfile
            {
                Id = NormalizeId(profile.Id), Name = profile.Name ?? "", Path = profile.Path ?? "",
                Enabled = profile.Enabled, RestorePrevious = profile.RestorePrevious, ShowInTray = profile.ShowInTray,
                OverlayType = profile.OverlayType is "safe" or "disabled" or "aggressive" or "force" ? profile.OverlayType : "normal",
                Brightness = NormalizeLevels(profile.Brightness)
            }).ToList(),
            LastBrightness = (settings.LastBrightness ?? new()).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && double.IsFinite(pair.Value))
                .ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value, 0, 100)),
            ImportIdentityMap = new(settings.ImportIdentityMap ?? new()),
            ImportedUpstreamJson = settings.ImportedUpstreamJson ?? "",
            ImportedKnownDisplaysJson = settings.ImportedKnownDisplaysJson ?? "",
            ImportUnmappedMonitorIds = new(settings.ImportUnmappedMonitorIds ?? new()),
            ImportWarnings = new(settings.ImportWarnings ?? new()),
            Monitors = (settings.Monitors ?? new()).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => NormalizeMonitor(pair.Value)),
            Schedule = (settings.Schedule ?? new()).Where(entry => entry is not null).Select(entry => new ScheduleEntry
            {
                Id = NormalizeId(entry.Id),
                Enabled = entry.Enabled,
                Time = entry.Time ?? "",
                Brightness = Math.Clamp(entry.Brightness, 0, 100),
                MonitorId = NormalizeTarget(entry.MonitorId),
                Event = entry.Event ?? "time",
                OffsetMinutes = Math.Clamp(entry.OffsetMinutes, -1440, 1440),
                IndividualBrightness = NormalizeLevels(entry.IndividualBrightness)
            }).ToList(),
            Hotkeys = (settings.Hotkeys ?? new()).Where(binding => binding is not null).Select(binding => new HotkeyBinding
            {
                Id = NormalizeId(binding.Id),
                Enabled = binding.Enabled && (binding.Actions?.Count > 0 ? binding.Actions.All(IsValidAction) : binding.Action is "increase" or "decrease" or "power") &&
                    (binding.NativeKey is "BrightnessUp" or "BrightnessDown" || string.IsNullOrEmpty(binding.NativeKey) && binding.VirtualKey is > 0 and <= 255),
                Modifiers = binding.Modifiers & 0x000F,
                VirtualKey = binding.VirtualKey,
                NativeKey = binding.NativeKey is "BrightnessUp" or "BrightnessDown" ? binding.NativeKey : "",
                Action = binding.Action ?? "",
                MonitorId = NormalizeTarget(binding.MonitorId),
                Step = Math.Clamp(binding.Step, 1, 100),
                Actions = (binding.Actions ?? new()).Where(action => action is not null).Select(action => new HotkeyAction
                {
                    Type = action.Type ?? "", MonitorId = NormalizeTarget(action.MonitorId),
                    Value = FiniteClamp(action.Value, -65535, 65535),
                    Values = (action.Values ?? new()).Where(double.IsFinite).Select(value => Math.Clamp(value, 0, 65535)).ToList(),
                    Vcp = action.Vcp, ProfileId = action.ProfileId ?? "", Target = action.Target ?? "brightness"
                }).ToList()
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
            ShowContrast = monitor.ShowContrast,
            ShowName = monitor.ShowName,
            ShowValue = monitor.ShowValue,
            IconGlyph = monitor.IconGlyph ?? "",
            MainControl = monitor.MainControl is "gamma" or "sdr" ? monitor.MainControl : "brightness",
            SoftwareFallback = monitor.SoftwareFallback,
            ExtendMinimum = monitor.ExtendMinimum,
            ExtendMinimumBreakpoint = Math.Clamp(monitor.ExtendMinimumBreakpoint, 1, 90),
            Calibration = (monitor.Calibration ?? new()).Where(point => point is not null && double.IsFinite(point.Input) && double.IsFinite(point.Output))
                .Select(point => new CalibrationPoint { Input = Math.Clamp(point.Input, 0, 100), Output = Math.Clamp(point.Output, 0, 100) }).ToList(),
            BrightnessVcp = monitor.BrightnessVcp,
            Features = (monitor.Features ?? new()).Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => new FeatureSettings
            {
                Enabled = pair.Value.Enabled, Name = pair.Value.Name ?? "",
                IconType = pair.Value.IconType is "text" or "image" ? pair.Value.IconType : "windows",
                IconGlyph = pair.Value.IconGlyph ?? "\uE897", IconText = pair.Value.IconText ?? "", IconPath = pair.Value.IconPath ?? "",
                Min = Math.Clamp(Math.Min(pair.Value.Min, pair.Value.Max), 0, 65535),
                Max = Math.Clamp(Math.Max(pair.Value.Min, pair.Value.Max), 0, 65535),
                LinkedToBrightness = pair.Value.LinkedToBrightness,
                MaxVisual = Math.Clamp(pair.Value.MaxVisual, 1, 100)
            }),
            SkipRestore = monitor.SkipRestore, ForceHdr = monitor.ForceHdr
        };
    }

    private static string NormalizeId(string? id) => string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
    private static bool IsValidAction(HotkeyAction? action) => action is not null &&
        action.Type is "offset" or "set" or "cycle" or "power" or "profile" or "refresh" or "panel" or "vcp" &&
        action.Target is "brightness" or "gamma" or "sdr" or "vcp" && double.IsFinite(action.Value) &&
        (action.Type != "cycle" || action.Values is { Count: > 0 } && action.Values.All(double.IsFinite));
    private static string NormalizeTarget(string? target) => string.IsNullOrWhiteSpace(target) ? "all" : target;
    private static double FiniteClamp(double value, double min, double max) => double.IsFinite(value) ? Math.Clamp(value, min, max) : Math.Clamp(0, min, max);
    private static Dictionary<string, int> NormalizeLevels(Dictionary<string, int>? levels) => (levels ?? new())
        .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)).ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value, 0, 100));
    private static SensorSettings NormalizeSensor(SensorSettings sensor) => new()
    {
        Enabled = sensor.Enabled, Provider = sensor.Provider ?? "windows", Endpoint = sensor.Endpoint ?? "http://127.0.0.1:4444",
        HardwareId = sensor.HardwareId ?? "", PollSeconds = Math.Clamp(sensor.PollSeconds, 1, 3600),
        FakeLux = FiniteClamp(sensor.FakeLux, 0, 1000000),
        Monitors = (sensor.Monitors ?? new()).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => new SensorMonitorSettings
            {
                Enabled = pair.Value.Enabled, MinLux = FiniteClamp(pair.Value.MinLux, 0, 1000000), MaxLux = FiniteClamp(pair.Value.MaxLux, 0, 1000000),
                MinBrightness = Math.Clamp(pair.Value.MinBrightness, 0, 100), MaxBrightness = Math.Clamp(pair.Value.MaxBrightness, 0, 100)
            })
    };
}
