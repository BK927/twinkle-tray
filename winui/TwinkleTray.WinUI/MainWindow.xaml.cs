using System.Globalization;
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
using Windows.UI.ViewManagement;

namespace TwinkleTray.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly AppController _controller;
    private readonly Dictionary<string, Slider> _sliders = new();
    private readonly Dictionary<Slider, TextBox> _numberEditors = new();
    private readonly Dictionary<Control, bool> _controlAvailability = new();
    private readonly Dictionary<string, Slider> _contrastSliders = new();
    private readonly Dictionary<string, Slider> _featureSliders = new();
    private readonly Dictionary<string, ComboBox> _featureChoices = new();
    private readonly Dictionary<string, Control> _focusTargets = new();
    private readonly Dictionary<uint, Slider> _activePointers = new();
    private readonly Dictionary<string, (string Id, double Value, bool Contrast, bool Linked)> _pending = new();
    private readonly Dictionary<string, (string Id, byte Code, double Value)> _pendingFeatures = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly AccessibilitySettings _accessibility = new();
    private bool _updating, _closing, _flushing, _renderDeferred, _layoutQueued, _positioning, _accessibilitySubscribed, _refreshing;
    private LayoutSnapshot? _layoutSnapshot;
    private (string Id, string Text, int Start, int Length)? _refreshDraft;
    private TextBlock? _emptyMessage;
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
        // This is a notification-style transient surface, not a small document window.
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        bool capturePreview = controller.IsDemo && !controller.IsSmokeTest && Environment.GetEnvironmentVariable("TWINKLETRAY_DEMO_CAPTURE") == "1";
        AppWindow.IsShownInSwitchers = capturePreview;
        // WS_EX_TOOLWINDOW excludes this transient flyout from Alt+Tab and the taskbar.
        if (!capturePreview) SetWindowLongPtr(_hwnd, -20, (nint)(GetWindowLongPtr(_hwnd, -20).ToInt64() | 0x80));
        Root.Loaded += (_, _) => QueueLayout();
        Root.SizeChanged += (_, _) => QueueLayout();
        PanelBody.SizeChanged += (_, _) => QueueLayout();
        Root.ActualThemeChanged += (_, _) => { ApplyBackdrop(); ApplyNativeFrameTheme(); };
        ErrorBar.Closed += (_, _) => QueueLayout();
        try { _accessibility.HighContrastChanged += AccessibilityChanged; _accessibilitySubscribed = true; }
        catch (COMException) { /* Some unpackaged environments cannot register accessibility notifications. */ }
        Closed += (_, _) =>
        {
            _closing = true;
            if (!_accessibilitySubscribed) return;
            try { _accessibility.HighContrastChanged -= AccessibilityChanged; }
            catch (COMException) { /* The notification source may already have shut down. */ }
            finally { _accessibilitySubscribed = false; }
        };
        Activated += Panel_Activated;
        AppWindow.Closing += (_, args) => { if (!_closing) { args.Cancel = true; HidePanel(); } };
        _debounce.Tick += async (_, _) => await FlushAsync();
        InitializeSystemAppearance();
        ApplySettings();
    }

    internal void ApplySettings()
    {
        try { Root.Language = CultureInfo.GetCultureInfo(LocalizationService.CurrentLanguage).Name; }
        catch (CultureNotFoundException) { Root.Language = "en"; }
        ApplySystemAppearance();
        _debounce.Interval = TimeSpan.FromMilliseconds(_controller.Settings.UpdateIntervalMilliseconds);
        int corner = _controller.Settings.WindowsStyle == "win10" ? 1 : 2;
        DwmSetWindowAttribute(_hwnd, 33, ref corner, sizeof(int));
        PanelOutline.CornerRadius = new CornerRadius(corner == 1 ? 0 : 8);
        Title = LocalizationService.ProductName;
        bool win10 = _controller.Settings.WindowsStyle == "win10";
        Grid.SetRow(ToolbarSurface, win10 ? 0 : 2);
        Heading.Text = T("PANEL_TITLE", "Adjust Brightness");
        ToolTipService.SetToolTip(Heading, LocalizationService.ProductName + "\n" + T("GENERIC_REFRESH_DISPLAYS", "Refresh displays") + " (F5)");
        ToolTipService.SetToolTip(LinkButton, T("PANEL_BUTTON_LINK_LEVELS", "Link levels"));
        AutomationProperties.SetName(LinkButton, T("PANEL_BUTTON_LINK_LEVELS", "Link levels"));
        PowerMenuItem.Text = T("PANEL_BUTTON_TURN_OFF_DISPLAYS", "Turn off displays");
        ToolTipService.SetToolTip(MoreButton, T("NATIVE_MORE", "More options"));
        AutomationProperties.SetName(MoreButton, T("NATIVE_MORE", "More options"));
        ToolTipService.SetToolTip(SettingsButton, T("GENERIC_SETTINGS", "Settings"));
        AutomationProperties.SetName(SettingsButton, T("GENERIC_SETTINGS", "Settings"));
        RefreshMenuItem.Text = T("GENERIC_REFRESH_DISPLAYS", "Refresh displays") + " (F5)";
        RefreshMoreMenuItem.Text = RefreshMenuItem.Text;
        AutomationProperties.SetName(RefreshProgress, T("GENERIC_DETECTING_DISPLAYS", "Detecting displays…"));
        LinkButton.IsChecked = _controller.Settings.LinkedBrightness;
        RenderMonitors();
        // Settings can move the toolbar without changing any monitor controls.
        QueueLayout();
    }

    internal void RenderMonitors()
    {
        var monitors = _controller.VisibleMonitors.ToList();
        if (LayoutMatches(monitors))
        {
            UpdateVisibleValues(monitors);
            UpdateToolbar(monitors);
            // Value editors have fixed widths. Actual content-size changes are
            // observed by SizeChanged; brightness updates need no native measure/move.
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
        (string Text, int Start, int Length)? editorDraft = null;
        FocusState focusState = FocusState.Keyboard;
        while (focused is not null)
        {
            if (focused is Control control && !string.IsNullOrEmpty(AutomationProperties.GetAutomationId(control)))
            {
                focusId = AutomationProperties.GetAutomationId(control); focusState = control.FocusState;
                if (control is TextBox editor && _numberEditors.ContainsValue(editor)) editorDraft = (editor.Text, editor.SelectionStart, editor.SelectionLength);
                break;
            }
            focused = VisualTreeHelper.GetParent(focused);
        }
        double scrollOffset = BodyScroll.VerticalOffset;
        _updating = true;
        try
        {
            MonitorList.Children.Clear(); _sliders.Clear(); _numberEditors.Clear(); _controlAvailability.Clear(); _contrastSliders.Clear(); _featureSliders.Clear(); _featureChoices.Clear(); _focusTargets.Clear(); _emptyMessage = null;
            bool linked = _controller.Settings.LinkedBrightness;
            var displayed = linked ? monitors.Where(_controller.CanControl).TakeLast(1).ToList() : monitors;
            bool expanded = !linked && monitors.Any(HasExtraControls);
            foreach (var monitor in displayed)
            {
                var preferences = _controller.Preferences(monitor.Id);
                string id = linked ? "all" : monitor.Id;
                string name = linked ? T("GENERIC_ALL_DISPLAYS", "All displays") : _controller.DisplayName(monitor);
                var card = new StackPanel { Spacing = 8 };
                AutomationProperties.SetAutomationId(card, "monitor:" + id);
                var title = new Grid { ColumnSpacing = 8, MinHeight = 24 };
                title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                title.Children.Add(new FontIcon { Glyph = string.IsNullOrWhiteSpace(preferences.IconGlyph) ? (monitor.Connection.Contains("WMI", StringComparison.OrdinalIgnoreCase) ? "\uE770" : "\uE7F4") : preferences.IconGlyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
                var label = new TextBlock { Text = linked || preferences.ShowName ? name : "", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(label, name);
                Grid.SetColumn(label, 1); title.Children.Add(label);
                card.Children.Add(title);
                var slider = MakeSlider(id, _controller.LogicalBrightness(monitor), false, name, _controller.CanControl(monitor));
                card.Children.Add(MakeControlRow(slider, T("PANEL_LABEL_BRIGHTNESS", "Brightness"), expanded ? DefaultFeatureIcon(0x10) : null, linked || preferences.ShowValue, expanded));
                _sliders[id] = slider;
                if (!_controller.CanControl(monitor))
                {
                    card.Children.Add(SecondaryText(T("GENERIC_NOT_SUPPORTED", "Not supported") + " · " + monitor.Connection));
                }
                if (linked) { MonitorList.Children.Add(card); continue; }
                bool configuredContrast = preferences.Features.TryGetValue(0x12, out var contrastFeature) && contrastFeature.Enabled;
                if (preferences.ShowContrast && monitor.SupportsContrast && !configuredContrast)
                {
                    string contrastName = T("PANEL_LABEL_CONTRAST", "Contrast");
                    var contrast = MakeSlider(monitor.Id, monitor.Contrast ?? 50, true, name);
                    _contrastSliders[monitor.Id] = contrast;
                    card.Children.Add(MakeControlRow(contrast, contrastName, DefaultFeatureIcon(0x12), true, true));
                }
                foreach (var pair in preferences.Features.Where(f => f.Value.Enabled && !f.Value.LinkedToBrightness))
                {
                    var feature = _controller.Features(monitor.Id).FirstOrDefault(f => f.Code == pair.Key);
                    if (feature is null) continue;
                    string featureName = string.IsNullOrWhiteSpace(pair.Value.Name) ? feature.Name : pair.Value.Name;
                    var icon = FeatureIcon(pair.Value, feature.Code);
                    if (feature.AllowedValues.Count > 0)
                    {
                        var choice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 13, MinWidth = 0 };
                        foreach (uint item in feature.AllowedValues) choice.Items.Add(new ComboBoxItem { Content = FeatureValueName(feature.Code, item), Tag = item });
                        choice.SelectedItem = choice.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (uint)i.Tag == feature.Current);
                        AutomationProperties.SetName(choice, featureName + " " + name);
                        RegisterFocusTarget(choice, "feature:" + monitor.Id + ":" + feature.Code);
                        _featureChoices[monitor.Id + ":" + feature.Code] = choice;
                        choice.SelectionChanged += async (_, _) =>
                        {
                            if (_updating || _refreshing || !choice.IsEnabled || choice.SelectedItem is not ComboBoxItem selected) return;
                            try { await _controller.SetFeatureAsync(monitor.Id, feature.Code, (uint)selected.Tag); }
                            catch (Exception exception) { ShowError(exception.Message); }
                        };
                        card.Children.Add(MakeFeatureRow(choice, icon, featureName));
                    }
                    else
                    {
                        var control = new Slider { Minimum = pair.Value.Min, Maximum = Math.Max(pair.Value.Min + 1, Math.Min(pair.Value.Max, feature.Maximum)), Value = feature.Current, StepFrequency = 1, MinHeight = 32 };
                        AutomationProperties.SetName(control, featureName + " " + name);
                        RegisterFocusTarget(control, "feature:" + monitor.Id + ":" + feature.Code);
                        TrackSliderGesture(control);
                        _featureSliders[monitor.Id + ":" + feature.Code] = control;
                        control.ValueChanged += (_, args) =>
                        {
                            if (_updating) return;
                            _pendingFeatures[monitor.Id + ":" + feature.Code] = (monitor.Id, feature.Code, args.NewValue);
                            ScheduleWrite();
                        };
                        card.Children.Add(MakeControlRow(control, featureName, icon, true, true));
                    }
                }
                MonitorList.Children.Add(card);
            }
            if (displayed.Count == 0)
            {
                MonitorList.Children.Add(new FontIcon { Glyph = "\uE7F4", FontSize = 20, Margin = new Thickness(0, 16, 0, 0) });
                _emptyMessage = SecondaryText(T("GENERIC_NO_COMPATIBLE_DISPLAYS", "No compatible displays found. Check that DDC/CI is enabled in your monitor settings."), new Thickness(8, 0, 8, 16));
                MonitorList.Children.Add(_emptyMessage);
            }
            ApplyRefreshingState(monitors);
            _layoutSnapshot = CaptureLayout(monitors); _renderDeferred = false; RenderGeneration++;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closing) return;
                BodyScroll.ChangeView(null, scrollOffset, null, true);
                if (focusId is not null && _focusTargets.TryGetValue(focusId, out var target))
                {
                    if (target is TextBox editor && editorDraft is { } draft)
                    { editor.Text = draft.Text; editor.Select(Math.Min(draft.Start, editor.Text.Length), Math.Min(draft.Length, Math.Max(0, editor.Text.Length - draft.Start))); }
                    target.Focus(focusState == FocusState.Unfocused ? FocusState.Keyboard : focusState);
                }
                QueueLayout();
            });
        }
        finally { _updating = false; }
    }

    private Slider MakeSlider(string id, double value, bool contrast, string name, bool enabled = true)
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Value = value, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(slider, name + " " + T(contrast ? "PANEL_LABEL_CONTRAST" : "PANEL_LABEL_BRIGHTNESS", contrast ? "Contrast" : "Brightness"));
        RegisterFocusTarget(slider, (contrast ? "contrast:" : "brightness:") + id, enabled);
        TrackSliderGesture(slider);
        slider.ValueChanged += (_, args) =>
        {
            if (_updating) return;
            bool linked = !contrast && _controller.Settings.LinkedBrightness;
            _pending[linked ? "all:brightness" : id + (contrast ? ":contrast" : ":brightness")] = (id, args.NewValue, contrast, linked);
            ScheduleWrite();
        };
        return slider;
    }

    private bool HasExtraControls(MonitorSnapshot monitor)
    {
        var preferences = _controller.Preferences(monitor.Id);
        return preferences.ShowContrast && monitor.SupportsContrast || preferences.Features.Any(pair =>
            pair.Value.Enabled && !pair.Value.LinkedToBrightness && _controller.Features(monitor.Id).Any(feature => feature.Code == pair.Key));
    }

    private static FontIcon DefaultFeatureIcon(byte code) => new()
    {
        Glyph = code switch { 0x10 or 0x13 => "\uE706", 0x12 => "\uE793", 0x62 => "\uE767", 0x60 => "\uE839", 0xD6 => "\uE7E8", _ => "\uE897" },
        FontSize = 20, VerticalAlignment = VerticalAlignment.Center,
    };

    private static FrameworkElement FeatureIcon(FeatureSettings settings, byte code)
    {
        if (settings.IconType == "text" && settings.IconText.Length > 0)
            return new TextBlock { Text = settings.IconText, FontSize = 12, MaxWidth = 24, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (settings.IconType == "image" && Path.IsPathFullyQualified(settings.IconPath) && File.Exists(settings.IconPath))
        {
            try { return new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(settings.IconPath)), Width = 20, Height = 20 }; }
            catch (Exception exception) when (exception is ArgumentException or COMException) { Program.Log(exception); }
        }
        // E897 is the persisted default (Help), not a feature-specific choice.
        // Keep settings unchanged and preserve every non-default custom glyph.
        return !string.IsNullOrEmpty(settings.IconGlyph) && settings.IconGlyph != "\uE897"
            ? new FontIcon { Glyph = settings.IconGlyph, FontSize = 20, VerticalAlignment = VerticalAlignment.Center }
            : DefaultFeatureIcon(code);
    }

    private static Grid MakeFeatureRow(FrameworkElement control, FrameworkElement icon, string name)
    {
        var row = new Grid { ColumnSpacing = 8, MinHeight = 32 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        row.Children.Add(icon); Grid.SetColumn(control, 1); row.Children.Add(control);
        ToolTipService.SetToolTip(icon, name); ToolTipService.SetToolTip(control, name);
        AutomationProperties.SetName(icon, name);
        return row;
    }

    private Grid MakeControlRow(Slider slider, string name, FrameworkElement? icon, bool showValue, bool compact)
    {
        var row = new Grid { ColumnSpacing = 8, MinHeight = 32 };
        if (icon is not null)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            ToolTipService.SetToolTip(icon, name); AutomationProperties.SetName(icon, name);
            row.Children.Add(icon);
        }
        int sliderColumn = icon is null ? 0 : 1;
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(slider, sliderColumn); row.Children.Add(slider);
        var editor = new TextBox
        {
            Style = (Style)Application.Current.Resources["TrayValueTextBoxStyle"],
            Text = FormatValue(slider.Value), Width = slider.Maximum > 999 ? 56 : 44,
            Visibility = showValue ? Visibility.Visible : Visibility.Collapsed,
        };
        AutomationProperties.SetName(editor, AutomationProperties.GetName(slider));
        ToolTipService.SetToolTip(editor, name + $" ({FormatValue(slider.Minimum)}–{FormatValue(slider.Maximum)})");
        RegisterFocusTarget(editor, AutomationProperties.GetAutomationId(slider) + ":value", _controlAvailability.GetValueOrDefault(slider, true));
        _numberEditors[slider] = editor;
        editor.LostFocus += (_, _) => CommitNumericEditor(slider, editor);
        editor.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter) return;
            CommitNumericEditor(slider, editor); args.Handled = true;
        };
        slider.ValueChanged += (_, _) => { if (!HasFocusWithin(editor)) editor.Text = FormatValue(slider.Value); };
        Grid.SetColumn(editor, sliderColumn + 1); row.Children.Add(editor);
        AttachWheel(slider);
        return row;
    }

    private bool CommitNumericEditor(Slider slider, TextBox editor)
    {
        if (_updating || _refreshing || !slider.IsEnabled) return false;
        bool valid = double.TryParse(editor.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) && double.IsFinite(value);
        if (valid) slider.Value = Math.Clamp(Math.Round(value), slider.Minimum, slider.Maximum);
        editor.Text = FormatValue(slider.Value);
        return valid;
    }

    private static string FormatValue(double value) => value.ToString("0", CultureInfo.CurrentCulture);

    private bool HasFocusWithin(DependencyObject target)
    {
        if (Root.XamlRoot is null) return false;
        for (var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; focused is not null; focused = VisualTreeHelper.GetParent(focused))
            if (ReferenceEquals(focused, target)) return true;
        return false;
    }

    private void AttachWheel(Slider slider)
    {
        var wheel = new WheelDeltaAccumulator();
        slider.PointerExited += (_, _) => wheel.Reset();
        slider.PointerWheelChanged += (_, args) =>
        {
            if (args.Handled || _refreshing || !slider.IsEnabled) return;
            int steps = wheel.Add(args.GetCurrentPoint(slider).Properties.MouseWheelDelta);
            slider.Value = Math.Clamp(slider.Value + steps *
                _controller.Settings.ScrollStep * (_controller.Settings.InvertScroll ? -1 : 1), slider.Minimum, slider.Maximum);
            if (_numberEditors.TryGetValue(slider, out var editor)) editor.Text = FormatValue(slider.Value);
            args.Handled = true;
        };
    }

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
            if (_sliders.TryGetValue("all", out var linked) && !_activePointers.ContainsValue(linked) && !_pending.ContainsKey("all:brightness"))
            {
                var representative = monitors.LastOrDefault(_controller.CanControl);
                if (representative is not null) linked.Value = _controller.LogicalBrightness(representative);
            }
        }
        finally { _updating = false; }
    }

    private void UpdateToolbar(IReadOnlyList<MonitorSnapshot> monitors)
    {
        LinkButton.Visibility = monitors.Count(_controller.CanControl) > 1 ? Visibility.Visible : Visibility.Collapsed;
        LinkButton.IsEnabled = !_refreshing;
        PowerMenuItem.IsEnabled = !_refreshing && (_controller.Settings.PowerOffMode is "windows" or "both" || (_controller.Settings.PowerOffMode == "ddc" && monitors.Any(monitor => monitor.Connection.Contains("DDC", StringComparison.OrdinalIgnoreCase))));
        PowerMenuItem.Visibility = _controller.Settings.PowerOffMode == "none" ? Visibility.Collapsed : Visibility.Visible;
    }

    private static TextBlock SecondaryText(string text, Thickness margin = default) => new()
    {
        Text = text, Style = (Style)Application.Current.Resources["SecondaryTextStyle"], Margin = margin,
    };

    private void RegisterFocusTarget(Control control, string id, bool enabled = true)
    {
        AutomationProperties.SetAutomationId(control, id);
        _focusTargets[id] = control;
        _controlAvailability[control] = enabled;
        control.IsEnabled = enabled && !_refreshing;
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

    private void AccessibilityChanged(AccessibilitySettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (!_closing) { ApplySystemAppearance(); QueueLayout(); } });

    private void ApplyBackdrop()
    {
        // Theme notifications can remain queued while the native window is closing.
        if (_closing) return;
        bool highContrast = _accessibility.HighContrast;
        UsesAccentSurface = !highContrast && _controller.Settings.Theme is not ("light" or "dark") && _personalization.ColoredSurface;
        AccentSurfaceColor = _systemUi.GetColorValue(Root.ActualTheme == ElementTheme.Dark ? UIColorType.AccentDark2 : UIColorType.AccentLight3);
        bool acrylic = !highContrast && _personalization.Transparency && _controller.Settings.UseAcrylic && Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported();
        if (!acrylic) { if (SystemBackdrop is not null) SystemBackdrop = null; }
        else if (UsesAccentSurface)
        {
            if (SystemBackdrop is AccentAcrylicBackdrop accent)
            {
                if (accent.SurfaceColor != AccentSurfaceColor) accent.UpdateColor(AccentSurfaceColor);
            }
            else SystemBackdrop = new AccentAcrylicBackdrop(AccentSurfaceColor);
        }
        else if (SystemBackdrop is not DesktopAcrylicBackdrop) SystemBackdrop = new DesktopAcrylicBackdrop();
        if (TintedBackground.Background is SolidColorBrush tint)
        {
            if (tint.Color != AccentSurfaceColor) tint.Color = AccentSurfaceColor;
        }
        else TintedBackground.Background = new SolidColorBrush(AccentSurfaceColor);
        TintedBackground.Visibility = !acrylic && UsesAccentSurface ? Visibility.Visible : Visibility.Collapsed;
        SolidBackground.Visibility = !acrylic && !UsesAccentSurface ? Visibility.Visible : Visibility.Collapsed;
    }

    private void QueueLayout()
    {
        if (_closing || _layoutQueued || _positioning) return;
        _layoutQueued = true;
        DispatcherQueue.TryEnqueue(() => { _layoutQueued = false; if (IsShown && !_closing) PositionPanel(); });
    }

    private void ScheduleWrite()
    {
        // Keep a steady update cadence during a drag instead of waiting for it to stop.
        if (!_debounce.IsEnabled) _debounce.Start();
    }

    private async Task FlushAsync()
    {
        _debounce.Stop();
        if (_flushing) return;
        _flushing = true;
        try
        {
            var writes = _pending.Values.ToArray(); _pending.Clear();
            var featureWrites = _pendingFeatures.Values.ToArray(); _pendingFeatures.Clear();
            foreach (var write in writes)
                await _controller.TrySetAsync(write.Id, write.Value, write.Contrast, write.Linked);
            foreach (var write in featureWrites)
                try { await _controller.SetFeatureAsync(write.Id, write.Code, write.Value); } catch (Exception exception) { ShowError(exception.Message); }
        }
        finally
        {
            _flushing = false;
            if (!_closing)
            {
                if (_pending.Count > 0 || _pendingFeatures.Count > 0) ScheduleWrite();
                RenderMonitors();
            }
        }
    }

    internal void ShowError(string message) { ErrorBar.Message = message; ErrorBar.IsOpen = true; if (IsShown) PositionPanel(); }
    internal void SetRefreshing(bool value)
    {
        if (value && !_refreshing)
        {
            _refreshFocusInterrupted = false;
            var editor = _numberEditors.Values.FirstOrDefault(HasFocusWithin);
            if (editor is not null) _refreshDraft = (AutomationProperties.GetAutomationId(editor), editor.Text, editor.SelectionStart, editor.SelectionLength);
        }
        _refreshing = value;
        ApplyRefreshingState();
        if (!value && _refreshDraft is { } draft)
        {
            _refreshDraft = null;
            if (_focusTargets.TryGetValue(draft.Id, out var target) && target is TextBox editor)
            {
                editor.Text = draft.Text;
                editor.Select(Math.Min(draft.Start, editor.Text.Length), Math.Min(draft.Length, Math.Max(0, editor.Text.Length - draft.Start)));
                if (IsShown && !_panelFocusPending && !_refreshFocusInterrupted) editor.Focus(FocusState.Programmatic);
            }
        }
        if (!value) QueuePendingPanelFocus();
        QueueLayout();
    }

    private void ApplyRefreshingState(IReadOnlyList<MonitorSnapshot>? monitors = null)
    {
        foreach (var (control, enabled) in _controlAvailability) control.IsEnabled = enabled && !_refreshing;
        // Disabled native controls already communicate busy state. Dimming the
        // whole list on every tray activation made the panel visibly flash.
        MonitorList.Opacity = 1;
        RefreshProgress.IsActive = _refreshing;
        RefreshProgress.Opacity = _refreshing ? 1 : 0;
        RefreshMenuItem.IsEnabled = !_refreshing;
        RefreshMoreMenuItem.IsEnabled = !_refreshing;
        UpdateToolbar(monitors ?? _controller.VisibleMonitors.ToList());
        LinkButton.IsEnabled = !_refreshing;
        if (_emptyMessage is not null) _emptyMessage.Text = _refreshing ? T("GENERIC_DETECTING_DISPLAYS", "Detecting displays…") : T("GENERIC_NO_COMPATIBLE_DISPLAYS", "No compatible displays found. Check that DDC/CI is enabled in your monitor settings.");
    }

    internal async Task FlushForVerificationAsync()
    {
        if (!(_controller.IsSmokeTest && _controller.IsDemo)) throw new InvalidOperationException("Tray input verification requires isolated demo smoke-test mode.");
        long deadline = Environment.TickCount64 + 5000;
        do
        {
            if (!_flushing) await FlushAsync();
            else await Task.Delay(10);
        } while ((_flushing || _pending.Count > 0 || _pendingFeatures.Count > 0) && Environment.TickCount64 < deadline);
        if (_flushing || _pending.Count > 0 || _pendingFeatures.Count > 0) throw new TimeoutException("The tray input queue did not complete.");
    }

    internal void CloseForExit() { CancelPanelPresentation(); _closing = true; _debounce.Stop(); _popupFocusWatch.Stop(); _controller.SetTrayPanelVisible(false); Close(); }
    private void Link_Click(object sender, RoutedEventArgs e) { _controller.Settings.LinkedBrightness = LinkButton.IsChecked == true; _controller.SaveSettings(); }
    private async void Power_Click(object sender, RoutedEventArgs e) => await _controller.PowerOffAsync("all");
    private void Settings_Click(object sender, RoutedEventArgs e) => _controller.OpenSettings();
    private async void Refresh_Click(object sender, RoutedEventArgs e) { if (!_refreshing) await _controller.RefreshAsync(); }
    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { DismissFromKeyboard(); e.Handled = true; }
        else if (e.Key == VirtualKey.F5) { e.Handled = true; if (!_refreshing) await _controller.RefreshAsync(); }
    }
    private static string T(string key, string fallback) => LocalizationService.Get(key, fallback);
    private static string FeatureValueName(byte code, uint value) => code switch
    {
        0x60 => value switch { 1 => "VGA", 3 => "DVI", 15 => "DisplayPort 1", 16 => "DisplayPort 2", 17 => "HDMI 1", 18 => "HDMI 2", 27 => "USB-C", _ => $"Input {value}" },
        0xD6 => value switch { 1 => "On", 2 => "Standby", 3 => "Suspend", 4 => "Off", 5 => "Power off", _ => value.ToString() },
        _ => value.ToString()
    };

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
