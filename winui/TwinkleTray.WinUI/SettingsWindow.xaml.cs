using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using System.Globalization;
using System.Runtime.InteropServices;
using TwinkleTray.Core;
using TwinkleTray.Hardware;
using TwinkleTray.WinUI.Services;
using Windows.Graphics;

namespace TwinkleTray.WinUI;

public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly Action _refreshMonitors;
    private IReadOnlyList<MonitorSnapshot> _monitors;
    private string _page = "general";

    public SettingsWindow(AppSettings settings, IReadOnlyList<MonitorSnapshot> monitors, Action saveSettings, Action refreshMonitors)
    {
        _settings = settings;
        _monitors = monitors;
        _saveSettings = saveSettings;
        _refreshMonitors = refreshMonitors;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        SystemBackdrop = new MicaBackdrop();
        SetInitialSizeAndPosition();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        Localize();
        ApplyTheme();
        Navigation.SelectedItem = GeneralNavigation;
        RenderPage();
    }

    /// <summary>Called after a fresh monitor scan; preserves the selected settings page.</summary>
    public void UpdateMonitors(IReadOnlyList<MonitorSnapshot> monitors)
    {
        _monitors = monitors;
        if (_page is "monitors" or "time" or "hotkeys") RenderPage();
    }

    private void SetInitialSizeAndPosition()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1d : dpi / 96d;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var margin = (int)Math.Round(16 * scale);
        var width = Math.Min((int)Math.Round(1040 * scale), Math.Max(1, area.Width - margin * 2));
        var height = Math.Min((int)Math.Round(740 * scale), Math.Max(1, area.Height - margin * 2));
        AppWindow.MoveAndResize(new RectInt32(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width,
            height));
    }

    internal int VerifyPagesForSmokeTest()
    {
        var originalPage = _page;
        var verified = 0;
        try
        {
            foreach (var page in new[] { "general", "monitors", "time", "hotkeys", "idle", "about" })
            {
                _page = page;
                RenderPage();
                if (PageContent.Children.Count == 0)
                    throw new InvalidOperationException($"The settings page '{page}' rendered no content.");
                verified++;
            }
            return verified;
        }
        finally
        {
            _page = originalPage;
            RenderPage();
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private static string T(string key, string fallback) => LocalizationService.Get(key, fallback);

    private void Localize()
    {
        LocalizationService.Configure(_settings.Language);
        Title = T("SETTINGS_TITLE", "Twinkle Tray Settings");
        WindowTitle.Text = Title;
        GeneralNavigation.Content = T("SETTINGS_SIDEBAR_GENERAL", "General");
        MonitorsNavigation.Content = T("SETTINGS_SIDEBAR_MONITORS", "Monitor Settings");
        TimeNavigation.Content = T("SETTINGS_SIDEBAR_TIME", "Time Adjustments");
        HotkeysNavigation.Content = T("SETTINGS_SIDEBAR_HOTKEYS", "Hotkeys & Shortcuts");
        IdleNavigation.Content = T("SETTINGS_TIME_IDLE_TITLE", "Idle Detection");
        AboutNavigation.Content = T("NATIVE_ABOUT", "About");
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = _settings.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string page)
        {
            _page = page;
            RenderPage();
        }
    }

    private void RenderPage()
    {
        if (PageContent is null) return;
        PageContent.Children.Clear();
        switch (_page)
        {
            case "monitors": RenderMonitors(); break;
            case "time": RenderTime(); break;
            case "hotkeys": RenderHotkeys(); break;
            case "idle": RenderIdle(); break;
            case "about": RenderAbout(); break;
            default: RenderGeneral(); break;
        }
    }

    private void Heading(string title, string? description = null)
    {
        PageContent.Children.Add(new TextBlock
        {
            Text = title, FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
        });
        if (!string.IsNullOrEmpty(description))
            PageContent.Children.Add(Description(description, new Thickness(0, 0, 0, 14)));
    }

    private void RenderGeneral()
    {
        Heading(T("SETTINGS_GENERAL_TITLE", "General"), T("NATIVE_GENERAL_DESCRIPTION", "Personalize Twinkle Tray's appearance and behavior. Changes are saved automatically."));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_STARTUP", "Launch at startup"), null,
            Toggle(_settings.RunAtStartup, value => { _settings.RunAtStartup = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_THEME_TITLE", "Theme"), null,
            Choice([("system", T("SETTINGS_GENERAL_THEME_SYSTEM", "System preferences (default)")),
                    ("light", T("SETTINGS_GENERAL_THEME_LIGHT", "Light")),
                    ("dark", T("SETTINGS_GENERAL_THEME_DARK", "Dark"))], _settings.Theme,
                value => { _settings.Theme = value; ApplyTheme(); Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_LANGUAGE_TITLE", "Language"), null,
            Choice([("system", T("SETTINGS_GENERAL_LANGUAGE_SYSTEM", "System language (default)")), ("en", "English"), ("ko", "한국어")],
                _settings.Language, value => { _settings.Language = value; Localize(); Save(); RenderPage(); })));
        PageContent.Children.Add(SettingRow(T("PANEL_BUTTON_LINK_LEVELS", "Link levels"), T("NATIVE_LINK_DESCRIPTION", "Adjust all displays together from the brightness panel."),
            Toggle(_settings.LinkedBrightness, value => { _settings.LinkedBrightness = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("NATIVE_SCROLL_STEP", "Brightness adjustment step"), T("NATIVE_SCROLL_DESCRIPTION", "Brightness change when using the mouse wheel over a slider."),
            Number(_settings.ScrollStep, 1, 100, value => { _settings.ScrollStep = value; Save(); })));
    }

    private void RenderMonitors()
    {
        Heading(T("SETTINGS_SIDEBAR_MONITORS", "Monitor Settings"), T("NATIVE_MONITORS_DESCRIPTION", "Customize display names, their order, and brightness ranges."));
        var refresh = new Button { Content = T("GENERIC_REFRESH_DISPLAYS", "Refresh displays"), Margin = new Thickness(0, 0, 0, 8) };
        refresh.Click += (_, _) => _refreshMonitors();
        PageContent.Children.Add(refresh);
        if (_monitors.Count == 0)
        {
            PageContent.Children.Add(Description(T("GENERIC_NO_DISPLAYS", "No displays detected. Please connect a display to your PC.")));
            return;
        }
        var sorted = _monitors.OrderBy(m => GetMonitorSettings(m.Id).Order).ThenBy(m => m.Name).ToList();
        for (var index = 0; index < sorted.Count; index++)
        {
            var monitor = sorted[index];
            var preferences = GetMonitorSettings(monitor.Id);
            var contents = new StackPanel { Spacing = 14 };
            var top = new Grid { ColumnSpacing = 12 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var identity = new StackPanel { Spacing = 3 };
            identity.Children.Add(Label(DisplayName(monitor), 18));
            var capability = monitor.SupportsBrightness ? T("NATIVE_SUPPORTS_BRIGHTNESS", "Brightness control available") : T("NATIVE_NO_BRIGHTNESS", "Brightness control unavailable");
            identity.Children.Add(Description($"{monitor.Connection} · {capability}"));
            top.Children.Add(identity);
            var order = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
            var up = new Button { Content = new FontIcon { Glyph = "\uE70E", FontSize = 12 }, IsEnabled = index > 0 };
            var down = new Button { Content = new FontIcon { Glyph = "\uE70D", FontSize = 12 }, IsEnabled = index < sorted.Count - 1 };
            AutomationProperties.SetName(up, T("NATIVE_MOVE_UP", "Move up"));
            AutomationProperties.SetName(down, T("NATIVE_MOVE_DOWN", "Move down"));
            ToolTipService.SetToolTip(up, T("NATIVE_MOVE_UP", "Move up"));
            ToolTipService.SetToolTip(down, T("NATIVE_MOVE_DOWN", "Move down"));
            var position = index;
            up.Click += (_, _) => MoveMonitor(sorted, position, -1);
            down.Click += (_, _) => MoveMonitor(sorted, position, 1);
            order.Children.Add(up);
            order.Children.Add(down);
            Grid.SetColumn(order, 1);
            top.Children.Add(order);
            contents.Children.Add(top);

            var name = new TextBox
            {
                Header = T("NATIVE_DISPLAY_NAME", "Display name"), Text = preferences.Name,
                PlaceholderText = monitor.Name, HorizontalAlignment = HorizontalAlignment.Stretch
            };
            name.LostFocus += (_, _) =>
            {
                var newName = name.Text.Trim();
                if (preferences.Name == newName) return;
                preferences.Name = newName;
                ((TextBlock)identity.Children[0]).Text = DisplayName(monitor);
                Save();
            };
            contents.Children.Add(name);
            contents.Children.Add(InlineRow(T("NATIVE_HIDE", "Hide from brightness panel"), Toggle(preferences.Hidden, value => { preferences.Hidden = value; Save(); })));
            var contrast = Toggle(preferences.ShowContrast, value => { preferences.ShowContrast = value; Save(); });
            contrast.IsEnabled = monitor.SupportsContrast;
            contents.Children.Add(InlineRow(T("NATIVE_SHOW_CONTRAST", "Show contrast slider"), contrast));
            if (!monitor.SupportsContrast) contents.Children.Add(Description(T("NATIVE_CONTRAST_UNAVAILABLE", "Contrast control is unavailable for this display.")));
            contents.Children.Add(Label(T("SETTINGS_MONITORS_NORMALIZE_TITLE", "Normalize Brightness")));
            contents.Children.Add(Description(T("NATIVE_NORMALIZE_DESCRIPTION", "Map the brightness panel's 0–100% range to these physical monitor brightness limits.")));
            var limits = new Grid { ColumnSpacing = 12 };
            limits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            limits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var min = Number(preferences.MinBrightness, 0, 99, _ => { });
            var max = Number(preferences.MaxBrightness, 1, 100, _ => { });
            min.Header = T("GENERIC_MINIMUM", "Min");
            max.Header = T("GENERIC_MAXIMUM", "Max");
            min.HorizontalAlignment = max.HorizontalAlignment = HorizontalAlignment.Stretch;
            min.Width = max.Width = double.NaN;
            var updating = false;
            min.ValueChanged += (_, args) =>
            {
                if (updating || double.IsNaN(args.NewValue)) return;
                updating = true;
                preferences.MinBrightness = Math.Clamp((int)args.NewValue, 0, preferences.MaxBrightness - 1);
                min.Value = preferences.MinBrightness;
                updating = false;
                Save();
            };
            max.ValueChanged += (_, args) =>
            {
                if (updating || double.IsNaN(args.NewValue)) return;
                updating = true;
                preferences.MaxBrightness = Math.Clamp((int)args.NewValue, preferences.MinBrightness + 1, 100);
                max.Value = preferences.MaxBrightness;
                updating = false;
                Save();
            };
            Grid.SetColumn(max, 1);
            limits.Children.Add(min);
            limits.Children.Add(max);
            contents.Children.Add(limits);
            PageContent.Children.Add(Card(contents));
        }
    }

    private void MoveMonitor(List<MonitorSnapshot> sorted, int index, int direction)
    {
        var other = index + direction;
        if (other < 0 || other >= sorted.Count) return;
        (sorted[index], sorted[other]) = (sorted[other], sorted[index]);
        for (var i = 0; i < sorted.Count; i++) GetMonitorSettings(sorted[i].Id).Order = i;
        Save();
        RenderPage();
    }

    private void RenderTime()
    {
        Heading(T("SETTINGS_TIME_TITLE", "Time of Day Adjustments"), T("NATIVE_TIME_DESCRIPTION", "Change the selected displays' brightness every day at a specified local time."));
        var add = AddButton(T("SETTINGS_TIME_ADD", "Add a time"));
        add.Click += (_, _) =>
        {
            _settings.Schedule.Add(new ScheduleEntry { Id = Guid.NewGuid().ToString("N"), Enabled = true, Time = DateTime.Now.AddHours(1).ToString("HH:mm", CultureInfo.InvariantCulture), Brightness = 50, MonitorId = "all" });
            Save();
            RenderPage();
        };
        PageContent.Children.Add(add);
        if (_settings.Schedule.Count == 0)
            PageContent.Children.Add(Description(T("NATIVE_NO_SCHEDULE", "No brightness adjustments scheduled. Add a time to get started."), new Thickness(0, 8, 0, 0)));
        foreach (var schedule in _settings.Schedule.OrderBy(s => s.Time).ToList())
        {
            var content = new StackPanel { Spacing = 14 };
            var validTime = DateTime.TryParseExact(schedule.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed);
            var timeWarning = new InfoBar
            {
                IsOpen = !validTime, IsClosable = false, Severity = InfoBarSeverity.Warning,
                Message = T("NATIVE_INVALID_TIME", "The saved time is invalid. Choose a valid time before enabling this schedule.")
            };
            var time = new TimePicker
            {
                Header = T("NATIVE_TIME", "Time"), ClockIdentifier = "24HourClock",
                Time = validTime ? parsed.TimeOfDay : TimeSpan.FromHours(12),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            content.Children.Add(EditorHeader(schedule.Enabled && validTime, value =>
            {
                schedule.Time = time.Time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                schedule.Enabled = value;
                validTime = true;
                timeWarning.IsOpen = false;
                Save();
            }, () => { _settings.Schedule.Remove(schedule); Save(); RenderPage(); }, T("SETTINGS_TIME_REMOVE", "Remove time")));
            time.TimeChanged += (_, args) =>
            {
                schedule.Time = args.NewTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                if (!validTime) schedule.Enabled = false;
                validTime = true;
                timeWarning.IsOpen = false;
                Save();
            };
            content.Children.Add(timeWarning);
            content.Children.Add(time);
            content.Children.Add(TargetChoice(schedule.MonitorId, value => { schedule.MonitorId = value; Save(); }));
            content.Children.Add(BrightnessEditor(schedule.Brightness, value => { schedule.Brightness = value; Save(); }));
            PageContent.Children.Add(Card(content));
        }
    }

    private void RenderHotkeys()
    {
        Heading(T("SETTINGS_SIDEBAR_HOTKEYS", "Hotkeys & Shortcuts"), T("SETTINGS_HOTKEYS_DESC", "Configure hotkeys to adjust the brightness of one or all displays."));
        PageContent.Children.Add(Description(T("NATIVE_HOTKEY_HINT", "Choose modifiers, a key, and an action, then enable the shortcut. Combinations used by other apps may be unavailable."), new Thickness(0, 0, 0, 8)));
        var add = AddButton(T("SETTINGS_HOTKEYS_ADD", "Add Hotkey"));
        add.Click += (_, _) =>
        {
            _settings.Hotkeys.Add(new HotkeyBinding { Id = Guid.NewGuid().ToString("N"), Enabled = false, Modifiers = 6, VirtualKey = 0x26, Action = "increase", MonitorId = "all", Step = 5 });
            Save();
            RenderPage();
        };
        PageContent.Children.Add(add);
        if (_settings.Hotkeys.Count == 0)
            PageContent.Children.Add(Description(T("NATIVE_NO_HOTKEYS", "No shortcuts configured. Add a hotkey to get started."), new Thickness(0, 8, 0, 0)));
        foreach (var hotkey in _settings.Hotkeys.ToList())
        {
            var content = new StackPanel { Spacing = 14 };
            var valid = hotkey.VirtualKey is > 0 and <= 255 && hotkey.Action is "increase" or "decrease" or "power";
            content.Children.Add(EditorHeader(hotkey.Enabled && valid, value =>
            {
                hotkey.Enabled = value && hotkey.VirtualKey is > 0 and <= 255 && hotkey.Action is "increase" or "decrease" or "power";
                Save();
                if (value && !hotkey.Enabled) RenderPage();
            }, () => { _settings.Hotkeys.Remove(hotkey); Save(); RenderPage(); }, T("SETTINGS_HOTKEYS_REMOVE", "Remove hotkey")));
            if (!valid)
                content.Children.Add(new InfoBar
                {
                    IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
                    Message = T("NATIVE_INVALID_HOTKEY", "Choose a valid key and action before enabling this shortcut.")
                });
            content.Children.Add(Label(T("NATIVE_MODIFIERS", "Modifiers")));
            var modifiers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            foreach (var (bit, label) in new (uint, string)[] { (2, "Ctrl"), (1, "Alt"), (4, "Shift"), (8, "Win") })
            {
                var modifier = new CheckBox { Content = label, IsChecked = (hotkey.Modifiers & bit) != 0 };
                modifier.Checked += (_, _) => { hotkey.Modifiers |= bit; Save(); };
                modifier.Unchecked += (_, _) => { hotkey.Modifiers &= ~bit; Save(); };
                modifiers.Children.Add(modifier);
            }
            content.Children.Add(modifiers);
            var keyChoices = KeyChoices();
            var selectedKey = hotkey.VirtualKey.ToString();
            if (!keyChoices.Any(k => k.Value == selectedKey)) keyChoices.Add((selectedKey, $"0x{hotkey.VirtualKey:X2}"));
            var key = Choice(keyChoices, selectedKey, value => { hotkey.VirtualKey = uint.Parse(value); Save(); RenderPage(); });
            key.Header = T("NATIVE_KEY", "Key");
            content.Children.Add(key);
            var action = Choice([
                ("increase", T("SETTINGS_HOTKEYS_INCREASE", "Increase Brightness")),
                ("decrease", T("SETTINGS_HOTKEYS_DECREASE", "Decrease Brightness")),
                ("power", T("PANEL_BUTTON_TURN_OFF_DISPLAYS", "Turn off displays"))],
                hotkey.Action, value => { hotkey.Action = value; Save(); RenderPage(); });
            action.Header = T("SETTINGS_HOTKEY_ACTION", "Action");
            content.Children.Add(action);
            content.Children.Add(TargetChoice(hotkey.MonitorId, value => { hotkey.MonitorId = value; Save(); }));
            if (hotkey.Action != "power")
            {
                var step = Number(hotkey.Step, 1, 100, value => { hotkey.Step = value; Save(); });
                step.Header = T("SETTINGS_HOTKEYS_LEVEL_TITLE", "Brightness level adjustment");
                step.Width = 240;
                content.Children.Add(step);
            }
            PageContent.Children.Add(Card(content));
        }
    }

    private void RenderIdle()
    {
        Heading(T("SETTINGS_TIME_IDLE_TITLE", "Idle Detection"), T("SETTINGS_TIME_IDLE_DESC", "When no input has been detected for a period of time, the brightness of all displays will be reduced."));
        PageContent.Children.Add(SettingRow(T("SETTINGS_TIME_IDLE_TITLE", "Idle Detection"), null,
            Toggle(_settings.IdleEnabled, value => { _settings.IdleEnabled = value; Save(); RenderPage(); })));
        var minutes = Number(_settings.IdleMinutes, 1, 1440, value => { _settings.IdleMinutes = value; Save(); });
        minutes.IsEnabled = _settings.IdleEnabled;
        PageContent.Children.Add(SettingRow(T("NATIVE_IDLE_MINUTES", "Wait time (minutes)"), null, minutes));
        var brightness = BrightnessEditor(_settings.IdleBrightness, value => { _settings.IdleBrightness = value; Save(); }, T("NATIVE_IDLE_BRIGHTNESS", "Brightness while idle"));
        foreach (var control in brightness.Children.OfType<Control>()) control.IsEnabled = _settings.IdleEnabled;
        PageContent.Children.Add(Card(brightness));
        PageContent.Children.Add(Description(T("NATIVE_IDLE_RESTORE", "Previous brightness is restored when you use the mouse or keyboard again."), new Thickness(0, 8, 0, 0)));
    }

    private void RenderAbout()
    {
        Heading(T("NATIVE_PORT_TITLE", "Twinkle Tray · WinUI 3"), T("NATIVE_PORT_DESCRIPTION", "A native Windows app based on the original Twinkle Tray layout and translations."));
        PageContent.Children.Add(SettingRow(T("NATIVE_IMPLEMENTED_TITLE", "Available features"), T("NATIVE_IMPLEMENTED", "DDC/CI and WMI brightness, contrast on compatible monitors, a tray panel, per-display settings, schedules, global hotkeys, and idle dimming.")));
        PageContent.Children.Add(SettingRow(T("NATIVE_PENDING_TITLE", "Features awaiting migration"), T("NATIVE_PENDING", "Advanced HDR/SDR control, gamma adjustment, light sensors, app profiles, and some advanced upstream DDC/CI features are not included yet.")));
        var links = new StackPanel { Spacing = 8 };
        links.Children.Add(new HyperlinkButton { Content = T("NATIVE_UPSTREAM", "Original project"), NavigateUri = new Uri("https://github.com/xanderfrangos/twinkle-tray"), Padding = new Thickness(0) });
        links.Children.Add(new HyperlinkButton { Content = T("NATIVE_FORK", "WinUI 3 fork"), NavigateUri = new Uri("https://github.com/BK927/twinkle-tray"), Padding = new Thickness(0) });
        links.Children.Add(Description(T("NATIVE_ATTRIBUTION", "Original Twinkle Tray by Xander Frangos and contributors. Original MIT license and copyright notices are retained.")));
        PageContent.Children.Add(Card(links));
    }

    private MonitorSettings GetMonitorSettings(string id)
    {
        if (!_settings.Monitors.TryGetValue(id, out var preferences))
        {
            preferences = new MonitorSettings { Order = _settings.Monitors.Count };
            _settings.Monitors[id] = preferences;
        }
        return preferences;
    }

    private string DisplayName(MonitorSnapshot monitor)
    {
        var preferred = GetMonitorSettings(monitor.Id).Name;
        return string.IsNullOrWhiteSpace(preferred) ? monitor.Name : preferred;
    }

    private ComboBox TargetChoice(string selectedId, Action<string> changed)
    {
        var options = new List<(string Value, string Label)> { ("all", T("GENERIC_ALL_DISPLAYS", "All Displays")) };
        options.AddRange(_monitors.OrderBy(m => GetMonitorSettings(m.Id).Order).Select(m => (m.Id, DisplayName(m))));
        if (!options.Any(o => o.Value == selectedId))
            options.Add((selectedId, T("NATIVE_DISCONNECTED", "Disconnected display")));
        var choice = Choice(options, selectedId, changed);
        choice.Header = T("NATIVE_DISPLAYS", "Displays");
        choice.MaxWidth = 500;
        choice.HorizontalAlignment = HorizontalAlignment.Stretch;
        return choice;
    }

    private static Grid EditorHeader(bool enabled, Action<bool> changed, Action deleted, string deleteLabel)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var toggle = Toggle(enabled, changed);
        toggle.Header = T("NATIVE_ENABLED", "Enabled");
        var delete = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 16 }, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(delete, deleteLabel);
        ToolTipService.SetToolTip(delete, deleteLabel);
        delete.Click += (_, _) => deleted();
        Grid.SetColumn(delete, 1);
        grid.Children.Add(toggle);
        grid.Children.Add(delete);
        return grid;
    }

    private static StackPanel BrightnessEditor(int value, Action<int> changed, string? title = null)
    {
        var content = new StackPanel { Spacing = 6 };
        var label = Label($"{title ?? T("PANEL_LABEL_BRIGHTNESS", "Brightness")} · {value}%");
        content.Children.Add(label);
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = value, StepFrequency = 1, TickFrequency = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(slider, title ?? T("PANEL_LABEL_BRIGHTNESS", "Brightness"));
        slider.ValueChanged += (_, args) =>
        {
            var level = (int)Math.Round(args.NewValue);
            label.Text = $"{title ?? T("PANEL_LABEL_BRIGHTNESS", "Brightness")} · {level}%";
            changed(level);
        };
        content.Children.Add(slider);
        return content;
    }

    private static ToggleSwitch Toggle(bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = T("GENERIC_ON", "On"), OffContent = T("GENERIC_OFF", "Off"), MinWidth = 92, VerticalAlignment = VerticalAlignment.Center };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    private static NumberBox Number(int value, int min, int max, Action<int> changed)
    {
        var number = new NumberBox
        {
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), SmallChange = 1,
            LargeChange = 10, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 132, HorizontalAlignment = HorizontalAlignment.Left,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten
        };
        number.ValueChanged += (_, args) =>
        {
            if (!double.IsNaN(args.NewValue)) changed(Math.Clamp((int)Math.Round(args.NewValue), min, max));
        };
        return number;
    }

    private static ComboBox Choice(IEnumerable<(string Value, string Label)> values, string selected, Action<string> changed)
    {
        var combo = new ComboBox { MinWidth = 220, MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (value, label) in values)
        {
            var option = new ComboBoxItem { Content = label, Tag = value };
            combo.Items.Add(option);
            if (value == selected) combo.SelectedItem = option;
        }
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem item && item.Tag is string value) changed(value); };
        return combo;
    }

    private static Button AddButton(string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = "\uE710", FontSize = 14 });
        content.Children.Add(new TextBlock { Text = label });
        return new Button { Content = content, Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
    }

    private Border SettingRow(string title, string? description, FrameworkElement? control = null)
    {
        var row = new Grid { ColumnSpacing = 22 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Label(title));
        if (!string.IsNullOrWhiteSpace(description)) text.Children.Add(Description(description));
        row.Children.Add(text);
        if (control is not null)
        {
            Grid.SetColumn(control, 1);
            control.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(control, title);
            row.Children.Add(control);
        }
        return Card(row);
    }

    private static Grid InlineRow(string title, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Label(title);
        label.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(label);
        Grid.SetColumn(control, 1);
        AutomationProperties.SetName(control, title);
        grid.Children.Add(control);
        return grid;
    }

    private Border Card(UIElement child) => new()
    {
        Child = child, Style = (Style)Root.Resources["SettingsCardStyle"]
    };

    private static TextBlock Label(string text, double fontSize = 14) => new()
    {
        Text = text, FontSize = fontSize, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock Description(string text, Thickness? margin = null) => new()
    {
        Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.72,
        Margin = margin ?? new Thickness(0)
    };

    private static List<(string Value, string Label)> KeyChoices()
    {
        var keys = new List<(string, string)>
        {
            ("38", "↑ Up"), ("40", "↓ Down"), ("37", "← Left"), ("39", "→ Right"),
            ("33", "Page Up"), ("34", "Page Down"), ("36", "Home"), ("35", "End"),
            ("32", "Space"), ("187", "+"), ("189", "−")
        };
        for (var i = 0; i <= 9; i++) keys.Add(((0x30 + i).ToString(), i.ToString()));
        for (var i = 0; i < 26; i++) keys.Add(((0x41 + i).ToString(), ((char)('A' + i)).ToString()));
        for (var i = 1; i <= 24; i++) keys.Add(((0x6F + i).ToString(), $"F{i}"));
        return keys;
    }

    internal void ShowError(string message) { SettingsError.Message = message; SettingsError.IsOpen = true; }

    private void Save() => _saveSettings();
}
