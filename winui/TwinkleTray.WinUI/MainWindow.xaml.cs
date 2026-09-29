using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
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
using Windows.UI.ViewManagement;

namespace TwinkleTray.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly AppController _controller;
    private readonly Dictionary<string, Slider> _sliders = new();
    private readonly Dictionary<string, TextBlock> _levels = new();
    private readonly Dictionary<string, Slider> _contrastSliders = new();
    private readonly Dictionary<string, Slider> _featureSliders = new();
    private readonly Dictionary<string, ComboBox> _featureChoices = new();
    private readonly Dictionary<string, Control> _focusTargets = new();
    private readonly Dictionary<uint, Slider> _activePointers = new();
    private readonly Dictionary<string, (string Id, double Value, bool Contrast, bool Linked)> _pending = new();
    private readonly Dictionary<string, (string Id, byte Code, double Value)> _pendingFeatures = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly AccessibilitySettings _accessibility = new();
    private bool _updating, _closing, _flushing, _renderDeferred, _layoutQueued, _positioning, _accessibilitySubscribed;
    private string _layoutSignature = "";
    private Point _anchorCursor;
    private long _deactivatedAt;
    private readonly nint _hwnd;
    public bool IsShown { get; private set; }
    internal int RenderGeneration { get; private set; }
    internal bool IsContentScrollable { get; private set; }
    internal (double DesiredHeightDip, int WidthPx, int HeightPx, int WorkAreaWidthPx, int WorkAreaHeightPx, double Scale) LayoutMetrics { get; private set; }

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
        Root.Loaded += (_, _) => QueueLayout();
        Root.SizeChanged += (_, _) => QueueLayout();
        PanelBody.SizeChanged += (_, _) => QueueLayout();
        Root.ActualThemeChanged += (_, _) => ApplyBackdrop();
        ErrorBar.Closed += (_, _) => QueueLayout();
        try { _accessibility.HighContrastChanged += AccessibilityChanged; _accessibilitySubscribed = true; }
        catch (COMException) { /* Some unpackaged environments cannot register accessibility notifications. */ }
        Closed += (_, _) =>
        {
            if (!_accessibilitySubscribed) return;
            try { _accessibility.HighContrastChanged -= AccessibilityChanged; }
            catch (COMException) { /* The notification source may already have shut down. */ }
            finally { _accessibilitySubscribed = false; }
        };
        Activated += (_, args) => { if (args.WindowActivationState == WindowActivationState.Deactivated && !_controller.IsDemo) { _deactivatedAt = Environment.TickCount64; HidePanel(); } };
        AppWindow.Closing += (_, args) => { if (!_closing) { args.Cancel = true; HidePanel(); } };
        _debounce.Tick += async (_, _) => await FlushAsync();
        ApplySettings();
    }

    internal void ApplySettings()
    {
        try { Root.Language = CultureInfo.GetCultureInfo(LocalizationService.CurrentLanguage).Name; }
        catch (CultureNotFoundException) { Root.Language = "en"; }
        Root.RequestedTheme = _controller.Settings.Theme switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };
        _debounce.Interval = TimeSpan.FromMilliseconds(_controller.Settings.UpdateIntervalMilliseconds);
        ApplyBackdrop();
        int corner = _controller.Settings.WindowsStyle == "win10" ? 1 : 2;
        DwmSetWindowAttribute(_hwnd, 33, ref corner, sizeof(int));
        Heading.Text = T("PANEL_TITLE", "Adjust Brightness");
        ToolTipService.SetToolTip(Heading, Heading.Text);
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
        var monitors = _controller.VisibleMonitors.ToList();
        string signature = LayoutSignature(monitors);
        if (_layoutSignature == signature)
        {
            UpdateVisibleValues(monitors);
            UpdateToolbar(monitors);
            QueueLayout();
            return;
        }
        if (_activePointers.Count > 0 || _flushing)
        {
            _renderDeferred = true;
            UpdateVisibleValues(monitors);
            return;
        }
        var focused = Root.XamlRoot is null ? null : FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        string? focusId = null;
        FocusState focusState = FocusState.Keyboard;
        while (focused is not null)
        {
            if (focused is Control control && !string.IsNullOrEmpty(AutomationProperties.GetAutomationId(control)))
            { focusId = AutomationProperties.GetAutomationId(control); focusState = control.FocusState; break; }
            focused = VisualTreeHelper.GetParent(focused);
        }
        double scrollOffset = BodyScroll.VerticalOffset;
        _updating = true;
        try
        {
            MonitorList.Children.Clear(); _sliders.Clear(); _levels.Clear(); _contrastSliders.Clear(); _featureSliders.Clear(); _featureChoices.Clear(); _focusTargets.Clear();
            foreach (var monitor in monitors)
            {
                var preferences = _controller.Preferences(monitor.Id);
                var card = new StackPanel { Spacing = 4 };
                var title = new Grid { ColumnSpacing = 8, MinHeight = 24 };
                title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                title.Children.Add(new FontIcon { Glyph = string.IsNullOrWhiteSpace(preferences.IconGlyph) ? (monitor.Connection.Contains("WMI", StringComparison.OrdinalIgnoreCase) ? "\uE770" : "\uE7F4") : preferences.IconGlyph, FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
                var label = new TextBlock { Text = preferences.ShowName ? _controller.DisplayName(monitor) : "", FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(label, _controller.DisplayName(monitor));
                Grid.SetColumn(label, 1); title.Children.Add(label);
                var level = new TextBlock { Text = $"{_controller.LogicalBrightness(monitor):0}", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, MinWidth = 32, TextAlignment = TextAlignment.Right };
                Grid.SetColumn(level, 2); title.Children.Add(level); _levels[monitor.Id] = level;
                level.Visibility = preferences.ShowValue ? Visibility.Visible : Visibility.Collapsed;
                card.Children.Add(title);
                var slider = MakeSlider(monitor.Id, _controller.LogicalBrightness(monitor), false, _controller.DisplayName(monitor));
                slider.IsEnabled = _controller.CanControl(monitor);
                card.Children.Add(slider); _sliders[monitor.Id] = slider;
                if (!_controller.CanControl(monitor))
                {
                    level.Text = "—";
                    card.Children.Add(SecondaryText(T("GENERIC_NOT_SUPPORTED", "Not supported") + " · " + monitor.Connection));
                }
                bool configuredContrast = preferences.Features.TryGetValue(0x12, out var contrastFeature) && contrastFeature.Enabled;
                if (preferences.ShowContrast && monitor.SupportsContrast && !configuredContrast)
                {
                    card.Children.Add(SecondaryText(T("PANEL_LABEL_CONTRAST", "Contrast"), new Thickness(0, 4, 0, 0)));
                    var contrast = MakeSlider(monitor.Id, monitor.Contrast ?? 50, true, T("PANEL_LABEL_CONTRAST", "Contrast") + " " + _controller.DisplayName(monitor));
                    _contrastSliders[monitor.Id] = contrast;
                    card.Children.Add(contrast);
                }
                foreach (var pair in preferences.Features.Where(f => f.Value.Enabled && !f.Value.LinkedToBrightness))
                {
                    var feature = _controller.Features(monitor.Id).FirstOrDefault(f => f.Code == pair.Key);
                    if (feature is null) continue;
                    string name = string.IsNullOrWhiteSpace(pair.Value.Name) ? feature.Name : pair.Value.Name;
                    var featureHeading = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
                    featureHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    featureHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    if (pair.Value.IconType == "text" && pair.Value.IconText.Length > 0) featureHeading.Children.Add(new TextBlock { Text = pair.Value.IconText, FontSize = 13, MaxWidth = 48, TextTrimming = TextTrimming.CharacterEllipsis });
                    else if (pair.Value.IconType == "image" && Path.IsPathFullyQualified(pair.Value.IconPath) && File.Exists(pair.Value.IconPath))
                    {
                        try { featureHeading.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(pair.Value.IconPath)), Width = 16, Height = 16 }); } catch (Exception exception) when (exception is ArgumentException or System.Runtime.InteropServices.COMException) { Program.Log(exception); }
                    }
                    else if (pair.Value.IconGlyph.Length > 0) featureHeading.Children.Add(new FontIcon { Glyph = pair.Value.IconGlyph, FontSize = 16 });
                    var featureLabel = SecondaryText(name);
                    featureLabel.TextWrapping = TextWrapping.NoWrap; featureLabel.TextTrimming = TextTrimming.CharacterEllipsis;
                    ToolTipService.SetToolTip(featureLabel, name);
                    Grid.SetColumn(featureLabel, 1); featureHeading.Children.Add(featureLabel);
                    card.Children.Add(featureHeading);
                    if (feature.AllowedValues.Count > 0)
                    {
                        var choice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 14 };
                        foreach (uint item in feature.AllowedValues) choice.Items.Add(new ComboBoxItem { Content = FeatureValueName(feature.Code, item), Tag = item });
                        choice.SelectedItem = choice.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (uint)i.Tag == feature.Current);
                        AutomationProperties.SetName(choice, name + " " + _controller.DisplayName(monitor));
                        RegisterFocusTarget(choice, "feature:" + monitor.Id + ":" + feature.Code);
                        _featureChoices[monitor.Id + ":" + feature.Code] = choice;
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
                        var control = new Slider { Minimum = pair.Value.Min, Maximum = Math.Max(pair.Value.Min + 1, Math.Min(pair.Value.Max, feature.Maximum)), Value = feature.Current, StepFrequency = 1, MinHeight = 32 };
                        AutomationProperties.SetName(control, name + " " + _controller.DisplayName(monitor));
                        RegisterFocusTarget(control, "feature:" + monitor.Id + ":" + feature.Code);
                        TrackSliderGesture(control);
                        _featureSliders[monitor.Id + ":" + feature.Code] = control;
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
                MonitorList.Children.Add(new FontIcon { Glyph = "\uE7F4", FontSize = 20, Margin = new Thickness(0, 16, 0, 0) });
                MonitorList.Children.Add(SecondaryText(T("GENERIC_NO_COMPATIBLE_DISPLAYS", "No compatible displays found. Check that DDC/CI is enabled in your monitor settings."), new Thickness(8, 0, 8, 16)));
            }
            UpdateToolbar(monitors);
            _layoutSignature = signature; _renderDeferred = false; RenderGeneration++;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closing) return;
                BodyScroll.ChangeView(null, scrollOffset, null, true);
                if (focusId is not null && _focusTargets.TryGetValue(focusId, out var target)) target.Focus(focusState == FocusState.Unfocused ? FocusState.Keyboard : focusState);
                QueueLayout();
            });
        }
        finally { _updating = false; }
    }

    private Slider MakeSlider(string id, double value, bool contrast, string name)
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Value = value, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(slider, name + " " + T(contrast ? "PANEL_LABEL_CONTRAST" : "PANEL_LABEL_BRIGHTNESS", contrast ? "Contrast" : "Brightness"));
        RegisterFocusTarget(slider, (contrast ? "contrast:" : "brightness:") + id);
        TrackSliderGesture(slider);
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

    private string LayoutSignature(IReadOnlyList<MonitorSnapshot> monitors) => JsonSerializer.Serialize(new
    {
        ContrastLabel = T("PANEL_LABEL_CONTRAST", "Contrast"), UnsupportedLabel = T("GENERIC_NOT_SUPPORTED", "Not supported"),
        EmptyLabel = T("GENERIC_NO_COMPATIBLE_DISPLAYS", "No compatible displays found. Check that DDC/CI is enabled in your monitor settings."),
        Monitors = monitors.Select(monitor =>
        {
            var preferences = _controller.Preferences(monitor.Id);
            return new
            {
                monitor.Id, Name = _controller.DisplayName(monitor), monitor.Connection, monitor.SupportsContrast,
                CanControl = _controller.CanControl(monitor), preferences.ShowName, preferences.ShowValue, preferences.ShowContrast, preferences.IconGlyph,
                Features = preferences.Features.Where(pair => pair.Value.Enabled && !pair.Value.LinkedToBrightness).Select(pair => new
                {
                    pair.Key, pair.Value.Name, pair.Value.Min, pair.Value.Max, pair.Value.IconGlyph, pair.Value.IconType, pair.Value.IconPath, pair.Value.IconText,
                    Hardware = _controller.Features(monitor.Id).Where(feature => feature.Code == pair.Key).Select(feature => new { feature.Name, feature.Maximum, feature.AllowedValues }),
                }),
                ContrastIsFeature = preferences.Features.TryGetValue(0x12, out var contrast) && contrast.Enabled,
            };
        }),
    });

    private void UpdateVisibleValues(IReadOnlyList<MonitorSnapshot> monitors)
    {
        if (_flushing) return;
        _updating = true;
        try
        {
            foreach (var monitor in monitors)
            {
                if (_sliders.TryGetValue(monitor.Id, out var slider) && !_activePointers.ContainsValue(slider) &&
                    !_pending.ContainsKey("all:brightness") && !_pending.ContainsKey(monitor.Id + ":brightness"))
                {
                    slider.Value = _controller.LogicalBrightness(monitor);
                    if (_levels.TryGetValue(monitor.Id, out var level)) level.Text = _controller.CanControl(monitor) ? $"{slider.Value:0}" : "—";
                }
                if (_contrastSliders.TryGetValue(monitor.Id, out var contrast) && !_activePointers.ContainsValue(contrast) && !_pending.ContainsKey(monitor.Id + ":contrast"))
                    contrast.Value = monitor.Contrast ?? 50;
                foreach (var feature in _controller.Features(monitor.Id))
                {
                    string key = monitor.Id + ":" + feature.Code;
                    if (_pendingFeatures.ContainsKey(key)) continue;
                    if (_featureSliders.TryGetValue(key, out var featureSlider) && !_activePointers.ContainsValue(featureSlider)) featureSlider.Value = feature.Current;
                    if (_featureChoices.TryGetValue(key, out var choice) && !choice.IsDropDownOpen)
                        choice.SelectedItem = choice.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (uint)item.Tag == feature.Current);
                }
            }
        }
        finally { _updating = false; }
    }

    private void UpdateToolbar(IReadOnlyList<MonitorSnapshot> monitors)
    {
        LinkButton.IsEnabled = monitors.Count(_controller.CanControl) > 1;
        PowerButton.IsEnabled = _controller.Settings.PowerOffMode is "windows" or "both" || (_controller.Settings.PowerOffMode == "ddc" && monitors.Any(monitor => monitor.Connection.Contains("DDC", StringComparison.OrdinalIgnoreCase)));
        Status.Text = _controller.IsDemo ? "DEMO · " + T("GENERIC_ALL_DISPLAYS", "All displays") : "Twinkle Tray · WinUI 3";
        ToolTipService.SetToolTip(Status, Status.Text);
    }

    private static TextBlock SecondaryText(string text, Thickness margin = default) => new()
    {
        Text = text, Style = (Style)Application.Current.Resources["SecondaryTextStyle"], Margin = margin,
    };

    private void RegisterFocusTarget(Control control, string id)
    {
        AutomationProperties.SetAutomationId(control, id);
        _focusTargets[id] = control;
    }

    private void TrackSliderGesture(Slider slider)
    {
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) => _activePointers[args.Pointer.PointerId] = slider), true);
        PointerEventHandler ended = (_, args) =>
        {
            _activePointers.Remove(args.Pointer.PointerId);
            if (_activePointers.Count == 0 && _renderDeferred) DispatcherQueue.TryEnqueue(RenderMonitors);
        };
        slider.AddHandler(UIElement.PointerReleasedEvent, ended, true);
        slider.AddHandler(UIElement.PointerCanceledEvent, ended, true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, ended, true);
    }

    private void AccessibilityChanged(AccessibilitySettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (!_closing) { ApplyBackdrop(); QueueLayout(); } });

    private void ApplyBackdrop()
    {
        bool acrylic = !_accessibility.HighContrast && _controller.Settings.UseAcrylic && Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported();
        SystemBackdrop = acrylic ? SystemBackdrop ?? new DesktopAcrylicBackdrop() : null;
        SolidBackground.Visibility = acrylic ? Visibility.Collapsed : Visibility.Visible;
    }

    private void QueueLayout()
    {
        if (_closing || _layoutQueued || _positioning) return;
        _layoutQueued = true;
        DispatcherQueue.TryEnqueue(() => { _layoutQueued = false; if (IsShown && !_closing) PositionPanel(); });
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
        finally { _flushing = false; if (!_closing) RenderMonitors(); }
    }

    public void ShowPanel()
    {
        IsShown = true; PositionPanel(useCursor: true); AppWindow.Show(); Activate(); SetForegroundWindow(_hwnd); QueueLayout();
    }
    public void HidePanel() { IsShown = false; AppWindow.Hide(); }
    public void TogglePanel()
    {
        if (IsShown) HidePanel();
        else if (Environment.TickCount64 - _deactivatedAt > 350) ShowPanel();
    }

    internal void RecalculateLayoutForVerification() => PositionPanel();

    private void PositionPanel(bool useCursor = false)
    {
        if (_positioning || _closing) return;
        _positioning = true;
        try
        {
        if (useCursor) GetCursorPos(out _anchorCursor);
        var currentDisplay = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var display = useCursor ? DisplayArea.GetFromPoint(new PointInt32(_anchorCursor.X, _anchorCursor.Y), DisplayAreaFallback.Nearest) : currentDisplay;
        if (currentDisplay.DisplayId != display.DisplayId) AppWindow.Move(new PointInt32(display.WorkArea.X + 12, display.WorkArea.Y + 12));
        double scale = GetDpiForWindow(_hwnd) / 96d;
        if (scale < .5) scale = Root.XamlRoot?.RasterizationScale ?? 1;
        var work = display.WorkArea;
        int gap = Math.Max(1, (int)Math.Round(12 * scale));
        int width = Math.Min((int)Math.Round(360 * scale), Math.Max(1, work.Width - gap * 2));
        var outerSize = AppWindow.Size;
        var clientSize = AppWindow.ClientSize;
        int nonClientWidth = Math.Max(0, outerSize.Width - clientSize.Width);
        int nonClientHeight = Math.Max(0, outerSize.Height - clientSize.Height);
        // Borderless windows may retain a native inset; XAML measures only the client area.
        double clientWidth = Math.Max(1, width - nonClientWidth) / scale;
        double innerWidth = Math.Max(1, clientWidth - PanelLayout.Padding.Left - PanelLayout.Padding.Right);
        var available = new Windows.Foundation.Size(innerWidth, double.PositiveInfinity);
        PanelHeader.Measure(available); PanelFooter.Measure(available); PanelBody.Measure(available);
        double chromeHeight = PanelLayout.Padding.Top + PanelLayout.Padding.Bottom + PanelHeader.DesiredSize.Height + PanelFooter.DesiredSize.Height + PanelLayout.RowSpacing * 2;
        double desiredHeight = chromeHeight + PanelBody.DesiredSize.Height;
        int height = Math.Min((int)Math.Ceiling(desiredHeight * scale) + nonClientHeight, Math.Max(1, work.Height - gap * 2));
        double clientHeight = Math.Max(0, height - nonClientHeight) / scale;
        IsContentScrollable = PanelBody.DesiredSize.Height > Math.Max(0, clientHeight - chromeHeight) + .5;
        LayoutMetrics = (desiredHeight, width, height, work.Width, work.Height, scale);
        // Follow the screen that contains the tray click, including negative virtual-screen coordinates.
        bool topTaskbar = work.Y > display.OuterBounds.Y && _anchorCursor.Y <= work.Y;
        bool leftTaskbar = work.X > display.OuterBounds.X && _anchorCursor.X <= work.X;
        int x = leftTaskbar ? work.X + gap : work.X + work.Width - width - gap;
        int y = topTaskbar ? work.Y + gap : work.Y + work.Height - height - gap;
        x = Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - width));
        y = Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - height));
        if (AppWindow.Position.X != x || AppWindow.Position.Y != y || AppWindow.Size.Width != width || AppWindow.Size.Height != height)
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        finally { _positioning = false; }
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
