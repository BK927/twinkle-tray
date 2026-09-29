using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TwinkleTray.Core;
using TwinkleTray.Hardware;
using TwinkleTray.WinUI.Services;
using Windows.Graphics;
using Windows.System;

namespace TwinkleTray.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly AppController _controller;
    private readonly Dictionary<string, Slider> _sliders = new();
    private readonly Dictionary<string, TextBlock> _levels = new();
    private readonly Dictionary<string, (string Id, double Value, bool Contrast, bool Linked)> _pending = new();
    private readonly Dictionary<string, (string Id, byte Code, double Value)> _pendingFeatures = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private bool _updating, _closing, _flushing;
    private int _height = 230;
    private long _deactivatedAt;
    private readonly nint _hwnd;
    public bool IsShown { get; private set; }

    internal MainWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico"));
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = _controller.IsDemo;
        // WS_EX_TOOLWINDOW excludes this transient flyout from Alt+Tab and the taskbar.
        if (!_controller.IsDemo) SetWindowLongPtr(_hwnd, -20, (nint)(GetWindowLongPtr(_hwnd, -20).ToInt64() | 0x80));
        if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
            Root.Background = null;
        }
        Activated += (_, args) => { if (args.WindowActivationState == WindowActivationState.Deactivated && !_controller.IsDemo) { _deactivatedAt = Environment.TickCount64; HidePanel(); } };
        AppWindow.Closing += (_, args) => { if (!_closing) { args.Cancel = true; HidePanel(); } };
        _debounce.Tick += async (_, _) => await FlushAsync();
        ApplySettings();
    }

    internal void ApplySettings()
    {
        Root.RequestedTheme = _controller.Settings.Theme switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };
        _debounce.Interval = TimeSpan.FromMilliseconds(_controller.Settings.UpdateIntervalMilliseconds);
        if (_controller.Settings.UseAcrylic && Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop ??= new DesktopAcrylicBackdrop(); Root.Background = null;
        }
        else
        {
            SystemBackdrop = null;
            Root.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        }
        int corner = _controller.Settings.WindowsStyle == "win10" ? 1 : 2;
        DwmSetWindowAttribute(_hwnd, 33, ref corner, sizeof(int));
        Heading.Text = T("PANEL_TITLE", "Adjust Brightness");
        ToolTipService.SetToolTip(LinkButton, T("PANEL_BUTTON_LINK_LEVELS", "Link levels"));
        AutomationProperties.SetName(LinkButton, T("PANEL_BUTTON_LINK_LEVELS", "Link levels"));
        ToolTipService.SetToolTip(PowerButton, T("PANEL_BUTTON_TURN_OFF_DISPLAYS", "Turn off displays"));
        AutomationProperties.SetName(PowerButton, T("PANEL_BUTTON_TURN_OFF_DISPLAYS", "Turn off displays"));
        ToolTipService.SetToolTip(SettingsButton, T("GENERIC_SETTINGS", "Settings"));
        AutomationProperties.SetName(SettingsButton, T("GENERIC_SETTINGS", "Settings"));
        ToolTipService.SetToolTip(RefreshButton, T("GENERIC_REFRESH_DISPLAYS", "Refresh displays"));
        AutomationProperties.SetName(RefreshButton, T("GENERIC_REFRESH_DISPLAYS", "Refresh displays"));
        LinkButton.IsChecked = _controller.Settings.LinkedBrightness;
        RenderMonitors();
    }

    internal void RenderMonitors()
    {
        _updating = true;
        try
        {
            MonitorList.Children.Clear(); _sliders.Clear(); _levels.Clear();
            var monitors = _controller.VisibleMonitors.ToList();
            foreach (var monitor in monitors)
            {
                var preferences = _controller.Preferences(monitor.Id);
                var card = new StackPanel { Spacing = 3 };
                var title = new Grid { ColumnSpacing = 10 };
                title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                title.Children.Add(new FontIcon { Glyph = string.IsNullOrWhiteSpace(preferences.IconGlyph) ? (monitor.Connection.Contains("WMI", StringComparison.OrdinalIgnoreCase) ? "\uE770" : "\uE7F4") : preferences.IconGlyph, FontSize = 20 });
                var label = new TextBlock { Text = preferences.ShowName ? _controller.DisplayName(monitor) : "", FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(label, 1); title.Children.Add(label);
                var level = new TextBlock { Text = $"{_controller.LogicalBrightness(monitor):0}", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, MinWidth = 28, TextAlignment = TextAlignment.Right };
                Grid.SetColumn(level, 2); title.Children.Add(level); _levels[monitor.Id] = level;
                level.Visibility = preferences.ShowValue ? Visibility.Visible : Visibility.Collapsed;
                card.Children.Add(title);
                var slider = MakeSlider(monitor.Id, _controller.LogicalBrightness(monitor), false, _controller.DisplayName(monitor));
                slider.IsEnabled = _controller.CanControl(monitor);
                card.Children.Add(slider); _sliders[monitor.Id] = slider;
                if (!_controller.CanControl(monitor))
                {
                    level.Text = "—";
                    card.Children.Add(new TextBlock { Text = T("GENERIC_NOT_SUPPORTED", "Not supported") + " · " + monitor.Connection, FontSize = 12, Opacity = .6 });
                }
                bool configuredContrast = preferences.Features.TryGetValue(0x12, out var contrastFeature) && contrastFeature.Enabled;
                if (preferences.ShowContrast && monitor.SupportsContrast && !configuredContrast)
                {
                    card.Children.Add(new TextBlock { Text = T("PANEL_LABEL_CONTRAST", "Contrast"), FontSize = 12, Opacity = .7 });
                    card.Children.Add(MakeSlider(monitor.Id, monitor.Contrast ?? 50, true, T("PANEL_LABEL_CONTRAST", "Contrast") + " " + _controller.DisplayName(monitor)));
                }
                foreach (var pair in preferences.Features.Where(f => f.Value.Enabled && !f.Value.LinkedToBrightness))
                {
                    var feature = _controller.Features(monitor.Id).FirstOrDefault(f => f.Code == pair.Key);
                    if (feature is null) continue;
                    string name = string.IsNullOrWhiteSpace(pair.Value.Name) ? feature.Name : pair.Value.Name;
                    var featureHeading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
                    if (pair.Value.IconType == "text" && pair.Value.IconText.Length > 0) featureHeading.Children.Add(new TextBlock { Text = pair.Value.IconText, FontSize = 12 });
                    else if (pair.Value.IconType == "image" && Path.IsPathFullyQualified(pair.Value.IconPath) && File.Exists(pair.Value.IconPath))
                    {
                        try { featureHeading.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(pair.Value.IconPath)), Width = 16, Height = 16 }); } catch (Exception exception) when (exception is ArgumentException or System.Runtime.InteropServices.COMException) { Program.Log(exception); }
                    }
                    else if (pair.Value.IconGlyph.Length > 0) featureHeading.Children.Add(new FontIcon { Glyph = pair.Value.IconGlyph, FontSize = 14 });
                    featureHeading.Children.Add(new TextBlock { Text = name, FontSize = 12, Opacity = .7 });
                    card.Children.Add(featureHeading);
                    if (feature.AllowedValues.Count > 0)
                    {
                        var choice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                        foreach (uint item in feature.AllowedValues) choice.Items.Add(new ComboBoxItem { Content = FeatureValueName(feature.Code, item), Tag = item });
                        choice.SelectedItem = choice.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (uint)i.Tag == feature.Current);
                        AutomationProperties.SetName(choice, name + " " + _controller.DisplayName(monitor));
                        choice.SelectionChanged += async (_, _) =>
                        {
                            if (_updating || choice.SelectedItem is not ComboBoxItem selected) return;
                            try { await _controller.SetFeatureAsync(monitor.Id, feature.Code, (uint)selected.Tag); }
                            catch (Exception exception) { ShowError(exception.Message); }
                        };
                        card.Children.Add(choice);
                    }
                    else
                    {
                        var control = new Slider { Minimum = pair.Value.Min, Maximum = Math.Max(pair.Value.Min + 1, Math.Min(pair.Value.Max, feature.Maximum)), Value = feature.Current, StepFrequency = 1 };
                        AutomationProperties.SetName(control, name + " " + _controller.DisplayName(monitor));
                        control.ValueChanged += (_, args) =>
                        {
                            if (_updating) return;
                            _pendingFeatures[monitor.Id + ":" + feature.Code] = (monitor.Id, feature.Code, args.NewValue);
                            _debounce.Stop(); _debounce.Start();
                        };
                        card.Children.Add(control);
                    }
                }
                MonitorList.Children.Add(card);
            }
            if (monitors.Count == 0)
            {
                MonitorList.Children.Add(new FontIcon { Glyph = "\uE7F4", FontSize = 32, Opacity = .5, Margin = new Thickness(0, 14, 0, 5) });
                MonitorList.Children.Add(new TextBlock { Text = T("GENERIC_NO_COMPATIBLE_DISPLAYS", "No compatible displays found. Check that DDC/CI is enabled in your monitor settings."), TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = .75, Margin = new Thickness(6) });
            }
            LinkButton.IsEnabled = monitors.Count(_controller.CanControl) > 1;
            PowerButton.IsEnabled = _controller.Settings.PowerOffMode is "windows" or "both" || (_controller.Settings.PowerOffMode == "ddc" && monitors.Any(x => x.Connection.Contains("DDC", StringComparison.OrdinalIgnoreCase)));
            _height = Math.Clamp(112 + monitors.Sum(x => (_controller.Preferences(x.Id).ShowContrast && x.SupportsContrast ? 138 : 74) + _controller.Preferences(x.Id).Features.Count(f => f.Value.Enabled && !f.Value.LinkedToBrightness) * 58), 216, 650);
            Status.Text = _controller.IsDemo ? "DEMO · " + T("GENERIC_ALL_DISPLAYS", "All displays") : "Twinkle Tray · WinUI 3";
            if (IsShown) PositionPanel();
        }
        finally { _updating = false; }
    }

    private Slider MakeSlider(string id, double value, bool contrast, string name)
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Value = value, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(slider, name + " " + T(contrast ? "PANEL_LABEL_CONTRAST" : "PANEL_LABEL_BRIGHTNESS", contrast ? "Contrast" : "Brightness"));
        slider.ValueChanged += (_, args) =>
        {
            if (_updating) return;
            bool linked = !contrast && _controller.Settings.LinkedBrightness;
            _pending[linked ? "all:brightness" : id + (contrast ? ":contrast" : ":brightness")] = (id, args.NewValue, contrast, linked);
            if (!contrast && _levels.TryGetValue(id, out var level)) level.Text = $"{args.NewValue:0}";
            if (!contrast && _controller.Settings.LinkedBrightness)
            {
                _updating = true;
                foreach (var pair in _sliders.Where(x => x.Value.IsEnabled)) { pair.Value.Value = args.NewValue; _levels[pair.Key].Text = $"{args.NewValue:0}"; }
                _updating = false;
            }
            _debounce.Stop(); _debounce.Start();
        };
        slider.PointerWheelChanged += (_, args) =>
        {
            slider.Value = Math.Clamp(slider.Value + Math.Sign(args.GetCurrentPoint(slider).Properties.MouseWheelDelta) * _controller.Settings.ScrollStep * (_controller.Settings.InvertScroll ? -1 : 1), 0, 100);
            args.Handled = true;
        };
        return slider;
    }

    private async Task FlushAsync()
    {
        _debounce.Stop();
        if (_flushing) return;
        _flushing = true;
        try
        {
            while (_pending.Count > 0 || _pendingFeatures.Count > 0)
            {
                var writes = _pending.Values.ToArray(); _pending.Clear();
                foreach (var write in writes)
                    await _controller.TrySetAsync(write.Id, write.Value, write.Contrast, write.Linked);
                var featureWrites = _pendingFeatures.Values.ToArray(); _pendingFeatures.Clear();
                foreach (var write in featureWrites)
                    try { await _controller.SetFeatureAsync(write.Id, write.Code, write.Value); } catch (Exception exception) { ShowError(exception.Message); }
            }
        }
        finally { _flushing = false; }
    }

    public void ShowPanel()
    {
        IsShown = true; PositionPanel(); AppWindow.Show(); Activate(); SetForegroundWindow(_hwnd);
    }
    public void HidePanel() { IsShown = false; AppWindow.Hide(); }
    public void TogglePanel()
    {
        if (IsShown) HidePanel();
        else if (Environment.TickCount64 - _deactivatedAt > 350) ShowPanel();
    }

    private void PositionPanel()
    {
        GetCursorPos(out var cursor);
        var display = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        var currentDisplay = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (currentDisplay.DisplayId != display.DisplayId) AppWindow.Move(new PointInt32(display.WorkArea.X + 12, display.WorkArea.Y + 12));
        double scale = GetDpiForWindow(_hwnd) / 96d;
        int width = (int)(360 * scale), height = (int)((_height + (ErrorBar.IsOpen ? 100 : 0)) * scale), gap = (int)(12 * scale);
        var work = display.WorkArea;
        height = Math.Min(height, work.Height - gap * 2);
        // Follow the screen that contains the tray click, including negative virtual-screen coordinates.
        bool topTaskbar = work.Y > display.OuterBounds.Y && cursor.Y <= work.Y;
        bool leftTaskbar = work.X > display.OuterBounds.X && cursor.X <= work.X;
        int x = leftTaskbar ? work.X + gap : work.X + work.Width - width - gap;
        int y = topTaskbar ? work.Y + gap : work.Y + work.Height - height - gap;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    internal void ShowError(string message) { ErrorBar.Message = message; ErrorBar.IsOpen = true; if (IsShown) PositionPanel(); }
    internal void SetRefreshing(bool value) { RefreshButton.IsEnabled = !value; if (value) Status.Text = T("GENERIC_DETECTING_DISPLAYS", "Detecting displays…"); }
    internal void CloseForExit() { _closing = true; _debounce.Stop(); Close(); }
    private void Link_Click(object sender, RoutedEventArgs e) { _controller.Settings.LinkedBrightness = LinkButton.IsChecked == true; _controller.SaveSettings(); }
    private async void Power_Click(object sender, RoutedEventArgs e) => await _controller.PowerOffAsync("all");
    private void Settings_Click(object sender, RoutedEventArgs e) => _controller.OpenSettings();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _controller.RefreshAsync();
    private void Root_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Escape) { HidePanel(); e.Handled = true; } }
    private static string T(string key, string fallback) => LocalizationService.Get(key, fallback);
    private static string FeatureValueName(byte code, uint value) => code switch
    {
        0x60 => value switch { 1 => "VGA", 3 => "DVI", 15 => "DisplayPort 1", 16 => "DisplayPort 2", 17 => "HDMI 1", 18 => "HDMI 2", 27 => "USB-C", _ => $"Input {value}" },
        0xD6 => value switch { 1 => "On", 2 => "Standby", 3 => "Suspend", 4 => "Off", 5 => "Power off", _ => value.ToString() },
        _ => value.ToString()
    };

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
