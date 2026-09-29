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
    private AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly Action _refreshMonitors;
    private readonly SettingsActions _actions;
    private IReadOnlyList<MonitorSnapshot> _monitors;
    private string _page = "general";

    public SettingsWindow(AppSettings settings, IReadOnlyList<MonitorSnapshot> monitors, Action saveSettings, Action refreshMonitors, SettingsActions? actions = null)
    {
        _settings = settings;
        _monitors = monitors;
        _saveSettings = saveSettings;
        _refreshMonitors = refreshMonitors;
        _actions = actions ?? new SettingsActions();
        InitializeComponent();
        InitializeSettingsLayout();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        Root.Loaded += (_, _) =>
        {
            SetInitialSizeAndPosition();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                PageContent.UpdateLayout();
                RestorePageState(_renderVersion);
            });
        };
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
        var unchanged = _monitors.SequenceEqual(monitors);
        _monitors = monitors;
        if (!unchanged && (_page is "monitors" or "features" or "time" or "hotkeys" or "profiles" or "sensors")) RenderPage();
    }

    public void ShowStatus(string message)
    {
        StatusBar.Message = message;
        StatusBar.IsOpen = !string.IsNullOrWhiteSpace(message);
    }

    public void ReloadSettings(AppSettings settings)
    {
        _settings = settings;
        _features.Clear();
        _sensorDevices = [];
        _pageStates.Clear();
        _lastFocusKeys.Clear();
        _viewRestorePending = false;
        _renderedPage = null;
        Localize();
        ApplyTheme();
        RenderPage();
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
        UpdateNavigationLayout(Root.ActualWidth);
    }

    internal int VerifyPagesForSmokeTest()
    {
        var originalPage = _page;
        var verified = 0;
        try
        {
            foreach (var page in new[] { "general", "monitors", "features", "time", "hotkeys", "idle", "profiles", "sensors", "advanced", "updates", "about" })
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

    private static readonly Dictionary<string, string> LabelKeys = new();
    private static string T(string key, string fallback)
    {
        var text = LocalizationService.Get(key, fallback);
        LabelKeys[text] = key;
        return text;
    }
    private static string LabelKey(string text) => LabelKeys.GetValueOrDefault(text, text);

    private void Localize()
    {
        LocalizationService.Configure(_settings.Language);
        try { Root.Language = CultureInfo.GetCultureInfo(LocalizationService.CurrentLanguage).Name; }
        catch (CultureNotFoundException) { Root.Language = "en"; }
        Title = T("SETTINGS_TITLE", "Twinkle Tray Native Settings");
        WindowTitle.Text = Title;
        GeneralNavigation.Content = T("SETTINGS_SIDEBAR_GENERAL", "General");
        MonitorsNavigation.Content = T("SETTINGS_SIDEBAR_MONITORS", "Monitor Settings");
        FeaturesNavigation.Content = T("SETTINGS_SIDEBAR_FEATURES", "DDC/CI Features");
        TimeNavigation.Content = T("SETTINGS_SIDEBAR_TIME", "Time Adjustments");
        HotkeysNavigation.Content = T("SETTINGS_SIDEBAR_HOTKEYS", "Hotkeys & Shortcuts");
        IdleNavigation.Content = T("SETTINGS_TIME_IDLE_TITLE", "Idle Detection");
        AboutNavigation.Content = T("NATIVE_ABOUT", "About");
        ProfilesNavigation.Content = T("SETTINGS_PROFILES_TITLE", "Profiles");
        SensorsNavigation.Content = T("SETTINGS_LIGHT_SENSOR_TITLE", "Light Sensor");
        AdvancedNavigation.Content = T("NATIVE_ADVANCED", "Advanced");
        UpdatesNavigation.Content = T("SETTINGS_SIDEBAR_UPDATES", "Updates");
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = _settings.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ApplySettingsBackdrop();
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string page)
        {
            if (_page == page && _renderedPage == page) return;
            _page = page;
            RenderPage();
            if (Navigation.PaneDisplayMode == NavigationViewPaneDisplayMode.LeftCompact) Navigation.IsPaneOpen = false;
        }
    }

    private void RenderPage()
    {
        if (PageContent is null || _isRendering) return;
        CapturePageState();
        var version = ++_renderVersion;
        _viewRestorePending = true;
        _isRendering = true;
        _cardSummaries.Clear();
        try
        {
            PageContent.Children.Clear();
            switch (_page)
            {
            case "monitors": RenderMonitors(); break;
            case "features": RenderFeatures(); break;
            case "time": RenderTimeAdvanced(); break;
            case "hotkeys": RenderHotkeysAdvanced(); break;
            case "idle": RenderIdle(); break;
            case "profiles": RenderProfiles(); break;
            case "sensors": RenderSensors(); break;
            case "advanced": RenderAdvanced(); break;
            case "updates": RenderUpdates(); break;
            case "about": RenderAbout(); break;
            default: RenderGeneral(); break;
            }
            _renderedPage = _page;
            WrapFieldHeaders(PageContent);
            _pageEditors = IndexEditors();
        }
        finally { _isRendering = false; }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => RestorePageState(version));
    }

    private void Heading(string title, string? description = null)
    {
        PageContent.Children.Add(new TextBlock
        {
            Text = title, Style = SharedStyle("PageHeadingTextStyle")
        });
        if (!string.IsNullOrEmpty(description))
            PageContent.Children.Add(Description(description, new Thickness(0, 0, 0, 8)));
    }

    private void RenderGeneral()
    {
        Heading(T("SETTINGS_GENERAL_TITLE", "General"), T("NATIVE_GENERAL_DESCRIPTION", "Personalize Twinkle Tray Native's appearance and behavior. Changes are saved automatically."));
        Section(T("NATIVE_APPEARANCE", "Appearance"));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_THEME_TITLE", "Theme"), null,
            Choice([("system", T("SETTINGS_GENERAL_THEME_SYSTEM", "System preferences (default)")),
                    ("light", T("SETTINGS_GENERAL_THEME_LIGHT", "Light")),
                    ("dark", T("SETTINGS_GENERAL_THEME_DARK", "Dark"))], _settings.Theme,
                value => { _settings.Theme = value; ApplyTheme(); Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_GENERAL_LANGUAGE_TITLE", "Language"), null,
            Choice(LanguageChoices(),
                _settings.Language, value => { _settings.Language = value; Localize(); Save(); RenderPage(); })));
        RenderGeneralExtensions();
    }

    private void RenderMonitors()
    {
        Heading(T("SETTINGS_SIDEBAR_MONITORS", "Monitor Settings"), T("NATIVE_MONITORS_DESCRIPTION", "Customize display names, their order, and brightness ranges."));
        PageContent.Children.Add(SettingRow(T("SETTINGS_MONITORS_RATE_TITLE", "Brightness update rate") + " (ms)", T("SETTINGS_MONITORS_RATE_DESC", "Increase the interval if your displays flicker while changing brightness."),
            Number(_settings.UpdateIntervalMilliseconds, 16, 5000, value => { _settings.UpdateIntervalMilliseconds = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_MONITORS_HIDE_INTERNAL_TITLE", "Hide Inactive Internal Display"), T("SETTINGS_MONITORS_HIDE_INTERNAL_DESC", "Hide the internal brightness slider when the laptop lid is closed."),
            Toggle(_settings.HideClosedLid, value => { _settings.HideClosedLid = value; Save(); })));
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
            var contents = new StackPanel { Spacing = 12 };
            var top = new Grid { ColumnSpacing = 12 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var identity = new StackPanel { Spacing = 4 };
            identity.Children.Add(Label(DisplayName(monitor), 18));
            var capability = monitor.SupportsBrightness ? T("NATIVE_SUPPORTS_BRIGHTNESS", "Brightness control available") : T("NATIVE_NO_BRIGHTNESS", "Brightness control unavailable");
            identity.Children.Add(Description($"{monitor.Connection} · {capability}"));
            top.Children.Add(identity);
            var order = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
            var up = new Button { Style = SharedStyle("ToolbarButtonStyle"), Content = new FontIcon { Glyph = "\uE70E", FontSize = 14 }, IsEnabled = index > 0 };
            var down = new Button { Style = SharedStyle("ToolbarButtonStyle"), Content = new FontIcon { Glyph = "\uE70D", FontSize = 14 }, IsEnabled = index < sorted.Count - 1 };
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

            var name = TextEditor(T("NATIVE_DISPLAY_NAME", "Display name"), preferences.Name, newName =>
            {
                if (preferences.Name == newName) return;
                preferences.Name = newName;
                ((TextBlock)identity.Children[0]).Text = DisplayName(monitor);
                Save();
            }, "monitor:" + monitor.Id + ":name");
            name.Input.PlaceholderText = monitor.Name;
            contents.Children.Add(name);
            contents.Children.Add(InlineRow(T("NATIVE_SHOW_NAME", "Show monitor name on slider"), Toggle(preferences.ShowName, value => { preferences.ShowName = value; Save(); })));
            contents.Children.Add(InlineRow(T("NATIVE_SHOW_VALUE", "Show brightness value on slider"), Toggle(preferences.ShowValue, value => { preferences.ShowValue = value; Save(); })));
            contents.Children.Add(TextEditor(T("NATIVE_SLIDER_GLYPH", "Slider icon (optional character)"), preferences.IconGlyph, value => { preferences.IconGlyph = value; Save(); }));
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
            PageContent.Children.Add(ExpandableCard("monitor:" + monitor.Id, () => DisplayName(monitor),
                () => $"{EnabledSummary(!preferences.Hidden)} · {monitor.Brightness:0}% · {preferences.MinBrightness}–{preferences.MaxBrightness}%", contents, index == 0));
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

    private void RenderIdle()
    {
        Heading(T("SETTINGS_TIME_IDLE_TITLE", "Idle Detection"), T("SETTINGS_TIME_IDLE_DESC", "When no input has been detected for a period of time, the brightness of all displays will be reduced."));
        PageContent.Children.Add(SettingRow(T("SETTINGS_TIME_IDLE_TITLE", "Idle Detection"), null,
            Toggle(_settings.IdleEnabled, value => { _settings.IdleEnabled = value; Save(); RenderPage(); })));
        var minutes = Number(_settings.IdleMinutes, 0, 1440, value =>
        {
            _settings.IdleMinutes = value;
            var adjustSeconds = value == 0 && _settings.IdleSeconds == 0;
            if (adjustSeconds) _settings.IdleSeconds = 1;
            Save();
            if (adjustSeconds) RenderPage();
        });
        minutes.IsEnabled = _settings.IdleEnabled;
        PageContent.Children.Add(SettingRow(T("NATIVE_IDLE_MINUTES", "Wait time (minutes)"), null, minutes));
        var brightness = BrightnessEditor(_settings.IdleBrightness, value => { _settings.IdleBrightness = value; Save(); }, T("NATIVE_IDLE_BRIGHTNESS", "Brightness while idle"));
        foreach (var control in brightness.Children.OfType<Control>()) control.IsEnabled = _settings.IdleEnabled;
        PageContent.Children.Add(Card(brightness));
        PageContent.Children.Add(Description(T("NATIVE_IDLE_RESTORE", "Previous brightness is restored when you use the mouse or keyboard again."), new Thickness(0, 8, 0, 0)));
        PageContent.Children.Add(SettingRow(T("NATIVE_IDLE_SECONDS", "Additional wait time (seconds)"), null,
            Number(_settings.IdleSeconds, 0, 59, value =>
            {
                _settings.IdleSeconds = value;
                var adjustMinutes = value == 0 && _settings.IdleMinutes == 0;
                if (adjustMinutes) _settings.IdleMinutes = 1;
                Save();
                if (adjustMinutes) RenderPage();
            })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_TIME_IDLE_FS_TITLE", "Fullscreen apps block idle detection"), T("SETTINGS_TIME_IDLE_FS_DESC", "The focused fullscreen app prevents idle dimming."),
            Toggle(_settings.IdleCheckFullscreen, value => { _settings.IdleCheckFullscreen = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("SETTINGS_TIME_IDLE_MEDIA_TITLE", "Media blocks idle detection"), T("SETTINGS_TIME_IDLE_MEDIA_DESC", "Media playback reported by Windows prevents idle dimming."),
            Toggle(_settings.IdleCheckMedia, value => { _settings.IdleCheckMedia = value; Save(); })));
        PageContent.Children.Add(SettingRow(T("NATIVE_IDLE_RESTORE_DELAY", "Idle restore delay (seconds)"), null,
            Number(_settings.IdleRestoreSeconds, 0, 3600, value => { _settings.IdleRestoreSeconds = value; Save(); })));
    }

    private void RenderAbout()
    {
        Heading(T("NATIVE_PORT_TITLE", "Twinkle Tray Native"), T("NATIVE_PORT_DESCRIPTION", "A native Windows app based on the original Twinkle Tray layout and translations."));
        PageContent.Children.Add(SettingRow(T("NATIVE_SUPPORTED_PLATFORMS", "Supported platform"), T("NATIVE_SUPPORTED_PLATFORMS_DESC", "Windows x64 is supported. macOS, Apple hardware and Windows ARM64 execution are not currently supported.")));
        PageContent.Children.Add(SettingRow(T("NATIVE_IMPLEMENTED_TITLE", "Available features"), T("NATIVE_FULL_FEATURES", "Hardware brightness and DDC/CI features, HDR SDR brightness, software dimming, calibrated monitor ranges, solar schedules, multi-action shortcuts, app profiles, ambient light sensors, and idle dimming.")));
        PageContent.Children.Add(SettingRow(T("NATIVE_HARDWARE_SUPPORT", "Hardware support"), T("NATIVE_HARDWARE_SUPPORT_DESC", "Available controls depend on the capabilities reported by your displays and sensors. Enable DDC/CI in the display's own settings to access its hardware controls.")));
        var links = new StackPanel { Spacing = 8 };
        links.Children.Add(new HyperlinkButton { Content = T("NATIVE_UPSTREAM", "Original project"), NavigateUri = new Uri("https://github.com/xanderfrangos/twinkle-tray"), Padding = new Thickness(0) });
        links.Children.Add(new HyperlinkButton { Content = T("NATIVE_FORK", "Twinkle Tray Native project"), NavigateUri = new Uri("https://github.com/BK927/twinkle-tray-native"), Padding = new Thickness(0) });
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
        var delete = new Button { Style = SharedStyle("ToolbarButtonStyle"), Content = new FontIcon { Glyph = "\uE74D", FontSize = 16 }, VerticalAlignment = VerticalAlignment.Center };
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
        var content = new StackPanel { Spacing = 4 };
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
        var combo = new ComboBox { MinWidth = 0, Width = 240, MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (value, label) in values)
        {
            var option = new ComboBoxItem { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Tag = value };
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
        return new Button { Content = content, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
    }

    private Border SettingRow(string title, string? description, FrameworkElement? control = null)
    {
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Label(title));
        if (!string.IsNullOrWhiteSpace(description)) text.Children.Add(Description(description));
        Border result;
        if (control is not null)
        {
            AutomationProperties.SetName(control, title);
            AutomationProperties.SetAutomationId(control, "setting:" + LabelKey(title));
            result = Card(ResponsiveRow(text, control));
        }
        else result = Card(text);
        return result;
    }

    private static Grid InlineRow(string title, FrameworkElement control)
    {
        var label = Label(title);
        label.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, title);
        AutomationProperties.SetAutomationId(control, "setting:" + LabelKey(title));
        return ResponsiveRow(label, control);
    }

    private Border Card(UIElement child) => new()
    {
        Child = child, Style = SharedStyle("SettingsCardStyle")
    };

    private static TextBlock Label(string text, double fontSize = 14) => new()
    {
        Text = text, Style = SharedStyle("SettingLabelTextStyle"), FontSize = fontSize
    };

    private static TextBlock Description(string text, Thickness? margin = null) => new()
    {
        Text = text, Style = SharedStyle("SecondaryTextStyle"),
        Margin = margin ?? new Thickness(0)
    };

    private static List<(string Value, string Label)> KeyChoices()
    {
        var keys = new List<(string, string)>
        {
            ("38", "↑ " + T("NATIVE_KEY_Up", "Up")), ("40", "↓ " + T("NATIVE_KEY_Down", "Down")),
            ("37", "← " + T("NATIVE_KEY_Left", "Left")), ("39", "→ " + T("NATIVE_KEY_Right", "Right")),
            ("33", T("NATIVE_KEY_PageUp", "Page up")), ("34", T("NATIVE_KEY_PageDown", "Page down")),
            ("36", T("NATIVE_KEY_Home", "Home")), ("35", T("NATIVE_KEY_End", "End")),
            ("32", T("NATIVE_KEY_Space", "Space")), ("187", "+"), ("189", "−")
        };
        for (var i = 0; i <= 9; i++) keys.Add(((0x30 + i).ToString(), i.ToString()));
        for (var i = 0; i < 26; i++) keys.Add(((0x41 + i).ToString(), ((char)('A' + i)).ToString()));
        for (var i = 1; i <= 24; i++) keys.Add(((0x6F + i).ToString(), $"F{i}"));
        foreach (var key in Enum.GetValues<Windows.System.VirtualKey>().OrderBy(key => (uint)key))
        {
            var code = (uint)key;
            if (code == 0 || code > 255 || keys.Any(existing => existing.Item1 == code.ToString(CultureInfo.InvariantCulture))) continue;
            if (key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.LeftControl or Windows.System.VirtualKey.RightControl
                or Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.LeftShift or Windows.System.VirtualKey.RightShift
                or Windows.System.VirtualKey.Menu or Windows.System.VirtualKey.LeftMenu or Windows.System.VirtualKey.RightMenu
                or Windows.System.VirtualKey.LeftWindows or Windows.System.VirtualKey.RightWindows) continue;
            var name = System.Text.RegularExpressions.Regex.Replace(key.ToString(), "([a-z])([A-Z])", "$1 $2");
            keys.Add((code.ToString(CultureInfo.InvariantCulture), code is >= 0x60 and <= 0x69
                ? $"{T("NATIVE_KEYPAD", "Number pad")} {code - 0x60}" : T("NATIVE_KEY_" + key, name)));
        }
        return keys;
    }

    internal void ShowError(string message) { SettingsError.Message = message; SettingsError.IsOpen = true; }

    private void Save()
    {
        if (_isRendering || _restoringView) return;
        _saveSettings();
        foreach (var update in _cardSummaries.ToArray()) update();
    }
}
