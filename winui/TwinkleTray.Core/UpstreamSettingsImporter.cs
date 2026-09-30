using System.Globalization;
using System.Text.Json;

namespace TwinkleTray.Core;

public sealed record SettingsImportResult(AppSettings Settings, IReadOnlyList<string> UnmappedMonitorIds, IReadOnlyList<string> Warnings)
{
    public bool HasUnresolved => UnmappedMonitorIds.Count > 0 || Warnings.Count > 0;
}

/// <summary>Pure migration of Electron settings.json. Does not read, back up, or write any file.</summary>
public static class UpstreamSettingsImporter
{
    public static SettingsImportResult Import(string json, IReadOnlyDictionary<string, string>? monitorIdentityMap = null, string? knownDisplaysJson = null)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Upstream settings must be a JSON object.");
        var result = new ImportContext(document.RootElement, json, monitorIdentityMap).Run();
        return knownDisplaysJson is null ? result : ImportKnownDisplays(result.Settings, knownDisplaysJson, monitorIdentityMap);
    }

    /// <summary>Merges Electron's separate known-displays JSON into a validated copy. Never reads files or writes hardware.</summary>
    public static SettingsImportResult ImportKnownDisplays(AppSettings current, string json, IReadOnlyDictionary<string, string>? monitorIdentityMap = null)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Known displays must be a JSON object.");
        var settings = SettingsNormalizer.Normalize(current);
        settings.ImportedKnownDisplaysJson = json;
        var mappings = new Dictionary<string, string>(monitorIdentityMap ?? settings.ImportIdentityMap, StringComparer.OrdinalIgnoreCase);
        settings.ImportIdentityMap = mappings;
        var unresolved = settings.ImportUnmappedMonitorIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings = settings.ImportWarnings.ToList();
        var candidates = new List<(string Source, string Target, double Value)>();
        foreach (var pair in document.RootElement.EnumerateObject())
        {
            if (pair.Value.ValueKind != JsonValueKind.Object) { warnings.Add($"Known display '{pair.Name}' is not an object; it was retained without activating a brightness value."); continue; }
            var aliases = new[] { pair.Name, Text(Property(pair.Value, "id")), Text(Property(pair.Value, "key")) }.Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var targets = aliases.Where(mappings.ContainsKey).Select(id => mappings[id]).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (targets.Length > 1) { warnings.Add($"Known display '{pair.Name}' has conflicting alias mappings and was not activated."); continue; }
            string target;
            if (targets.Length == 0) { unresolved.Add(pair.Name); target = "unresolved:" + pair.Name; }
            else target = targets[0];
            var value = Property(pair.Value, "brightness");
            if (settings.Monitors.GetValueOrDefault(target)?.MainControl == "sdr" && Property(pair.Value, "sdrLevel").ValueKind != JsonValueKind.Undefined)
                value = Property(pair.Value, "sdrLevel");
            if (!double.TryParse(Text(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var level) || !double.IsFinite(level) || level is < 0 or > 100)
            { warnings.Add($"Known display '{pair.Name}' has no valid 0–100 brightness; its source data was retained."); continue; }
            candidates.Add((pair.Name, target, level));
        }
        foreach (var group in candidates.GroupBy(item => item.Target, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1)
            {
                warnings.Add($"Multiple known display records map to '{group.Key}' ({string.Join(", ", group.Select(item => item.Source))}); none of their levels were activated.");
                continue;
            }
            settings.LastBrightness[group.Key] = group.Single().Value;
        }
        if (unresolved.Count > 0) warnings.Add("Unmapped known-display levels are retained under inert 'unresolved:' identifiers; map the display IDs and re-import to restore them.");
        settings.ImportUnmappedMonitorIds = unresolved.Order(StringComparer.OrdinalIgnoreCase).ToList();
        settings.ImportWarnings = warnings.Distinct().ToList();
        return new(settings, settings.ImportUnmappedMonitorIds, settings.ImportWarnings);
    }

    private sealed class ImportContext(JsonElement root, string source, IReadOnlyDictionary<string, string>? identityMap)
    {
        private readonly AppSettings settings = new() { ImportedUpstreamJson = source };
        private readonly Dictionary<string, string> identities = new(identityMap ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> unresolved = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> warnings = new();
        private readonly HashSet<string> consumed = new(StringComparer.OrdinalIgnoreCase);

        public SettingsImportResult Run()
        {
            settings.Theme = Text(Get("theme"), "system") switch { "default" => "system", var value => value };
            settings.Language = Text(Get("language"), "system");
            settings.LinkedBrightness = Bool(Get("linkedLevelsActive"));
            settings.RunAtStartup = Bool(Get("openAtLogin"));
            settings.RestoreBrightnessAtStartup = Bool(Get("brightnessAtStartup"));
            settings.CheckScheduleAtStartup = Bool(Get("checkTimeAtStartup"), true);
            settings.ScrollStep = Integer(Get("scrollFlyoutAmount"), 5);
            settings.TrayScrollEnabled = Bool(Get("scrollShortcut"), true);
            settings.TrayScrollStep = Integer(Get("scrollShortcutAmount"), 2);
            settings.InvertScroll = Bool(Get("invertScroll"));
            settings.HotkeysBreakLinkedLevels = Bool(Get("hotkeysBreakLinkedLevels"), true);
            settings.WindowsStyle = Text(Get("windowsStyle"), "system");
            settings.UseAcrylic = Bool(Get("useAcrylic"), true);
            settings.TrayIcon = Text(Get("icon"), "fluent");
            settings.PollBrightness = Bool(Get("getDDCBrightnessUpdates"));
            settings.IdleEnabled = Bool(Get("detectIdleTimeEnabled"));
            settings.IdleMinutes = Integer(Get("detectIdleTimeMinutes"), 10);
            settings.IdleSeconds = Integer(Get("detectIdleTimeSeconds"));
            if (Get("detectIdleTime") is { ValueKind: JsonValueKind.Number } legacyIdle)
            {
                var seconds = Integer(legacyIdle); settings.IdleEnabled = seconds > 0;
                settings.IdleMinutes = seconds / 60; settings.IdleSeconds = seconds % 60;
            }
            // Upstream's idle operation dims to zero; it has no idle-brightness setting.
            settings.IdleBrightness = 0;
            settings.IdleCheckFullscreen = Bool(Get("detectIdleCheckFullscreen"));
            settings.IdleCheckMedia = Bool(Get("detectIdleMedia"));
            settings.IdleRestoreSeconds = Integer(Get("idleRestoreSeconds"));
            settings.WakeRestoreSeconds = Integer(Get("wakeRestoreSeconds"));
            settings.HardwareRestoreSeconds = Integer(Get("hardwareRestoreSeconds"), 2);
            settings.UpdateIntervalMilliseconds = Integer(Get("updateInterval"), 90);
            settings.VcpReadDelayMilliseconds = Integer(Get("checkVCPWaitMS"));
            settings.DisableOnLockScreen = Bool(Get("disableOnLockScreen"));
            settings.DisableAutoRefresh = Bool(Get("disableAutoRefresh"));
            settings.DisableAutoApply = Bool(Get("disableAutoApply"));
            settings.HideClosedLid = Bool(Get("hideClosedLid"));
            settings.ThemeNotifications = !Bool(Get("disableThemeChanges"));
            settings.PowerNotifications = !Bool(Get("disablePowerNotifications"));
            settings.DisableWmi = Bool(Get("disableWMI")) | Bool(Get("disableWMIC"));
            settings.DisableDdc = Bool(Get("disableWin32"));
            settings.DisableAppleStudio = Bool(Get("disableAppleStudio"));
            settings.Logging = Bool(Get("logging"), true);
            settings.UseSoftwareBrightnessFallback = Bool(Get("useSoftwareBrightnessFallback"));
            settings.PowerOffValue = Integer(Get("ddcPowerOffValue"), 5);
            settings.PowerOffMode = Text(Get("sleepAction"), "ddcci") switch { "ps" => "windows", "ps_ddcci" => "both", "none" => "none", _ => "ddc" };
            settings.CheckForUpdates = Bool(Get("checkForUpdates"));
            settings.UpdateChannel = Text(Get("branch"), "master") == "beta" ? "beta" : "stable";
            settings.UdpEnabled = Bool(Get("udpEnabled")); settings.UdpRemote = Bool(Get("udpRemote"));
            settings.UdpPort = Integer(Get("udpPortStart"), 14715);
            settings.UdpKey = Text(Get("udpKey"), settings.UdpKey);
            settings.Latitude = Number(Get("adjustmentTimeLatitude"));
            settings.Longitude = Number(Get("adjustmentTimeLongitude"));
            settings.ScheduleInterpolation = Bool(Get("adjustmentTimeAnimate"));
            var speed = Text(Get("adjustmentTimeSpeed"), "normal");
            settings.SmoothTransitions = speed != "instant";
            settings.TransitionSeconds = speed switch { "instant" => 0, "fastest" => 2, "fast" => 4, "slow" => 40, "slowest" => 100, _ => 10 };
            if (speed != "instant") warnings.Add("Transition speed was approximated in seconds; Electron uses brightness-dependent step timing.");
            var overlay = Text(Get("defaultOverlayType"), "safe"); settings.ShowOverlay = overlay != "disabled";
            settings.OverlayPolicy = overlay == "aggressive" ? "aggressive" : "safe";
            ImportMonitors(); ImportSchedules(); ImportHotkeys(); ImportProfiles(); ImportSensor();
            settings.ImportIdentityMap = identities;
            settings.ImportUnmappedMonitorIds = unresolved.Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (unresolved.Count > 0) warnings.Add("Unmapped monitor references are retained under inert 'unresolved:' identifiers. Supply explicit identity mappings and re-import to activate them.");
            var metadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "isDev", "settingsVer", "settingsBuild", "uuid", "userClosedIntro", "analytics", "dismissedUpdate", "udpPortActive", "lastDetectedDDCCIMethod", "hotkeysPre1160", "killWhenIdle", "isWin11", "enableSunValley" };
            var unhandled = root.EnumerateObject().Where(p => !consumed.Contains(p.Name) && !metadata.Contains(p.Name)).Select(p => p.Name).ToArray();
            if (unhandled.Length > 0) warnings.Add("Fields retained only in ImportedUpstreamJson (not activated): " + string.Join(", ", unhandled));
            settings.ImportWarnings = warnings.Distinct().ToList();
            return new(SettingsNormalizer.Normalize(settings), settings.ImportUnmappedMonitorIds, settings.ImportWarnings);
        }

        private void ImportMonitors()
        {
            foreach (var pair in Properties(Get("names"))) Monitor(pair.Name).Name = Text(pair.Value);
            foreach (var pair in Properties(Get("remaps")))
            {
                var monitor = Monitor(pair.Name);
                monitor.MinBrightness = Integer(Property(pair.Value, "min")); monitor.MaxBrightness = Integer(Property(pair.Value, "max"), 100);
                monitor.Calibration = Items(Property(pair.Value, "calibration")).Select(p => new CalibrationPoint { Input = Number(Property(p, "input")), Output = Number(Property(p, "output")) }).ToList();
            }
            var order = Items(Get("order")).ToArray();
            var orderedIds = order.All(item => item.ValueKind == JsonValueKind.String)
                ? order.Select(item => Text(item))
                : order.OrderBy(item => Integer(Property(item, "order"))).Select(item => Text(Property(item, "id")));
            int orderIndex = 0;
            foreach (var id in orderedIds.Where(id => id.Length > 0).Distinct()) Monitor(id).Order = orderIndex++;
            foreach (var pair in Properties(Get("hideDisplays"))) Monitor(pair.Name).Hidden = Bool(pair.Value);
            foreach (var pair in Properties(Get("hdrDisplays"))) Monitor(pair.Name).ForceHdr = Bool(pair.Value);
            foreach (var pair in Properties(Get("sdrAsMainSliderDisplays"))) if (Bool(pair.Value)) Monitor(pair.Name).MainControl = "sdr";
            foreach (var pair in Properties(Get("gammaAsMainSliderDisplays"))) if (Bool(pair.Value)) Monitor(pair.Name).MainControl = "gamma";
            foreach (var pair in Properties(Get("extendMinimumDisplays"))) Monitor(pair.Name).ExtendMinimum = Bool(pair.Value);
            foreach (var pair in Properties(Get("extendMinimumBreakpoints"))) Monitor(pair.Name).ExtendMinimumBreakpoint = Integer(pair.Value, 20);
            foreach (var pair in Properties(Get("userDDCBrightnessVCPs")))
            {
                if (TryVcp(Text(pair.Value), out var code)) Monitor(pair.Name).BrightnessVcp = code;
                else warnings.Add($"Unrecognized brightness VCP for '{pair.Name}' was retained in source JSON.");
            }
            foreach (var item in Items(Get("userSkipReapply"))) if (Text(item).Length > 0) Monitor(Text(item)).SkipRestore = true;
            foreach (var model in Properties(Get("monitorFeatures")))
                foreach (var feature in Properties(model.Value))
                    if (TryVcp(feature.Name, out var code)) Feature(Monitor(model.Name), code).Enabled = Bool(feature.Value);
                    else warnings.Add($"Unknown feature '{feature.Name}' for '{model.Name}' was retained in source JSON.");
            foreach (var model in Properties(Get("monitorFeaturesSettings")))
                foreach (var feature in Properties(model.Value))
                {
                    if (!TryVcp(feature.Name, out var code)) continue;
                    var target = Feature(Monitor(model.Name), code);
                    target.Name = Text(Property(feature.Value, "name"));
                    target.IconType = Text(Property(feature.Value, "iconType"), "windows");
                    target.IconText = Text(Property(feature.Value, "iconText"));
                    target.IconPath = Text(Property(feature.Value, "iconPath"));
                    string icon = Text(Property(feature.Value, "icon"), "E897");
                    target.IconGlyph = int.TryParse(icon, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint) && System.Text.Rune.IsValid(codePoint)
                        ? char.ConvertFromUtf32(codePoint) : icon;
                    if (target.IconType == "text" && target.IconText.Length > 0) target.Name = target.IconText;
                    target.Min = Integer(Property(feature.Value, "min")); target.Max = Integer(Property(feature.Value, "max"), 100);
                    target.MaxVisual = Integer(Property(feature.Value, "maxVisual"), 100); target.LinkedToBrightness = Bool(Property(feature.Value, "linked"));
                }
            foreach (var monitor in settings.Monitors.Values)
            {
                monitor.ShowContrast = monitor.Features.TryGetValue(0x12, out var contrast) && contrast.Enabled;
                monitor.SoftwareFallback = settings.UseSoftwareBrightnessFallback;
            }
        }

        private void ImportSchedules()
        {
            bool individual = Bool(Get("adjustmentTimeIndividualDisplays"));
            foreach (var item in Items(Get("adjustmentTimes")))
            {
                var entry = new ScheduleEntry
                {
                    Id = Text(Property(item, "id"), Guid.NewGuid().ToString("N")),
                    Enabled = Bool(Property(item, "enabled"), true), Time = Text(Property(item, "time")),
                    Event = Bool(Property(item, "useSunCalc")) ? Text(Property(item, "sunCalc"), "sunrise") : "time",
                    Brightness = Integer(Property(item, "brightness"), 50), OffsetMinutes = Integer(Property(item, "offsetMinutes"))
                };
                if (entry.Time.Length == 0 && Property(item, "hour").ValueKind != JsonValueKind.Undefined)
                {
                    int hour = Integer(Property(item, "hour"));
                    if (Text(Property(item, "am"), "am").Equals("pm", StringComparison.OrdinalIgnoreCase)) hour = hour % 12 + 12;
                    else hour %= 12;
                    entry.Time = $"{hour:00}:{Integer(Property(item, "minute")):00}";
                }
                if (individual)
                {
                    foreach (var pair in Properties(Property(item, "monitors")))
                        if (Number(pair.Value, -1) >= 0) entry.IndividualBrightness[Resolve(pair.Name)] = Integer(pair.Value);
                    if (entry.IndividualBrightness.Count == 0) { entry.Enabled = false; warnings.Add("An individual-display schedule with no valid targets was disabled."); }
                }
                if (entry.Event == "time" && !TimeOnly.TryParseExact(entry.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                { entry.Enabled = false; warnings.Add("An invalid fixed-time schedule was retained but disabled."); }
                if (entry.Event != "time" && !SolarCalculator.EventNames.Contains(entry.Event, StringComparer.OrdinalIgnoreCase))
                { entry.Enabled = false; warnings.Add($"Unknown solar event '{entry.Event}' was retained but disabled."); }
                settings.Schedule.Add(entry);
            }
        }

        private void ImportHotkeys()
        {
            var hotkeys = Get("hotkeys");
            foreach (var item in hotkeys.ValueKind == JsonValueKind.Object ? hotkeys.EnumerateObject().Select(pair => pair.Value) : Items(hotkeys))
            {
                var binding = new HotkeyBinding { Id = Text(Property(item, "id"), Guid.NewGuid().ToString("N")), Enabled = Bool(Property(item, "enabled"), true) };
                string accelerator = Text(Property(item, "accelerator"));
                uint modifiers = 0, key = 0;
                if (accelerator is "BrightnessUp" or "BrightnessDown") binding.NativeKey = accelerator;
                else if (!TryAccelerator(accelerator, out modifiers, out key))
                { binding.Enabled = false; warnings.Add($"Hotkey '{accelerator}' has an unsupported or empty key; it was retained but disabled."); }
                binding.Modifiers = modifiers; binding.VirtualKey = key;
                var actions = Items(Property(item, "actions")).ToArray();
                if (actions.Length == 0 && Property(item, "monitor").ValueKind != JsonValueKind.Undefined)
                {
                    string target = Text(Property(item, "monitor"));
                    binding.Actions.Add(new HotkeyAction { Type = target == "turn_off_displays" ? "power" : "offset", MonitorId = target is "all" or "turn_off_displays" ? "all" : Resolve(target), Value = Integer(Get("hotkeyPercent"), 10) * Number(Property(item, "direction"), 1) });
                }
                foreach (var action in actions)
                {
                    string type = Text(Property(action, "type"), "offset"); if (type == "off") type = "power";
                    if (type is not ("offset" or "set" or "cycle" or "power" or "profile" or "refresh" or "panel" or "vcp"))
                    { binding.Enabled = false; warnings.Add($"Hotkey action '{type}' requires manual migration; its binding was disabled."); continue; }
                    string target = Text(Property(action, "target"), "brightness"); byte vcp = 0x10;
                    if (target is not ("brightness" or "sdr" or "gamma"))
                    {
                        if (!TryVcp(target, out vcp)) { binding.Enabled = false; warnings.Add($"Hotkey target '{target}' requires manual migration."); }
                        target = "vcp";
                    }
                    var targets = Bool(Property(action, "allMonitors")) || type is "power" or "refresh" or "panel" or "profile"
                        ? new[] { "all" } : Properties(Property(action, "monitors")).Where(pair => Bool(pair.Value)).Select(pair => Resolve(pair.Name)).ToArray();
                    if (targets.Length == 0) { binding.Enabled = false; warnings.Add("A hotkey action with no selected monitors was retained but disabled."); targets = ["unresolved:no-monitor-selected"]; }
                    foreach (var id in targets) binding.Actions.Add(new HotkeyAction
                    {
                        Type = type, MonitorId = id, Target = target, Vcp = vcp,
                        Value = Number(Property(action, "value")), Values = Items(Property(action, "values")).Select(value => Number(value)).ToList(),
                        ProfileId = Text(Property(action, "profileId"))
                    });
                }
                if (binding.Actions.Count == 0) binding.Enabled = false;
                settings.Hotkeys.Add(binding);
            }
        }

        private void ImportProfiles()
        {
            foreach (var item in Items(Get("profiles")))
            {
                var profile = new AppProfile
                {
                    Id = Text(Property(item, "uuid"), Text(Property(item, "id"), Guid.NewGuid().ToString("N"))),
                    Name = Text(Property(item, "name")), Path = Text(Property(item, "path")), Enabled = Bool(Property(item, "enabled"), true),
                    ShowInTray = Bool(Property(item, "showInMenu")), RestorePrevious = Bool(Property(item, "restorePrevious"), true),
                    OverlayType = Text(Property(item, "overlayType"), "normal")
                };
                if (Bool(Property(item, "setBrightness")))
                    foreach (var pair in Properties(Property(item, "monitors"))) profile.Brightness[Resolve(pair.Name)] = Integer(pair.Value);
                settings.Profiles.Add(profile);
            }
        }

        private void ImportSensor()
        {
            var sensor = Get("lightSensor"); if (sensor.ValueKind != JsonValueKind.Object) return;
            settings.Sensor.Enabled = Bool(Property(sensor, "enabled"));
            settings.Sensor.Provider = Text(Property(sensor, "active"), "windows");
            settings.Sensor.PollSeconds = Integer(Property(sensor, "sensorPollingInterval"), 10);
            var providers = Property(sensor, "sensors");
            settings.Sensor.FakeLux = Number(Property(Property(providers, "fake"), "overriddenLux"), 50);
            string endpoint = Text(Property(Property(providers, "yocto"), "hubUrl"), "http://127.0.0.1:4444");
            if (!endpoint.Contains("://", StringComparison.Ordinal)) endpoint = "http://" + endpoint;
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var hub) && hub.IsDefaultPort) endpoint = new UriBuilder(hub) { Port = 4444 }.Uri.ToString();
            settings.Sensor.Endpoint = endpoint;
            foreach (var pair in Properties(Property(sensor, "monitorSettings"))) settings.Sensor.Monitors[Resolve(pair.Name)] = new SensorMonitorSettings
            {
                Enabled = Bool(Property(pair.Value, "enabled")), MinLux = Number(Property(pair.Value, "minLux"), 5), MaxLux = Number(Property(pair.Value, "maxLux"), 250)
            };
        }

        private JsonElement Get(string name) { consumed.Add(name); return Property(root, name); }
        private string Resolve(string upstreamId)
        {
            if (identities.TryGetValue(upstreamId, out var id) && !string.IsNullOrWhiteSpace(id)) return id;
            unresolved.Add(upstreamId); return "unresolved:" + upstreamId;
        }
        private MonitorSettings Monitor(string upstreamId)
        {
            string id = Resolve(upstreamId);
            if (!settings.Monitors.TryGetValue(id, out var monitor)) settings.Monitors[id] = monitor = new MonitorSettings();
            return monitor;
        }
        private static FeatureSettings Feature(MonitorSettings monitor, byte code)
        {
            if (!monitor.Features.TryGetValue(code, out var feature)) monitor.Features[code] = feature = new FeatureSettings();
            return feature;
        }
    }

    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    private static IEnumerable<JsonProperty> Properties(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value.EnumerateObject() : [];
    private static IEnumerable<JsonElement> Items(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    private static string Text(JsonElement value, string fallback = "") => value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : fallback;
    private static double Number(JsonElement value, double fallback = 0)
    {
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1) return Number(value[0], fallback);
        return double.TryParse(Text(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : fallback;
    }
    private static int Integer(JsonElement value, int fallback = 0) => (int)Math.Clamp(Number(value, fallback), int.MinValue, int.MaxValue);
    private static bool Bool(JsonElement value, bool fallback = false) => value.ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Number => Number(value) != 0,
        JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : fallback,
        _ => fallback
    };

    private static bool TryVcp(string value, out byte code)
    {
        value = value switch { "contrast" => "0x12", "volume" => "0x62", "powerState" => "0xD6", "brightness" => "0x10", _ => value };
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)
            : byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
    }

    public static bool TryAccelerator(string accelerator, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0; virtualKey = 0;
        foreach (var token in accelerator.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var upper = token.ToUpperInvariant();
            if (upper is "CTRL" or "CONTROL" or "CMDORCTRL" or "COMMANDORCONTROL") { modifiers |= 2; continue; }
            if (upper is "SHIFT") { modifiers |= 4; continue; }
            if (upper is "ALT" or "OPTION") { modifiers |= 1; continue; }
            if (upper is "SUPER" or "META" or "WIN" or "COMMAND" or "CMD") { modifiers |= 8; continue; }
            uint key;
            if (upper.Length == 1 && char.IsAsciiLetterOrDigit(upper[0])) key = upper[0];
            else if (upper.StartsWith('F') && int.TryParse(upper.AsSpan(1), out var function) && function is >= 1 and <= 24) key = (uint)(0x6F + function);
            else key = upper switch
            {
                "UP" or "ARROWUP" => 0x26, "DOWN" or "ARROWDOWN" => 0x28, "LEFT" or "ARROWLEFT" => 0x25, "RIGHT" or "ARROWRIGHT" => 0x27,
                "SPACE" => 0x20, "TAB" => 0x09, "ENTER" or "RETURN" => 0x0D, "ESC" or "ESCAPE" => 0x1B,
                "BACKSPACE" => 0x08, "DELETE" or "DEL" => 0x2E, "INSERT" => 0x2D, "HOME" => 0x24, "END" => 0x23,
                "PAGEUP" => 0x21, "PAGEDOWN" => 0x22, "VOLUMEUP" => 0xAF, "VOLUMEDOWN" => 0xAE, "VOLUMEMUTE" => 0xAD,
                "MEDIANEXTTRACK" => 0xB0, "MEDIAPREVIOUSTRACK" => 0xB1, "MEDIASTOP" => 0xB2, "MEDIAPLAYPAUSE" => 0xB3,
                "PLUS" or "=" => 0xBB, "MINUS" or "-" => 0xBD, "," => 0xBC, "." => 0xBE, "/" => 0xBF,
                _ => 0
            };
            if (key == 0 || virtualKey != 0) { virtualKey = 0; return false; }
            virtualKey = key;
        }
        return virtualKey != 0;
    }
}
