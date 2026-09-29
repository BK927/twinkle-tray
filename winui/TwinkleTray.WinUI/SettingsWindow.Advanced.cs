using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TwinkleTray.Core;
using TwinkleTray.Hardware;
using TwinkleTray.WinUI.Services;

namespace TwinkleTray.WinUI;

public sealed partial class SettingsWindow
{
    private readonly Dictionary<string, IReadOnlyList<VcpFeature>> _features = new();
    private IReadOnlyList<AmbientLightDevice> _sensorDevices = [];

    private static IEnumerable<(string Value, string Label)> LanguageChoices()
    {
        yield return ("system", T("SETTINGS_GENERAL_LANGUAGE_SYSTEM", "System language (default)"));
        foreach (var language in LocalizationService.AvailableLanguages()) yield return (language.Code, language.Name);
    }

    private void RenderGeneralExtensions()
    {
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_WINDOWS_UI_STYLE_TITLE", "Windows UI Style"), null,
            Choice([("system", T("GENERIC_DEFAULT", "Default")), ("win11", "Windows 11"), ("win10", "Windows 10")], _settings.WindowsStyle, value => { _settings.WindowsStyle = value; ApplyTheme(); Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_ACRYLIC_TITLE", "Acrylic Blur"), T("SETTINGS_GENERAL_ACRYLIC_DESC", "Enable blur behind the brightness panel."),
            Toggle(_settings.UseAcrylic, value => { _settings.UseAcrylic = value; ApplyTheme(); Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_TRAY_ICON_TITLE", "Tray icon"), null,
            Choice([("fluent", "Fluent"), ("mdl2", "MDL2"), ("icon", T("GENERIC_DEFAULT", "Default"))], _settings.TrayIcon, value => { _settings.TrayIcon = value; Save(); })));
        Section(T("NATIVE_BRIGHTNESS_SHORTCUTS", "Brightness and scrolling"));
        PageContent.Children.Add(SettingRow(T("PANEL_BUTTON_LINK_LEVELS", "Link levels"), T("NATIVE_LINK_DESCRIPTION", "Adjust all displays together from the brightness panel."),
            Toggle(_settings.LinkedBrightness, value => { _settings.LinkedBrightness = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("NATIVE_SCROLL_STEP", "Brightness adjustment step"), T("NATIVE_SCROLL_DESCRIPTION", "Brightness change when using the mouse wheel over a slider."),
            Number(_settings.ScrollStep, 1, 100, value => { _settings.ScrollStep = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_SCROLL_TITLE", "Tray icon scroll shortcut"), T("SETTINGS_GENERAL_SCROLL_DESC", "Scroll over the tray icon to change display brightness."),
            Toggle(_settings.TrayScrollEnabled, value => { _settings.TrayScrollEnabled = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_HOTKEYS_SCROLL_AMOUNT", "Amount to scroll"), null,
            Number(_settings.TrayScrollStep, 1, 100, value => { _settings.TrayScrollStep = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_HOTKEYS_INVERT_SCROLL_TITLE", "Invert scroll"), null,
            Toggle(_settings.InvertScroll, value => { _settings.InvertScroll = value; Save(); })));
        Section(T("NATIVE_OVERLAY_SECTION", "Brightness overlay"));
        PageContent.Children.Add(SettingRow(T("NATIVE_SHOW_OVERLAY", "Show brightness overlay"), null,
            Toggle(_settings.ShowOverlay, value => { _settings.ShowOverlay = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("NATIVE_OVERLAY_TIMEOUT", "Overlay duration (seconds)"), null,
            Number(_settings.OverlayTimeoutSeconds, 1, 60, value => { _settings.OverlayTimeoutSeconds = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_OVERLAY_TITLE", "Default overlay behavior"), T("SETTINGS_GENERAL_OVERLAY_DESC", "Choose how the brightness overlay is shown over other apps."),
            Choice([("safe", T("SETTINGS_GENERAL_ON_OVERLAY_TITLE", "Most compatible")), ("aggressive", T("SETTINGS_GENERAL_FORCE_OVERLAY_TITLE", "Forced on"))], _settings.OverlayPolicy, value => { _settings.OverlayPolicy = value; Save(); })));
        Section(T("NATIVE_STARTUP_RESTORE", "Startup and restoration"));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_STARTUP", "Launch at startup"), null,
            Toggle(_settings.RunAtStartup, value => { _settings.RunAtStartup = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_BRIGHTNESS_STARTUP_TITLE", "Apply brightness at startup"), T("SETTINGS_GENERAL_BRIGHTNESS_STARTUP_DESC", "Restore the last known brightness for each display when Twinkle Tray starts."),
            Toggle(_settings.RestoreBrightnessAtStartup, value => { _settings.RestoreBrightnessAtStartup = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_DISABLE_ON_LOCK_SCREEN_TITLE", "Disable on Lock Screen"), T("SETTINGS_GENERAL_DISABLE_ON_LOCK_SCREEN_DESC", "Do not access monitors while the user session is locked."),
            Toggle(_settings.DisableOnLockScreen, value => { _settings.DisableOnLockScreen = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_AUTOBRIGHT_TITLE", "Auto-apply brightness"), T("SETTINGS_GENERAL_AUTOBRIGHT_DESC", "Restore known brightness when displays reconnect or wake."),
            Toggle(!_settings.DisableAutoApply, value => { _settings.DisableAutoApply = !value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_SKIP_THEME_CHANGES_TITLE", "Theme update detection"), T("SETTINGS_GENERAL_SKIP_THEME_CHANGES_DESC", "Update appearance when the Windows theme changes."),
            Toggle(_settings.ThemeNotifications, value => { _settings.ThemeNotifications = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_SKIP_POWER_EVENTS_TITLE", "Monitor power state detection"), T("SETTINGS_GENERAL_SKIP_POWER_EVENTS_DESC", "Detect display power and wake events."),
            Toggle(_settings.PowerNotifications, value => { _settings.PowerNotifications = value; Save(); })));
    }

    private void RenderTimeAdvanced()
    {
        Heading(T("SETTINGS_TIME_TITLE", "Time of Day Adjustments"), T("NATIVE_TIME_DESCRIPTION", "Change the selected displays' brightness every day at a specified local time."));
        PageContent.Children.Add(SettingRow(T("SETTINGS_TIME_STARTUP_TITLE", "Check at app startup"), T("SETTINGS_TIME_STARTUP_DESC", "Apply the most relevant scheduled brightness when the app starts."), Toggle(_settings.CheckScheduleAtStartup, value => { _settings.CheckScheduleAtStartup = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_TIME_ANIMATE_TITLE", "Animate between times"), T("SETTINGS_TIME_ANIMATE_DESC", "Interpolate brightness between the current and next scheduled event."), Toggle(_settings.ScheduleInterpolation, value => { _settings.ScheduleInterpolation = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("NATIVE_SMOOTH_TRANSITIONS", "Smooth brightness transitions"), null, Toggle(_settings.SmoothTransitions, value => { _settings.SmoothTransitions = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("NATIVE_TRANSITION_SECONDS", "Transition duration (seconds)"), null, Number(_settings.TransitionSeconds, 0, 3600, value => { _settings.TransitionSeconds = value; Save(); })));
        var location = new StackPanel { Spacing = 12 };
        location.Children.Add(Description(T("SETTINGS_TIME_SUN_DESC", "Enter latitude and longitude to schedule adjustments by sun position.")));
        var latitude = DecimalNumber(_settings.Latitude, -90, 90, value => { _settings.Latitude = value; Save(); });
        latitude.Header = T("SETTINGS_TIME_LAT", "Latitude");
        var longitude = DecimalNumber(_settings.Longitude, -180, 180, value => { _settings.Longitude = value; Save(); });
        longitude.Header = T("SETTINGS_TIME_LONG", "Longitude");
        location.Children.Add(FieldGrid(latitude, longitude));
        if (_actions.GetCoordinatesAsync is not null)
            location.Children.Add(ActionButton(T("SETTINGS_TIME_SUN_GET", "Get coordinates"), async () =>
            {
                var coordinates = await _actions.GetCoordinatesAsync();
                _settings.Latitude = Math.Clamp(coordinates.Latitude, -90, 90);
                _settings.Longitude = Math.Clamp(coordinates.Longitude, -180, 180);
                Save(); RenderPage();
            }));
        PageContent.Children.Add(ExpandableCard("time:coordinates", () => T("SETTINGS_TIME_SUN_TITLE", "Coordinates for sun position"),
            () => $"{_settings.Latitude:0.####}, {_settings.Longitude:0.####}", location));
        var add = AddButton(T("SETTINGS_TIME_ADD", "Add a time"));
        add.Click += (_, _) =>
        {
            var entry = new ScheduleEntry { Id = Guid.NewGuid().ToString("N"), Enabled = true, Time = DateTime.Now.AddHours(1).ToString("HH:mm", CultureInfo.InvariantCulture), Brightness = 50, MonitorId = "all" };
            _settings.Schedule.Add(entry);
            ExpandNewCard("schedule:" + entry.Id);
            Save(); RenderPage();
        };
        PageContent.Children.Add(add);
        if (_settings.Schedule.Count == 0) PageContent.Children.Add(Description(T("NATIVE_NO_SCHEDULE", "No brightness adjustments scheduled. Add a time to get started.")));
        foreach (var schedule in _settings.Schedule.ToList())
        {
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(EditorHeader(schedule.Enabled, value => { schedule.Enabled = value; Save(); }, () => { _settings.Schedule.Remove(schedule); Save(); RenderPage(); }, T("SETTINGS_TIME_REMOVE", "Remove time")));
            var events = new List<(string Value, string Label)> { ("time", T("NATIVE_TIME", "Time")) };
            events.AddRange(SolarCalculator.EventNames.Select(name => (name, SolarEventName(name))));
            body.Children.Add(FieldChoice(T("NATIVE_SCHEDULE_EVENT", "Schedule event"), events, schedule.Event, value => { schedule.Event = value; Save(); RenderPage(); }));
            if (schedule.Event == "time")
            {
                var valid = DateTime.TryParseExact(schedule.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed);
                var warning = new InfoBar { IsOpen = !valid, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = T("NATIVE_INVALID_TIME", "The saved time is invalid. Choose a valid time before enabling this schedule.") };
                body.Children.Add(warning);
                var picker = new TimePicker { Header = T("NATIVE_TIME", "Time"), ClockIdentifier = "24HourClock", Time = valid ? parsed.TimeOfDay : TimeSpan.FromHours(12) };
                picker.TimeChanged += (_, args) => { schedule.Time = args.NewTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture); warning.IsOpen = false; Save(); };
                body.Children.Add(picker);
            }
            else
                body.Children.Add(FieldNumber(T("NATIVE_SOLAR_OFFSET", "Offset from event (minutes)"), schedule.OffsetMinutes, -1440, 1440, value => { schedule.OffsetMinutes = value; Save(); }));
            body.Children.Add(TargetChoice(schedule.MonitorId, value => { schedule.MonitorId = value; Save(); }));
            body.Children.Add(BrightnessEditor(schedule.Brightness, value => { schedule.Brightness = value; Save(); }));
            body.Children.Add(ExpandableCard("schedule:" + schedule.Id + ":displays", () => T("SETTINGS_TIME_INDIVIDUAL_TITLE", "Set brightness for individual displays"),
                null, MonitorLevelEditors(schedule.IndividualBrightness), schedule.IndividualBrightness.Count > 0));
            PageContent.Children.Add(ExpandableCard("schedule:" + schedule.Id,
                () => schedule.Event == "time" ? schedule.Time : $"{SolarEventName(schedule.Event)} {schedule.OffsetMinutes:+#;-#;0} {T("NATIVE_MINUTES", "min")}",
                () => $"{EnabledSummary(schedule.Enabled)} · {schedule.Brightness}% · {TargetName(schedule.MonitorId)}", body));
        }
    }

    private static string SolarEventName(string name) => T("NATIVE_SOLAR_" + name, name switch
    {
        "dawn" => "Dawn", "sunrise" => "Sunrise", "sunriseEnd" => "Sunrise end", "goldenHourEnd" => "Morning golden hour end",
        "solarNoon" => "Solar noon", "goldenHour" => "Evening golden hour", "sunsetStart" => "Sunset start", "sunset" => "Sunset",
        "dusk" => "Dusk", "nauticalDawn" => "Nautical dawn", "nauticalDusk" => "Nautical dusk", "nightEnd" => "Night end",
        "night" => "Night", "nadir" => "Solar midnight", _ => name
    });

    private void RenderHotkeysAdvanced()
    {
        Heading(T("SETTINGS_SIDEBAR_HOTKEYS", "Hotkeys & Shortcuts"), T("SETTINGS_HOTKEYS_DESC", "Configure hotkeys to adjust the brightness of one or all displays."));
        PageContent.Children.Add(SettingRow(T("SETTINGS_HOTKEYS_BREAK_TITLE", "Hotkeys break linked levels"), T("SETTINGS_HOTKEYS_BREAK_DESC", "A shortcut targeting one display temporarily overrides linked brightness."), Toggle(_settings.HotkeysBreakLinkedLevels, value => { _settings.HotkeysBreakLinkedLevels = value; Save(); })));
        PageContent.Children.Add(Description(T("NATIVE_HOTKEY_HINT", "Choose modifiers, a key, and actions, then enable the shortcut. Combinations used by other apps may be unavailable.")));
        var add = AddButton(T("SETTINGS_HOTKEYS_ADD", "Add Hotkey"));
        add.Click += (_, _) => { var binding = new HotkeyBinding { Enabled = false }; _settings.Hotkeys.Add(binding); ExpandNewCard("hotkey:" + binding.Id); Save(); RenderPage(); };
        PageContent.Children.Add(add);
        foreach (var hotkey in _settings.Hotkeys.ToList())
        {
            var body = new StackPanel { Spacing = 12 };
            bool IsValid() => (hotkey.NativeKey is "BrightnessUp" or "BrightnessDown" || hotkey.VirtualKey is > 0 and <= 255) && (hotkey.Actions.Count > 0 || hotkey.Action is "increase" or "decrease" or "power");
            body.Children.Add(EditorHeader(hotkey.Enabled && IsValid(), value => { hotkey.Enabled = value && IsValid(); Save(); RenderPage(); }, () => { _settings.Hotkeys.Remove(hotkey); Save(); RenderPage(); }, T("SETTINGS_HOTKEYS_REMOVE", "Remove hotkey")));
            if (!IsValid()) body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = T("NATIVE_INVALID_HOTKEY", "Choose a valid key and action before enabling this shortcut.") });
            if (hotkey.Enabled && _settings.Hotkeys.Any(other => other != hotkey && other.Enabled && (hotkey.NativeKey.Length > 0 ? other.NativeKey == hotkey.NativeKey : other.NativeKey.Length == 0 && other.Modifiers == hotkey.Modifiers && other.VirtualKey == hotkey.VirtualKey)))
                body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = T("NATIVE_DUPLICATE_HOTKEY", "Another enabled shortcut uses this key combination. Only one can be registered.") });
            body.Children.Add(FieldChoice(T("NATIVE_HOTKEY_SOURCE", "Shortcut key type"), [
                ("", T("NATIVE_STANDARD_KEY", "Standard keyboard shortcut")),
                ("BrightnessUp", T("SETTINGS_HOTKEYS_INCREASE", "Brightness Up") + " (Fn)"),
                ("BrightnessDown", T("SETTINGS_HOTKEYS_DECREASE", "Brightness Down") + " (Fn)")], hotkey.NativeKey,
                value => { hotkey.NativeKey = value; if (value.Length > 0) hotkey.VirtualKey = 0; else if (hotkey.VirtualKey == 0) hotkey.VirtualKey = 0x26; Save(); RenderPage(); }));
            if (hotkey.NativeKey.Length > 0) body.Children.Add(Description(T("SETTINGS_HOTKEYS_NATIVE_BRIGHTNESS_WARN", "Windows will still adjust the built-in display when a brightness key is pressed. Do not use this shortcut to adjust the built-in display.")));
            if (hotkey.NativeKey.Length == 0)
            {
            var modifiers = new List<FrameworkElement>();
            foreach (var (bit, label) in new (uint, string)[] { (2, "Ctrl"), (1, "Alt"), (4, "Shift"), (8, "Win") })
            {
                var modifier = new CheckBox { Content = label, IsChecked = (hotkey.Modifiers & bit) != 0 };
                modifier.Checked += (_, _) => { hotkey.Modifiers |= bit; Save(); };
                modifier.Unchecked += (_, _) => { hotkey.Modifiers &= ~bit; Save(); };
                modifiers.Add(modifier);
            }
            body.Children.Add(FieldGrid(modifiers.ToArray()));
            var keys = KeyChoices();
            var selectedKey = hotkey.VirtualKey.ToString(CultureInfo.InvariantCulture);
            if (!keys.Any(k => k.Value == selectedKey)) keys.Add((selectedKey, $"0x{hotkey.VirtualKey:X2}"));
            body.Children.Add(FieldChoice(T("NATIVE_KEY", "Key"), keys, selectedKey, value => { hotkey.VirtualKey = uint.Parse(value, CultureInfo.InvariantCulture); Save(); RenderPage(); }));
            }
            if (hotkey.Actions.Count == 0 && hotkey.Action is "increase" or "decrease" or "power")
            {
                body.Children.Add(FieldChoice(T("SETTINGS_HOTKEY_ACTION", "Action"), [
                    ("increase", T("SETTINGS_HOTKEYS_INCREASE", "Increase Brightness")), ("decrease", T("SETTINGS_HOTKEYS_DECREASE", "Decrease Brightness")),
                    ("power", T("PANEL_BUTTON_TURN_OFF_DISPLAYS", "Turn off displays"))], hotkey.Action, value => { hotkey.Action = value; Save(); RenderPage(); }));
                body.Children.Add(TargetChoice(hotkey.MonitorId, value => { hotkey.MonitorId = value; Save(); }));
                if (hotkey.Action != "power") body.Children.Add(FieldNumber(T("SETTINGS_HOTKEYS_LEVEL_TITLE", "Brightness level adjustment"), hotkey.Step, 1, 100, value => { hotkey.Step = value; Save(); }));
            }
            for (var i = 0; i < hotkey.Actions.Count; i++) body.Children.Add(HotkeyActionEditor(hotkey, i));
            var addAction = AddButton(T("SETTINGS_HOTKEY_ADD_ACTION", "Add Action"));
            addAction.Click += (_, _) =>
            {
                if (hotkey.Actions.Count == 0 && hotkey.Action is "increase" or "decrease" or "power")
                    hotkey.Actions.Add(new HotkeyAction { Type = hotkey.Action == "power" ? "power" : "offset", MonitorId = hotkey.MonitorId, Value = hotkey.Action == "decrease" ? -hotkey.Step : hotkey.Step });
                var action = new HotkeyAction { Type = "set", Value = 50 };
                hotkey.Actions.Add(action);
                ExpandNewCard("hotkey:" + hotkey.Id + ":action:" + ObjectKey(action));
                Save(); RenderPage();
            };
            body.Children.Add(addAction);
            PageContent.Children.Add(ExpandableCard("hotkey:" + hotkey.Id, () => HotkeySummary(hotkey),
                () => $"{EnabledSummary(hotkey.Enabled)} · {Math.Max(1, hotkey.Actions.Count)} {T("NATIVE_ACTIONS", "actions")}", body));
        }
    }

    private Expander HotkeyActionEditor(HotkeyBinding binding, int index)
    {
        var action = binding.Actions[index];
        var body = new StackPanel { Spacing = 12 };
        var up = new Button { Content = T("NATIVE_MOVE_UP", "Move up"), IsEnabled = index > 0 };
        var down = new Button { Content = T("NATIVE_MOVE_DOWN", "Move down"), IsEnabled = index < binding.Actions.Count - 1 };
        void Move(int offset)
        {
            (binding.Actions[index], binding.Actions[index + offset]) = (binding.Actions[index + offset], binding.Actions[index]);
            Save(); RenderPage();
        }
        up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
        var delete = DeleteButton(() =>
        {
            binding.Actions.Remove(action);
            if (binding.Actions.Count == 0) { binding.Action = ""; binding.Enabled = false; }
            Save(); RenderPage();
        });
        body.Children.Add(FieldGrid(up, down, delete));
        body.Children.Add(FieldChoice(T("SETTINGS_HOTKEY_ACTION", "Action"), [
            ("set", T("SETTINGS_HOTKEY_ACTION_SET", "Set value")), ("offset", T("SETTINGS_HOTKEY_ACTION_OFFSET", "Adjust value")),
            ("cycle", T("SETTINGS_HOTKEY_ACTION_CYCLE", "Cycle list of values")), ("power", T("PANEL_BUTTON_TURN_OFF_DISPLAYS", "Turn off displays")),
            ("profile", T("NATIVE_APPLY_PROFILE", "Apply profile")), ("refresh", T("GENERIC_REFRESH_DISPLAYS", "Refresh displays")),
            ("panel", T("NATIVE_SHOW_PANEL", "Show brightness panel")), ("vcp", T("NATIVE_SET_VCP", "Set raw VCP value"))], action.Type,
            value => { action.Type = value; if (value == "vcp") action.Target = "vcp"; Save(); RenderPage(); }));
        if (action.Type == "profile")
        {
            var profiles = _settings.Profiles.Select(p => (p.Id, p.Name)).ToList();
            profiles.Insert(0, ("", T("NATIVE_CHOOSE_PROFILE", "Choose a profile")));
            body.Children.Add(FieldChoice(T("SETTINGS_PROFILES_TITLE", "Profiles"), profiles, action.ProfileId, value => { action.ProfileId = value; Save(); }));
        }
        else if (action.Type is not ("refresh" or "panel"))
        {
            body.Children.Add(TargetChoice(action.MonitorId, value => { action.MonitorId = value; Save(); }));
            if (action.Type != "power")
            {
                if (action.Type != "vcp") body.Children.Add(FieldChoice(T("SETTINGS_HOTKEY_TARGET", "Action Target"), [
                    ("brightness", T("PANEL_LABEL_BRIGHTNESS", "Brightness")), ("sdr", T("SETTINGS_FEATURES_SDR_BRIGHTNESS", "SDR Brightness")),
                    ("gamma", T("NATIVE_GAMMA", "Software dimming (gamma)")), ("vcp", "DDC/CI VCP")], action.Target, value => { action.Target = value; Save(); RenderPage(); }));
                if (action.Target == "vcp" || action.Type == "vcp")
                {
                    var vcp = VcpCodeEditor(action.Vcp, value => { action.Vcp = value; Save(); });
                    vcp.Header = T("SETTINGS_FEATURES_ADD_VCP", "VCP Code");
                    body.Children.Add(vcp);
                }
                var maximum = action.Target == "vcp" || action.Type == "vcp" ? 65535 : 100;
                if (action.Type == "cycle")
                {
                    string? ValidateCycle(string text)
                    {
                        var parsed = new List<double>();
                        foreach (var token in text.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < 0 || number > maximum)
                                return T("NATIVE_INVALID_VALUES", "Enter comma-separated numeric values within the target's range.");
                            parsed.Add(number);
                        }
                        return parsed.Count == 0 ? T("NATIVE_VALUES_REQUIRED", "Add at least one value to cycle.") : null;
                    }
                    var values = ValidatedText("hotkey:" + binding.Id + ":action:" + ObjectKey(action) + ":values", T("SETTINGS_HOTKEY_VALUES", "Values"),
                        string.Join(", ", action.Values.Select(v => v.ToString(CultureInfo.InvariantCulture))), text =>
                        {
                            action.Values = text.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToList();
                            Save();
                        }, ValidateCycle);
                    values.Input.PlaceholderText = "20, 50, 100";
                    body.Children.Add(values);
                }
                else
                {
                    var value = DecimalNumber(action.Value, action.Type == "offset" ? -maximum : 0, maximum, number => { action.Value = number; Save(); });
                    value.Header = T("SETTINGS_HOTKEY_VALUE", "Value");
                    body.Children.Add(value);
                }
            }
        }
        return ExpandableCard("hotkey:" + binding.Id + ":action:" + ObjectKey(action),
            () => $"{index + 1}. {T("SETTINGS_HOTKEY_ACTION", "Action")}", () => ActionSummary(action), body);
    }

    private void RenderFeatures()
    {
        Heading(T("SETTINGS_SIDEBAR_FEATURES", "DDC/CI Features"), T("SETTINGS_FEATURES_DESCRIPTION", "Choose which hardware controls appear in the brightness panel. Supported features vary by display."));
        PageContent.Children.Add(SettingRow(T("SETTINGS_FEATURES_CUR_BRIGHTNESS_TITLE", "Check Current Values"), T("SETTINGS_FEATURES_CUR_BRIGHTNESS_DESC", "Periodically read the current brightness from connected displays."),
            Toggle(_settings.PollBrightness, value => { _settings.PollBrightness = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("NATIVE_POLL_SECONDS", "Read interval (seconds)"), null,
            Number(_settings.PollSeconds, 1, 3600, value => { _settings.PollSeconds = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_FEATURES_POWER_TITLE", "Power State Signal"), null,
            Choice([("4", T("SETTINGS_FEATURES_POWER_STANDBY", "Standby")), ("5", T("SETTINGS_FEATURES_POWER_OFF", "Power off"))], _settings.PowerOffValue.ToString(CultureInfo.InvariantCulture), value => { _settings.PowerOffValue = int.Parse(value, CultureInfo.InvariantCulture); Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_HOTKEYS_TOD_TITLE", "Turn Off Displays action"), null,
            Choice([("none", T("SETTINGS_HOTKEYS_TOD_NONE", "None (hide icon)")), ("windows", T("SETTINGS_HOTKEYS_TOD_SOFT", "Software signal")), ("ddc", T("SETTINGS_HOTKEYS_TOD_HARD", "Hardware signal (DDC/CI)")),
                ("both", T("SETTINGS_HOTKEYS_TOD_BOTH", "Hardware and software signal"))], _settings.PowerOffMode, value => { _settings.PowerOffMode = value; Save(); })));
        if (_monitors.Count == 0) PageContent.Children.Add(Description(T("GENERIC_NO_DISPLAYS", "No displays detected.")));
        foreach (var monitor in OrderedMonitors())
        {
            var settings = GetMonitorSettings(monitor.Id);
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(Description($"{monitor.Connection} · HDR: {(monitor.HdrActive ? T("GENERIC_ACTIVE", "Active") : monitor.HdrSupported ? T("GENERIC_SUPPORTED", "Supported") : T("GENERIC_NOT_SUPPORTED", "Not supported"))}"));
            body.Children.Add(FieldChoice(T("NATIVE_MAIN_CONTROL", "Primary brightness control"), [
                ("brightness", T("PANEL_LABEL_BRIGHTNESS", "Brightness")),
                ("sdr", T("SETTINGS_FEATURES_SDR_BRIGHTNESS", "SDR Brightness")),
                ("gamma", T("NATIVE_GAMMA", "Software dimming (gamma)"))], settings.MainControl,
                value => { settings.MainControl = value; Save(); }));
            body.Children.Add(InlineRow(T("NATIVE_SOFTWARE_FALLBACK", "Use software dimming when hardware brightness is unavailable"), Toggle(settings.SoftwareFallback, value => { settings.SoftwareFallback = value; Save(); })));
            body.Children.Add(InlineRow(T("SETTINGS_MONITORS_EXTEND_MINIMUM_TITLE", "Extend Minimum Brightness"), Toggle(settings.ExtendMinimum, value => { settings.ExtendMinimum = value; Save(); })));
            body.Children.Add(FieldNumber(T("SETTINGS_MONITORS_EXTEND_MINIMUM_BREAKPOINT", "Percentage of slider that uses software dimming"), settings.ExtendMinimumBreakpoint, 1, 90, value => { settings.ExtendMinimumBreakpoint = value; Save(); }));
            body.Children.Add(InlineRow(T("NATIVE_FORCE_HDR", "Expose SDR brightness control even if HDR detection fails"), Toggle(settings.ForceHdr, value => { settings.ForceHdr = value; Save(); })));
            body.Children.Add(InlineRow(T("NATIVE_SKIP_RESTORE", "Skip automatic brightness restoration"), Toggle(settings.SkipRestore, value => { settings.SkipRestore = value; Save(); })));
            var brightnessCode = VcpCodeEditor(settings.BrightnessVcp, value => { settings.BrightnessVcp = value; Save(); });
            brightnessCode.Header = T("NATIVE_BRIGHTNESS_VCP", "Brightness VCP code");
            body.Children.Add(brightnessCode);
            body.Children.Add(Description(T("SETTINGS_FEATURES_BRIGHTNESS_VCP_DESC", "The standard brightness VCP code is 0x10.")));
            body.Children.Add(CalibrationEditor(settings));

            if (_actions.QueryFeaturesAsync is not null)
            {
                body.Children.Add(ActionButton(T("NATIVE_QUERY_FEATURES", "Read supported DDC/CI features"), async () =>
                {
                    _features[monitor.Id] = await _actions.QueryFeaturesAsync(monitor.Id);
                    RenderPage();
                }));
            }
            if (_features.TryGetValue(monitor.Id, out var reported))
            {
                foreach (var feature in reported)
                {
                    var code = feature.Code;
                    var row = new StackPanel { Spacing = 4 };
                    row.Children.Add(Label($"0x{code:X2} · {feature.Name}"));
                    row.Children.Add(Description($"{feature.Current} / {feature.Maximum}" + (feature.AllowedValues.Count > 0 ? $" · {string.Join(", ", feature.AllowedValues)}" : "")));
                    if (!settings.Features.ContainsKey(code))
                    {
                        var use = new Button { Content = T("SETTINGS_FEATURES_ADD", "Add Feature") };
                        use.Click += (_, _) =>
                        {
                            settings.Features[code] = new FeatureSettings { Enabled = true, Name = feature.Name, Max = (int)Math.Clamp(feature.Maximum, 1u, 65535u) };
                            ExpandNewCard("feature:" + monitor.Id + ":" + code);
                            Save(); RenderPage();
                        };
                        row.Children.Add(use);
                    }
                    body.Children.Add(row);
                }
                if (reported.Count == 0) body.Children.Add(Description(T("NATIVE_NO_REPORTED_FEATURES", "No feature capabilities were reported by this display.")));
            }
            foreach (var pair in settings.Features.OrderBy(pair => pair.Key).ToList()) body.Children.Add(FeatureEditor(monitor.Id, settings, pair.Key, pair.Value));
            var newCode = (byte)0x12;
            var input = VcpCodeEditor(newCode, value => newCode = value);
            input.Header = T("SETTINGS_FEATURES_ADD_VCP", "VCP Code");
            body.Children.Add(input);
            var add = AddButton(T("SETTINGS_FEATURES_ADD", "Add Feature"));
            add.Click += (_, _) =>
            {
                if (!input.TryCommit() || !TryParseVcp(input.Text, out newCode)) return;
                if (settings.Features.ContainsKey(newCode)) { ShowStatus(T("SETTINGS_FEATURES_ADD_EXISTS", "This feature is already active.")); return; }
                settings.Features[newCode] = new FeatureSettings { Enabled = true, Name = $"VCP 0x{newCode:X2}" };
                ExpandNewCard("feature:" + monitor.Id + ":" + newCode);
                Save(); RenderPage();
            };
            body.Children.Add(add);
            PageContent.Children.Add(ExpandableCard("features:" + monitor.Id, () => DisplayName(monitor),
                () => $"{monitor.Connection} · {settings.Features.Count} {T("NATIVE_FEATURES", "features")}", body, monitor.Id == OrderedMonitors().First().Id));
        }
    }

    private StackPanel CalibrationEditor(MonitorSettings settings)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Label(T("NATIVE_CALIBRATION", "Brightness calibration")));
        body.Children.Add(Description(T("SETTINGS_MONITORS_CALIBRATION_DESC", "Map panel brightness levels to physical brightness levels using calibration points.")));
        foreach (var point in settings.Calibration.ToList())
        {
            var input = ValidatedDecimal("calibration:" + ObjectKey(point) + ":input", T("NATIVE_INPUT", "Input"), point.Input, 0, 100, value => { point.Input = value; Save(); },
                value => settings.Calibration.Any(other => !ReferenceEquals(other, point) && Math.Abs(other.Input - value) < 0.000001) ? T("NATIVE_DUPLICATE_CALIBRATION", "Use a different input value for each calibration point.") : null);
            var output = ValidatedDecimal("calibration:" + ObjectKey(point) + ":output", T("NATIVE_OUTPUT", "Output"), point.Output, 0, 100, value => { point.Output = value; Save(); });
            body.Children.Add(FieldGrid(input, output, DeleteButton(() => { settings.Calibration.Remove(point); Save(); RenderPage(); })));
        }
        var add = AddButton(T("GENERIC_CALIBRATION_POINT", "Calibration Point"));
        add.IsEnabled = settings.Calibration.Count < 101;
        add.Click += (_, _) =>
        {
            var input = Enumerable.Range(0, 101).OrderBy(value => Math.Abs(value - 50)).FirstOrDefault(value => settings.Calibration.All(point => Math.Abs(point.Input - value) > 0.000001));
            settings.Calibration.Add(new CalibrationPoint { Input = input, Output = input }); Save(); RenderPage();
        };
        body.Children.Add(add);
        return body;
    }

    private Expander FeatureEditor(string monitorId, MonitorSettings monitor, byte code, FeatureSettings feature)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(EditorHeader(feature.Enabled, value => { feature.Enabled = value; Save(); }, () => { monitor.Features.Remove(code); Save(); RenderPage(); }, T("GENERIC_DELETE", "Delete")));
        body.Children.Add(TextEditor(T("SETTINGS_MONITORS_DETAILS_NAME", "Name"), feature.Name, value => { feature.Name = value; Save(); }));
        body.Children.Add(FieldChoice(T("GENERIC_ICON", "Icon"), [
            ("windows", "Windows"), ("text", T("GENERIC_TEXT", "Text")), ("image", T("NATIVE_IMAGE", "Image"))], feature.IconType,
            value => { feature.IconType = value; Save(); RenderPage(); }));
        if (feature.IconType == "windows")
            body.Children.Add(TextEditor(T("NATIVE_WINDOWS_GLYPH", "Windows icon (Unicode character)"), feature.IconGlyph, value => { feature.IconGlyph = value; Save(); }));
        else if (feature.IconType == "text")
            body.Children.Add(TextEditor(T("GENERIC_SLIDER_TEXT", "Slider Text"), feature.IconText, value => { feature.IconText = value; Save(); }));
        else if (feature.IconType == "image")
            body.Children.Add(TextEditor(T("NATIVE_ICON_PATH", "Local icon path (.png, .jpg, .ico)"), feature.IconPath, value => { feature.IconPath = value; Save(); }));
        var min = FieldNumber(T("GENERIC_MINIMUM", "Min"), feature.Min, 0, 65534, _ => { });
        var max = FieldNumber(T("GENERIC_MAXIMUM", "Max"), feature.Max, 1, 65535, _ => { });
        var updating = false;
        min.ValueChanged += (_, args) =>
        {
            if (updating || !double.IsFinite(args.NewValue)) return;
            updating = true;
            feature.Min = Math.Clamp((int)Math.Round(args.NewValue), 0, 65534);
            feature.Max = Math.Max(feature.Max, feature.Min + 1);
            min.Value = feature.Min; max.Value = feature.Max;
            updating = false; Save();
        };
        max.ValueChanged += (_, args) =>
        {
            if (updating || !double.IsFinite(args.NewValue)) return;
            updating = true;
            feature.Max = Math.Clamp((int)Math.Round(args.NewValue), 1, 65535);
            feature.Min = Math.Min(feature.Min, feature.Max - 1);
            min.Value = feature.Min; max.Value = feature.Max;
            updating = false; Save();
        };
        body.Children.Add(min); body.Children.Add(max);
        body.Children.Add(InlineRow(T("SETTINGS_FEATURES_LINKED_TO_BRIGHTNESS", "Linked to brightness"), Toggle(feature.LinkedToBrightness, value => { feature.LinkedToBrightness = value; Save(); })));
        body.Children.Add(FieldNumber(T("SETTINGS_FEATURES_STOP_ON_BRIGHTNESS", "Stop after this brightness level"), feature.MaxVisual, 1, 100, value => { feature.MaxVisual = value; Save(); }));
        return ExpandableCard("feature:" + monitorId + ":" + code, () => $"0x{code:X2} · {feature.Name}",
            () => $"{EnabledSummary(feature.Enabled)} · {feature.Min}–{feature.Max}", body);
    }

    private void RenderProfiles()
    {
        Heading(T("SETTINGS_PROFILES_TITLE", "Profiles"), T("SETTINGS_PROFILES_DESC", "Adjust display brightness for foreground apps, or apply profiles from the tray menu."));
        var add = AddButton(T("SETTINGS_PROFILES_ADD", "New Profile"));
        add.Click += (_, _) =>
        {
            var profile = new AppProfile { Name = T("SETTINGS_PROFILES_ADD", "New Profile") };
            _settings.Profiles.Add(profile); ExpandNewCard("profile:" + profile.Id); Save(); RenderPage();
        };
        PageContent.Children.Add(add);
        foreach (var profile in _settings.Profiles.ToList())
        {
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(EditorHeader(profile.Enabled, value => { profile.Enabled = value; Save(); }, () => { _settings.Profiles.Remove(profile); Save(); RenderPage(); }, T("GENERIC_DELETE", "Delete")));
            body.Children.Add(TextEditor(T("SETTINGS_PROFILES_NAME", "Profile Name"), profile.Name, value => { profile.Name = value; Save(); }, "profile:" + profile.Id + ":name"));
            body.Children.Add(TextEditor(T("SETTINGS_PROFILES_APP_PATH", "App path"), profile.Path, value => { profile.Path = value; Save(); }, "profile:" + profile.Id + ":path"));
            body.Children.Add(Description(T("SETTINGS_PROFILES_APP_DESC", "Use all or part of an executable path to activate this profile for the foreground app.")));
            body.Children.Add(InlineRow(T("NATIVE_PROFILE_RESTORE", "Restore previous brightness when leaving this app"), Toggle(profile.RestorePrevious, value => { profile.RestorePrevious = value; Save(); })));
            body.Children.Add(InlineRow(T("SETTINGS_PROFILES_SHOW_MENU", "Show in right-click tray menu"), Toggle(profile.ShowInTray, value => { profile.ShowInTray = value; Save(); })));
            body.Children.Add(FieldChoice(T("SETTINGS_PROFILES_OVERLAY_TITLE", "Override overlay type"), [
                ("normal", T("GENERIC_DEFAULT", "Default")), ("safe", T("SETTINGS_GENERAL_ON_OVERLAY_TITLE", "Most compatible")),
                ("disabled", T("SETTINGS_GENERAL_DIS_OVERLAY_TITLE", "Disable overlay")),
                ("aggressive", T("SETTINGS_GENERAL_FORCE_OVERLAY_TITLE", "Forced on"))], profile.OverlayType == "force" ? "aggressive" : profile.OverlayType, value => { profile.OverlayType = value; Save(); }));
            body.Children.Add(MonitorLevelEditors(profile.Brightness));
            if (_actions.ApplyProfileAsync is not null)
                body.Children.Add(ActionButton(T("NATIVE_APPLY_PROFILE", "Apply profile now"), () => _actions.ApplyProfileAsync(profile)));
            PageContent.Children.Add(ExpandableCard("profile:" + profile.Id, () => profile.Name,
                () => $"{EnabledSummary(profile.Enabled)} · {profile.Brightness.Count} {T("NATIVE_DISPLAYS", "displays")}" + (string.IsNullOrWhiteSpace(profile.Path) ? "" : " · " + profile.Path), body));
        }
    }

    private void RenderSensors()
    {
        var sensor = _settings.Sensor;
        Heading(T("SETTINGS_LIGHT_SENSOR_TITLE", "Light Sensor"), T("SETTINGS_LIGHT_SENSOR_DESC", "Adjust monitor brightness using ambient light levels."));
        PageContent.Children.Add(SettingRow(T("SETTINGS_LIGHT_SENSOR_ENABLE", "Enable feature"), null, Toggle(sensor.Enabled, value => { sensor.Enabled = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_LIGHT_SENSOR_TYPE_TITLE", "Sensor type"), null, Choice([
            ("windows", T("SETTINGS_LIGHT_SENSOR_TYPE_WINDOWS", "Windows Ambient")),
            ("yocto", T("SETTINGS_LIGHT_SENSOR_TYPE_YOCTO", "Yocto")),
            ("fake", T("SETTINGS_LIGHT_SENSOR_TYPE_FAKE", "Fake"))], sensor.Provider, value => { sensor.Provider = value; _sensorDevices = []; Save(); RenderPage(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_LIGHT_SENSOR_POLLING_TITLE", "Sensor polling interval (seconds)"), null,
            Number(sensor.PollSeconds, 1, 3600, value => { sensor.PollSeconds = value; Save(); })));
        if (sensor.Provider == "yocto")
        {
            PageContent.Children.Add(Card(ValidatedText("sensor:endpoint", T("SETTINGS_LIGHT_SENSOR_YOCTO_URL_LABEL", "VirtualHub URL"), sensor.Endpoint,
                value => { sensor.Endpoint = value; Save(); }, value =>
                    !Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") || string.IsNullOrEmpty(endpoint.Host) || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
                        ? T("NATIVE_INVALID_ENDPOINT", "Enter an absolute HTTP or HTTPS sensor URL.") : null)));
            PageContent.Children.Add(Description(T("SETTINGS_LIGHT_SENSOR_YOCTO_INSTALL_DESC", "Run Yocto VirtualHub to communicate with a connected Yocto light sensor.")));
        }
        if (sensor.Provider is "windows" or "yocto")
            PageContent.Children.Add(Card(ValidatedText("sensor:hardware", T("NATIVE_SENSOR_HARDWARE", "Sensor hardware ID (empty = first available)"), sensor.HardwareId,
                value => { sensor.HardwareId = value; Save(); }, value =>
                    sensor.Provider == "yocto" && (value.Count(c => c == '.') > 1 || value.StartsWith('.') || value.EndsWith('.') || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
                        ? T("NATIVE_INVALID_SENSOR_ID", "Use a serial number or serial.function sensor ID.") : null)));
        if (sensor.Provider != "fake" && _actions.QuerySensorsAsync is not null)
            PageContent.Children.Add(ActionButton(T("NATIVE_QUERY_SENSORS", "Find connected sensors"), async () => { _sensorDevices = await _actions.QuerySensorsAsync(); RenderPage(); }));
        if (_sensorDevices.Count > 0)
        {
            var devices = _sensorDevices.Select(device => (device.Id, device.Name)).ToList();
            devices.Insert(0, ("", T("GENERIC_DEFAULT", "Default")));
            if (sensor.HardwareId.Length > 0 && !devices.Any(device => device.Item1 == sensor.HardwareId)) devices.Add((sensor.HardwareId, sensor.HardwareId));
            PageContent.Children.Add(SettingRow(T("SETTINGS_LIGHT_SENSOR_WINDOWS_AVAILABLE", "Available sensors"), null,
                Choice(devices, sensor.HardwareId, value => { sensor.HardwareId = value; Save(); RenderPage(); })));
        }
        if (sensor.Provider == "fake")
            PageContent.Children.Add(SettingRow(T("SETTINGS_LIGHT_SENSOR_FAKE_LUX_TITLE", "Overridden Lux"), T("SETTINGS_LIGHT_SENSOR_FAKE_LUX_DESC", "Set the simulated light level in Lux."),
                DecimalNumber(sensor.FakeLux, 0, 1000000, value => { sensor.FakeLux = value; Save(); })));
        PageContent.Children.Add(Description(T("SETTINGS_LIGHT_SENSOR_MONITORS_DESC_INTERPOLATE", "Brightness is linearly interpolated between the configured light levels.")));
        foreach (var monitor in OrderedMonitors())
        {
            if (!sensor.Monitors.TryGetValue(monitor.Id, out var range)) sensor.Monitors[monitor.Id] = range = new SensorMonitorSettings();
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(Label(DisplayName(monitor), 18));
            body.Children.Add(InlineRow(T("NATIVE_ENABLED", "Enabled"), Toggle(range.Enabled, value => { range.Enabled = value; Save(); })));
            var minLux = ValidatedDecimal("sensor:" + monitor.Id + ":minimum", T("NATIVE_MIN_LUX", "Minimum ambient light (Lux)"), range.MinLux, 0, 999999,
                value => { range.MinLux = value; Save(); }, value => value >= range.MaxLux ? T("NATIVE_MIN_BELOW_MAX", "The minimum must be below the maximum.") : null);
            var maxLux = ValidatedDecimal("sensor:" + monitor.Id + ":maximum", T("NATIVE_MAX_LUX", "Maximum ambient light (Lux)"), range.MaxLux, 0.1, 1000000,
                value => { range.MaxLux = value; Save(); }, value => value <= range.MinLux ? T("NATIVE_MAX_ABOVE_MIN", "The maximum must be above the minimum.") : null);
            body.Children.Add(FieldGrid(minLux, maxLux));
            body.Children.Add(BrightnessEditor(range.MinBrightness, value => { range.MinBrightness = value; Save(); }, T("NATIVE_MIN_BRIGHTNESS", "Minimum brightness")));
            body.Children.Add(BrightnessEditor(range.MaxBrightness, value => { range.MaxBrightness = value; Save(); }, T("NATIVE_MAX_BRIGHTNESS", "Maximum brightness")));
            PageContent.Children.Add(Card(body));
        }
    }

    private void RenderAdvanced()
    {
        Heading(T("NATIVE_ADVANCED", "Advanced"), T("NATIVE_ADVANCED_DESC", "Monitor detection, remote commands, settings transfer, and diagnostics."));
        PageContent.Children.Add(SettingRow(T("NATIVE_WMI", "Windows internal display detection (WMI)"), null, Toggle(!_settings.DisableWmi, value => { _settings.DisableWmi = !value; Save(); })));
        PageContent.Children.Add(SettingRow(T("NATIVE_DDC", "External display detection (DDC/CI)"), null, Toggle(!_settings.DisableDdc, value => { _settings.DisableDdc = !value; Save(); })));
        PageContent.Children.Add(SettingRow(T("NATIVE_APPLE", "Apple Studio Display detection"), null, Toggle(!_settings.DisableAppleStudio, value => { _settings.DisableAppleStudio = !value; Save(); })));
        PageContent.Children.Add(SettingRow(T("NATIVE_SOFTWARE_FALLBACK", "Use software dimming when hardware brightness is unavailable"), null, Toggle(_settings.UseSoftwareBrightnessFallback, value => { _settings.UseSoftwareBrightnessFallback = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("NATIVE_AUTO_REFRESH", "Refresh displays after hardware changes"), null, Toggle(!_settings.DisableAutoRefresh, value => { _settings.DisableAutoRefresh = !value; Save(); })));
        PageContent.Children.Add(SettingRow(T("NATIVE_WAKE_DELAY", "Wake restore delay (seconds)"), null, Number(_settings.WakeRestoreSeconds, 0, 3600, value => { _settings.WakeRestoreSeconds = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("NATIVE_HARDWARE_DELAY", "Hardware change delay (seconds)"), null, Number(_settings.HardwareRestoreSeconds, 0, 60, value => { _settings.HardwareRestoreSeconds = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("NATIVE_VCP_DELAY", "VCP read delay (milliseconds)"), null, Number(_settings.VcpReadDelayMilliseconds, 0, 1000, value => { _settings.VcpReadDelayMilliseconds = value; Save(); })));
        var udp = new StackPanel { Spacing = 12 };
        udp.Children.Add(Label("UDP", 18));
        udp.Children.Add(InlineRow(T("NATIVE_UDP_ENABLED", "Enable UDP command server"), Toggle(_settings.UdpEnabled, value => { _settings.UdpEnabled = value; Save(); })));
        udp.Children.Add(InlineRow(T("NATIVE_UDP_REMOTE", "Allow connections from other computers"), Toggle(_settings.UdpRemote, value => { _settings.UdpRemote = value; Save(); })));
        udp.Children.Add(FieldNumber(T("NATIVE_PORT", "Port"), _settings.UdpPort, 1, 65535, value => { _settings.UdpPort = value; Save(); }));
        var key = new PasswordBox { Header = T("NATIVE_UDP_KEY", "Authentication key"), Password = _settings.UdpKey, PasswordRevealMode = PasswordRevealMode.Peek };
        key.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(key.Password)) { key.Password = _settings.UdpKey; ShowStatus(T("NATIVE_KEY_REQUIRED", "The UDP authentication key cannot be empty.")); return; }
            _settings.UdpKey = key.Password; Save();
        };
        udp.Children.Add(key);
        PageContent.Children.Add(Card(udp));
        var transfer = new StackPanel { Spacing = 12 };
        transfer.Children.Add(Label(T("NATIVE_SETTINGS_TRANSFER", "Settings import and export"), 18));
        if (_actions.ImportSettingsAsync is not null) transfer.Children.Add(ActionButton(T("NATIVE_IMPORT", "Import settings"), async () => { await _actions.ImportSettingsAsync(); Localize(); ApplyTheme(); RenderPage(); }));
        if (_actions.ImportKnownDisplaysAsync is not null) transfer.Children.Add(ActionButton(T("NATIVE_IMPORT_DISPLAYS", "Import known-displays.json"), async () => { await _actions.ImportKnownDisplaysAsync(); RenderPage(); }));
        if (_actions.ExportSettingsAsync is not null) transfer.Children.Add(ActionButton(T("NATIVE_EXPORT", "Export settings"), _actions.ExportSettingsAsync));
        if (_actions.ResetSettingsAsync is not null) transfer.Children.Add(ActionButton(T("SETTINGS_GENERAL_RESET_BUTTON", "Reset settings"), async () =>
        {
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = T("SETTINGS_GENERAL_RESET_TITLE", "Reset settings"), Content = T("NATIVE_RESET_CONFIRM", "Reset all Twinkle Tray settings to their defaults?"), PrimaryButtonText = T("SETTINGS_GENERAL_RESET_BUTTON", "Reset settings"), CloseButtonText = T("GENERIC_CANCEL", "Cancel"), DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) { await _actions.ResetSettingsAsync(); Localize(); ApplyTheme(); RenderPage(); }
        }));
        if (_settings.ImportWarnings.Count > 0) transfer.Children.Add(Description(string.Join(Environment.NewLine, _settings.ImportWarnings)));
        foreach (var unmatched in _settings.ImportUnmappedMonitorIds)
        {
            var choices = new List<(string Value, string Label)> { ("", T("NATIVE_UNMAPPED", "Not mapped")) };
            choices.AddRange(_monitors.Select(m => (m.Id, DisplayName(m))));
            var mapping = FieldChoice(unmatched, choices, _settings.ImportIdentityMap.GetValueOrDefault(unmatched, ""), value =>
            {
                if (value.Length == 0) _settings.ImportIdentityMap.Remove(unmatched); else _settings.ImportIdentityMap[unmatched] = value;
                Save();
            });
            transfer.Children.Add(mapping);
        }
        if (_settings.ImportUnmappedMonitorIds.Count > 0 && _actions.ReapplyImportAsync is not null)
            transfer.Children.Add(ActionButton(T("NATIVE_APPLY_MAPPINGS", "Apply display mappings"), async () => { await _actions.ReapplyImportAsync(); RenderPage(); }));
        PageContent.Children.Add(Card(transfer));
        PageContent.Children.Add(SettingRow(T("NATIVE_LOGGING", "Save diagnostic logs"), null, Toggle(_settings.Logging, value => { _settings.Logging = value; Save(); })));
        if (_actions.OpenLogs is not null) PageContent.Children.Add(ActionButton(T("NATIVE_OPEN_LOGS", "Open logs folder"), () => { _actions.OpenLogs(); return Task.CompletedTask; }));
        if (_actions.GenerateDiagnosticsAsync is not null) PageContent.Children.Add(ActionButton(T("SETTINGS_GENERAL_REPORT_TITLE", "Generate Report"), _actions.GenerateDiagnosticsAsync));
        var refresh = new Button { Content = T("GENERIC_REFRESH_DISPLAYS", "Refresh displays") };
        refresh.Click += (_, _) => _refreshMonitors();
        PageContent.Children.Add(refresh);
    }

    private void RenderUpdates()
    {
        Heading(T("SETTINGS_UPDATES_TITLE", "Updates"), $"Twinkle Tray · WinUI 3  {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)}");
        PageContent.Children.Add(SettingRow(T("SETTINGS_UPDATES_AUTOMATIC_TITLE", "Automatically check for new versions"), T("SETTINGS_UPDATES_AUTOMATIC_DESC", "Check the WinUI 3 fork for new releases."),
            Toggle(_settings.CheckForUpdates, value => { _settings.CheckForUpdates = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_UPDATES_CHANNEL", "Update channel"), null,
            Choice([("stable", T("SETTINGS_UPDATES_BRANCH_STABLE", "Stable (default)")), ("beta", T("SETTINGS_UPDATES_BRANCH_BETA", "Beta"))], _settings.UpdateChannel, value => { _settings.UpdateChannel = value; Save(); })));
        if (_actions.CheckUpdatesAsync is not null) PageContent.Children.Add(ActionButton(T("NATIVE_CHECK_UPDATES", "Check for updates"), _actions.CheckUpdatesAsync));
        if (_actions.InstallUpdateAsync is not null) PageContent.Children.Add(ActionButton(T("NATIVE_INSTALL_UPDATE", "Download and install update"), _actions.InstallUpdateAsync));
        PageContent.Children.Add(new HyperlinkButton { Content = T("NATIVE_RELEASES", "Release history"), NavigateUri = new Uri("https://github.com/BK927/twinkle-tray/releases"), HorizontalAlignment = HorizontalAlignment.Left });
    }

    private IReadOnlyList<MonitorSnapshot> OrderedMonitors() => _monitors.OrderBy(m => GetMonitorSettings(m.Id).Order).ThenBy(m => m.Name).ToArray();

    private StackPanel MonitorLevelEditors(Dictionary<string, int> values)
    {
        var content = new StackPanel { Spacing = 12 };
        foreach (var monitor in OrderedMonitors())
        {
            var active = values.ContainsKey(monitor.Id);
            content.Children.Add(InlineRow(DisplayName(monitor), Toggle(active, value =>
            {
                if (value) values[monitor.Id] = 50; else values.Remove(monitor.Id);
                Save(); RenderPage();
            })));
            if (active) content.Children.Add(BrightnessEditor(values[monitor.Id], value => { values[monitor.Id] = value; Save(); }));
        }
        foreach (var disconnected in values.Keys.Where(id => !_monitors.Any(m => m.Id == id)).ToArray())
        {
            content.Children.Add(InlineRow(T("NATIVE_DISCONNECTED", "Disconnected display") + " · " + disconnected,
                DeleteButton(() => { values.Remove(disconnected); Save(); RenderPage(); })));
            content.Children.Add(BrightnessEditor(values[disconnected], value => { values[disconnected] = value; Save(); }));
        }
        return content;
    }

    private static ComboBox FieldChoice(string title, IEnumerable<(string Value, string Label)> choices, string selected, Action<string> changed)
    {
        var control = Choice(choices, selected, changed);
        control.Header = Label(title);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(control, "field:" + LabelKey(title));
        return control;
    }

    private static NumberBox FieldNumber(string title, int value, int min, int max, Action<int> changed)
    {
        var control = Number(value, min, max, changed);
        control.Header = Label(title);
        control.Width = 180;
        control.MinWidth = 0;
        return control;
    }

    private SettingsTextField TextEditor(string title, string value, Action<string> changed, string? key = null) =>
        ValidatedText(key ?? LabelKey(title), title, value, changed);

    private static NumberBox DecimalNumber(double value, double min, double max, Action<double> changed)
    {
        var number = new NumberBox { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), SmallChange = 1, LargeChange = 10, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        number.ValueChanged += (_, args) => { if (double.IsFinite(args.NewValue)) changed(Math.Clamp(args.NewValue, min, max)); };
        return number;
    }

    private SettingsTextField VcpCodeEditor(byte value, Action<byte> changed)
    {
        return ValidatedText("vcp", T("SETTINGS_FEATURES_ADD_VCP", "VCP Code"), $"0x{value:X2}", text =>
            { if (TryParseVcp(text, out var code)) changed(code); },
            text => TryParseVcp(text, out _) ? null : T("NATIVE_INVALID_VCP", "Enter a VCP code between 0x00 and 0xFF."));
    }

    private static bool TryParseVcp(string text, out byte value)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static Button DeleteButton(Action deleted)
    {
        var button = new Button { Content = T("GENERIC_DELETE", "Delete"), VerticalAlignment = VerticalAlignment.Bottom };
        button.Click += (_, _) => deleted();
        return button;
    }

    private Button ActionButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (Exception error) { ShowError(error.Message); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
}

public sealed class SettingsActions
{
    public Func<Task>? CheckUpdatesAsync { get; init; }
    public Func<Task>? InstallUpdateAsync { get; init; }
    public Func<Task>? ImportSettingsAsync { get; init; }
    public Func<Task>? ImportKnownDisplaysAsync { get; init; }
    public Func<Task>? ExportSettingsAsync { get; init; }
    public Func<Task>? ResetSettingsAsync { get; init; }
    public Func<Task>? ReapplyImportAsync { get; init; }
    public Func<Task>? GenerateDiagnosticsAsync { get; init; }
    public Func<Task<(double Latitude, double Longitude)>>? GetCoordinatesAsync { get; init; }
    public Func<Task<IReadOnlyList<AmbientLightDevice>>>? QuerySensorsAsync { get; init; }
    public Action? OpenLogs { get; init; }
    public Func<AppProfile, Task>? ApplyProfileAsync { get; init; }
    public Func<string, Task<IReadOnlyList<VcpFeature>>>? QueryFeaturesAsync { get; init; }
}
