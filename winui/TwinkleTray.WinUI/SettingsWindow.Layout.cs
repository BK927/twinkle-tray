using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
using System.Globalization;
using System.Runtime.CompilerServices;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace TwinkleTray.WinUI;

public sealed partial class SettingsWindow
{
    private readonly Dictionary<string, bool> _expandedCards = new();
    private readonly Dictionary<string, SettingsPageState> _pageStates = new();
    private readonly List<Action> _cardSummaries = [];
    private readonly Dictionary<string, string> _lastFocusKeys = new();
    private readonly AccessibilitySettings _accessibility = new();
    private Dictionary<string, Control> _pageEditors = new();
    private string? _renderedPage;
    private bool _isRendering;
    private bool _restoringView;
    private bool _viewRestorePending;
    private bool _adjustingWindow;
    private bool _accessibilitySubscribed;
    private bool _closing;
    private int _renderVersion;
    private int _restoringVersion = -1;
    private bool _focusScrollingBeforeRestore = true;
    private string? _revealCard;

    private sealed record TextDraft(string Text, int Start, int Length, bool Invalid);
    private sealed record SettingsPageState(double Offset, string? FocusKey, Dictionary<string, TextDraft> Drafts);

    private void InitializeSettingsLayout()
    {
        Root.SizeChanged += (_, _) => UpdateNavigationLayout(Root.ActualWidth);
        Root.ActualThemeChanged += (_, _) => ApplyTheme();
        AppWindow.Changed += (_, args) => { if (args.DidSizeChange) EnsureMinimumWindowSize(); };
        try
        {
            _accessibility.HighContrastChanged += AccessibilityChanged;
            _accessibilitySubscribed = true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Some desktop sessions expose the current setting without an event source.
        }
        Closed += (_, _) =>
        {
            _closing = true;
            ++_renderVersion;
            _viewRestorePending = false;
            _restoringView = false;
            if (!_accessibilitySubscribed) return;
            try { _accessibility.HighContrastChanged -= AccessibilityChanged; }
            catch (System.Runtime.InteropServices.COMException) { }
            _accessibilitySubscribed = false;
        };
    }

    private void AccessibilityChanged(AccessibilitySettings sender, object args) => DispatcherQueue.TryEnqueue(ApplyTheme);

    private void ApplySettingsBackdrop()
    {
        if (_closing) return;
        var acrylic = _settings.WindowsStyle == "win10";
        if (!_settings.UseAcrylic || _accessibility.HighContrast || (acrylic ? !DesktopAcrylicController.IsSupported() : !MicaController.IsSupported()))
        {
            SystemBackdrop = null;
            SolidBackground.Visibility = Visibility.Visible;
            return;
        }
        if (acrylic) { if (SystemBackdrop is not DesktopAcrylicBackdrop) SystemBackdrop = new DesktopAcrylicBackdrop(); }
        else if (SystemBackdrop is not MicaBackdrop) SystemBackdrop = new MicaBackdrop();
        // Keep a themed opaque first frame while the backdrop target and XAML
        // content connect. Reveal the material only after initial presentation.
        SolidBackground.Visibility = _settingsSurfaceReady ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateNavigationLayout(double width)
    {
        if (_closing) return;
        var compact = width > 0 && width < 960;
        var target = compact ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Left;
        if (Navigation.PaneDisplayMode != target)
        {
            Navigation.PaneDisplayMode = target;
            Navigation.IsPaneOpen = !compact;
        }
        Navigation.IsPaneToggleButtonVisible = compact;
        PageContent.Margin = new Thickness(compact ? 16 : 32, compact ? 16 : 24, compact ? 16 : 32, 32);
    }

    private void EnsureMinimumWindowSize()
    {
        if (_closing || _adjustingWindow) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1d : dpi / 96d;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var margin = (int)Math.Round(16 * scale);
        var minimumWidth = Math.Min((int)Math.Round(640 * scale), Math.Max(1, area.Width - margin * 2));
        var minimumHeight = Math.Min((int)Math.Round(480 * scale), Math.Max(1, area.Height - margin * 2));
        var size = AppWindow.Size;
        if (size.Width >= minimumWidth && size.Height >= minimumHeight) return;
        _adjustingWindow = true;
        try { AppWindow.Resize(new SizeInt32(Math.Max(size.Width, minimumWidth), Math.Max(size.Height, minimumHeight))); }
        finally { _adjustingWindow = false; }
    }

    private static Style SharedStyle(string key) => (Style)Application.Current.Resources[key];

    private static Grid ResponsiveRow(FrameworkElement text, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(text);
        grid.Children.Add(control);
        var requestedWidth = control.Width;
        var requestedMax = control.MaxWidth;
        void Arrange(double width)
        {
            if (width <= 0) return;
            var stacked = width < 600;
            // The second row exists only for narrow layouts. An unconditional
            // gap adds empty space beneath every otherwise horizontal card.
            grid.RowSpacing = stacked ? 8 : 0;
            Grid.SetColumn(control, stacked ? 0 : 1);
            Grid.SetRow(control, stacked ? 1 : 0);
            Grid.SetColumnSpan(text, stacked ? 2 : 1);
            Grid.SetColumnSpan(control, stacked ? 2 : 1);
            control.MaxWidth = Math.Min(requestedMax, width);
            if (!double.IsNaN(requestedWidth)) control.Width = Math.Min(requestedWidth, width);
            control.VerticalAlignment = VerticalAlignment.Center;
            control.HorizontalAlignment = HorizontalAlignment.Left;
        }
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        grid.Loaded += (_, _) => Arrange(grid.ActualWidth);
        return grid;
    }

    private static Grid FieldGrid(params FrameworkElement[] fields)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        foreach (var field in fields)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            field.Width = double.NaN;
            field.MinWidth = 0;
            field.HorizontalAlignment = HorizontalAlignment.Stretch;
            grid.Children.Add(field);
        }
        void Arrange(double width)
        {
            var stacked = width < 600;
            grid.RowSpacing = stacked ? 12 : 0;
            for (var i = 0; i < fields.Length; i++)
            {
                Grid.SetColumn(fields[i], stacked ? 0 : i);
                Grid.SetRow(fields[i], stacked ? i : 0);
                Grid.SetColumnSpan(fields[i], stacked ? fields.Length : 1);
            }
        }
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        grid.Loaded += (_, _) => Arrange(grid.ActualWidth);
        return grid;
    }

    private void Section(string title)
    {
        var heading = new TextBlock { Text = title, Style = SharedStyle("SettingsSectionTextStyle"), Margin = new Thickness(0, 16, 0, 0) };
        PageContent.Children.Add(heading);
    }

    private Expander ExpandableCard(string key, Func<string> title, Func<string>? summary, UIElement content, bool initiallyExpanded = false)
    {
        var header = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = Label(title());
        name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        header.Children.Add(name);
        var detail = Description(summary?.Invoke() ?? "");
        if (summary is not null) header.Children.Add(detail);
        var expander = new Expander
        {
            Header = header, Content = new Border { Padding = new Thickness(16), Child = content },
            IsExpanded = _expandedCards.GetValueOrDefault(key, initiallyExpanded),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0), Tag = "card:" + key
        };
        AutomationProperties.SetAutomationId(expander, "card:" + key);
        expander.SizeChanged += (_, args) => header.MaxWidth = Math.Max(0, args.NewSize.Width - 80);
        expander.Expanding += (_, _) => _expandedCards[key] = true;
        expander.Collapsed += (_, _) => _expandedCards[key] = false;
        _cardSummaries.Add(() => { name.Text = title(); detail.Text = summary?.Invoke() ?? ""; });
        return expander;
    }

    private void ExpandNewCard(string key)
    {
        _expandedCards[key] = true;
        _revealCard = "card:" + key;
    }

    private static string EnabledSummary(bool enabled) => T(enabled ? "NATIVE_ENABLED" : "NATIVE_DISABLED", enabled ? "Enabled" : "Disabled");
    private string TargetName(string id) => id == "all" ? T("GENERIC_ALL_DISPLAYS", "All displays") :
        _monitors.FirstOrDefault(monitor => monitor.Id == id) is { } monitor ? DisplayName(monitor) : T("NATIVE_DISCONNECTED", "Disconnected display");

    private static string HotkeySummary(TwinkleTray.Core.HotkeyBinding binding)
    {
        if (binding.NativeKey.Length > 0) return T("NATIVE_KEY_" + binding.NativeKey, binding.NativeKey == "BrightnessUp" ? "Brightness up" : "Brightness down");
        var parts = new List<string>();
        foreach (var (bit, label) in new (uint, string)[] { (2, "Ctrl"), (1, "Alt"), (4, "Shift"), (8, "Win") })
            if ((binding.Modifiers & bit) != 0) parts.Add(label);
        parts.Add(KeyChoices().FirstOrDefault(item => item.Value == binding.VirtualKey.ToString(CultureInfo.InvariantCulture)).Label ?? $"0x{binding.VirtualKey:X2}");
        return string.Join(" + ", parts);
    }

    private string ActionSummary(TwinkleTray.Core.HotkeyAction action)
    {
        var name = action.Type switch
        {
            "set" => T("SETTINGS_HOTKEY_ACTION_SET", "Set value"), "offset" => T("SETTINGS_HOTKEY_ACTION_OFFSET", "Adjust value"),
            "cycle" => T("SETTINGS_HOTKEY_ACTION_CYCLE", "Cycle values"), "power" => T("PANEL_BUTTON_TURN_OFF_DISPLAYS", "Turn off displays"),
            "profile" => T("NATIVE_APPLY_PROFILE", "Apply profile"), "refresh" => T("GENERIC_REFRESH_DISPLAYS", "Refresh displays"),
            "panel" => T("NATIVE_SHOW_PANEL", "Show brightness panel"), _ => "VCP"
        };
        return action.Type is "panel" or "refresh" ? name : $"{name} · {TargetName(action.MonitorId)}";
    }

    // Actions and calibration points have no persistent IDs in the settings schema.
    // Object identity remains stable while these items are edited or reordered in this window.
    private static string ObjectKey(object value) => RuntimeHelpers.GetHashCode(value).ToString("X", CultureInfo.InvariantCulture);

    private static IEnumerable<FrameworkElement> LogicalChildren(FrameworkElement element) => element switch
    {
        Panel panel => panel.Children.OfType<FrameworkElement>(),
        Border { Child: FrameworkElement child } => [child],
        ContentControl { Content: FrameworkElement child } => [child],
        _ => []
    };

    private static void WrapFieldHeaders(FrameworkElement element)
    {
        switch (element)
        {
            case NumberBox { Header: string numberTitle } number: number.Header = Label(numberTitle); break;
            case ComboBox { Header: string choiceTitle } choice: choice.Header = Label(choiceTitle); break;
            case TextBox { Header: string textTitle } text: text.Header = Label(textTitle); break;
            case ToggleSwitch { Header: string toggleTitle } toggle: toggle.Header = Label(toggleTitle); break;
            case TimePicker { Header: string timeTitle } time: time.Header = Label(timeTitle); break;
        }
        foreach (var child in LogicalChildren(element)) WrapFieldHeaders(child);
    }

    private Dictionary<string, Control> IndexEditors()
    {
        var result = new Dictionary<string, Control>();
        var repeats = new Dictionary<string, int>();
        void Walk(FrameworkElement element, string scope)
        {
            if (element.Tag is string tag && tag.StartsWith("card:", StringComparison.Ordinal)) scope = tag;
            if (element is Control control)
            {
                var id = AutomationProperties.GetAutomationId(element);
                var label = id.Length > 0 ? id : ControlCaption(control);
                var candidate = scope + "/" + control.GetType().Name + ":" + label;
                var occurrence = repeats.GetValueOrDefault(candidate);
                repeats[candidate] = occurrence + 1;
                var editorKey = candidate + ":" + occurrence;
                result[editorKey] = control;
                var page = _page;
                control.GotFocus += (_, _) =>
                {
                    if (!_isRendering && !_restoringView && ReferenceEquals(FindFocusedEditor(), control)) _lastFocusKeys[page] = editorKey;
                };
            }
            foreach (var child in LogicalChildren(element)) Walk(child, scope);
        }
        Walk(PageContent, _page);
        return result;
    }

    private static string ControlCaption(Control control)
    {
        static string Text(object? value) => value is TextBlock block ? block.Text : value?.ToString() ?? "";
        return control switch
        {
            TextBox text => Text(text.Header), NumberBox number => Text(number.Header),
            ComboBox choice => Text(choice.Header), TimePicker time => Text(time.Header),
            ToggleSwitch toggle => Text(toggle.Header) + AutomationProperties.GetName(toggle),
            Button button => AutomationProperties.GetName(button).Length > 0 ? AutomationProperties.GetName(button) : Text(button.Content),
            CheckBox check => Text(check.Content), _ => AutomationProperties.GetName(control)
        };
    }

    private Control? FindFocusedEditor()
    {
        var focused = Root.XamlRoot is null ? null : FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        for (var current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Control control && _pageEditors.Values.Contains(control)) return control;
        return null;
    }

    private void CapturePageState()
    {
        if (_renderedPage is null || _restoringView || _viewRestorePending) return;
        var focusedControl = FindFocusedEditor();
        var focusKey = focusedControl is null ? _lastFocusKeys.GetValueOrDefault(_renderedPage) : _pageEditors.FirstOrDefault(pair => ReferenceEquals(pair.Value, focusedControl)).Key;
        if (focusedControl is null && focusKey is not null) _pageEditors.TryGetValue(focusKey, out focusedControl);
        var drafts = new Dictionary<string, TextDraft>();
        foreach (var (key, editor) in _pageEditors)
        {
            if (editor is TextBox text && (ReferenceEquals(editor, focusedControl) || text.Tag is SettingsTextField { HasError: true }))
                drafts[key] = new TextDraft(text.Text, text.SelectionStart, text.SelectionLength, text.Tag is SettingsTextField { HasError: true });
            else if (editor is NumberBox number && ReferenceEquals(editor, focusedControl))
                drafts[key] = new TextDraft(number.Text, 0, 0, false);
        }
        _pageStates[_renderedPage] = new SettingsPageState(PageScrollViewer.VerticalOffset, focusKey, drafts);
    }

    private void RestorePageState(int version)
    {
        if (_renderVersion != version || !_viewRestorePending || _restoringVersion == version) return;
        _restoringVersion = version;
        if (!_restoringView) _focusScrollingBeforeRestore = PageScrollViewer.BringIntoViewOnFocusChange;
        _restoringView = true;
        PageScrollViewer.BringIntoViewOnFocusChange = false;
        _pageStates.TryGetValue(_page, out var state);
        PageContent.UpdateLayout();
        Control? focus = null;
        if (state?.FocusKey is string key) _pageEditors.TryGetValue(key, out focus);
        if (focus is not null && !focus.IsLoaded)
        {
            // A dispatcher callback may precede Loaded for a newly created Expander body.
            // Applying a TextBox selection before Loaded lets its template reset it later.
            var awaited = focus;
            var fallback = DispatcherQueue.CreateTimer();
            fallback.Interval = TimeSpan.FromMilliseconds(250);
            fallback.IsRepeating = false;
            RoutedEventHandler? loaded = null;
            var continued = false;
            void Continue()
            {
                if (continued) return;
                continued = true;
                awaited.Loaded -= loaded;
                fallback.Stop();
                RestoreLoadedPageState(version, state);
            }
            loaded = (_, _) => Continue();
            awaited.Loaded += loaded;
            fallback.Tick += (_, _) => Continue();
            fallback.Start();
            return;
        }
        RestoreLoadedPageState(version, state);
    }

    private void RestoreLoadedPageState(int version, SettingsPageState? state)
    {
        if (_renderVersion != version) return;
        if (state is not null)
        {
            foreach (var (key, draft) in state.Drafts)
            {
                if (!_pageEditors.TryGetValue(key, out var control)) continue;
                if (control is TextBox text)
                {
                    text.Text = draft.Text;
                    if (draft.Invalid && text.Tag is SettingsTextField field) field.Validate();
                }
                else if (control is NumberBox number) number.Text = draft.Text;
            }
            if (state.FocusKey is string focusKey && _pageEditors.TryGetValue(focusKey, out var focus) && focus.IsEnabled && focus.IsLoaded)
            {
                focus.Focus(FocusState.Programmatic);
                _lastFocusKeys[_page] = focusKey;
            }
        }
        // Focus and TextBox template work can request scrolling asynchronously. Keep the
        // focus-scrolling guard until the next dispatcher turn, then apply our saved view.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_renderVersion != version) return;
            Root.UpdateLayout();
            if (state?.FocusKey is string focusKey && state.Drafts.TryGetValue(focusKey, out var draft) &&
                _pageEditors.TryGetValue(focusKey, out var focus) && focus is TextBox input)
                input.Select(Math.Min(draft.Start, input.Text.Length), Math.Min(draft.Length, Math.Max(0, input.Text.Length - draft.Start)));
            RestorePageScroll(version, state?.Offset ?? 0, state?.FocusKey is not null || state?.Offset > 0 || _revealCard is not null);
        });
    }

    private bool HasActiveCardAnimations()
    {
        foreach (var card in _pageEditors.Values.OfType<Expander>())
        {
            if (!card.IsLoaded || VisualTreeHelper.GetChildrenCount(card) == 0) return true;
            if (VisualTreeHelper.GetChild(card, 0) is not FrameworkElement templateRoot) continue;
            var groups = VisualStateManager.GetVisualStateGroups(templateRoot);
            for (var i = 0; i < groups.Count; i++)
                if (groups[i].CurrentState?.Storyboard?.GetCurrentState() == ClockState.Active) return true;
        }
        return false;
    }

    private void RestorePageScroll(int version, double savedOffset, bool restoreEditorView)
    {
        // ScrollViewer's extent can still describe the previous page after its new
        // content has loaded. Expanded templates can also grow over several frames.
        // Do not clamp a saved offset to that transient extent or declare completion
        // before the asynchronous ChangeView has actually moved the viewport. A
        // restored editor also waits for card animations: their transforms can
        // move the focused input after the extent has already stopped changing.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        double lastTarget = double.NaN;
        double lastExtent = double.NaN, lastViewport = double.NaN, lastContentHeight = double.NaN;
        TimeSpan? stableSince = null;
        int stableFrames = 0;
        void Tick()
        {
            if (_renderVersion != version) { timer.Stop(); return; }
            Root.UpdateLayout();
            double available = PageScrollViewer.ScrollableHeight;
            double extent = PageScrollViewer.ExtentHeight;
            double viewport = PageScrollViewer.ViewportHeight;
            double contentHeight = PageContent.ActualHeight + PageContent.Margin.Top + PageContent.Margin.Bottom;
            bool expired = elapsed.Elapsed >= TimeSpan.FromMilliseconds(1500);
            if (!expired && (PageScrollViewer.ViewportHeight <= 0 ||
                (available + .5 < savedOffset && elapsed.Elapsed < TimeSpan.FromMilliseconds(500)))) return;

            double target = Math.Clamp(savedOffset, 0, available);
            bool reached = Math.Abs(PageScrollViewer.VerticalOffset - target) <= .5;
            bool geometryStable = Math.Abs(extent - lastExtent) <= .5 && Math.Abs(viewport - lastViewport) <= .5 &&
                Math.Abs(contentHeight - lastContentHeight) <= .5 && Math.Abs(extent - Math.Max(viewport, contentHeight)) <= 1;
            bool stable = reached && Math.Abs(lastTarget - target) <= .5 &&
                (!restoreEditorView || geometryStable && !HasActiveCardAnimations());
            stableFrames = stable ? stableFrames + 1 : 0;
            stableSince = stable ? stableSince ?? elapsed.Elapsed : null;
            lastTarget = target;
            lastExtent = extent; lastViewport = viewport; lastContentHeight = contentHeight;
            if (!reached) PageScrollViewer.ChangeView(null, target, null, true);
            // New pages retain the quick path. Saved editor views need a short
            // quiet interval after the final layout/animation/scroll operation.
            if (!expired && (stableFrames < 2 || restoreEditorView &&
                (stableSince is null || elapsed.Elapsed - stableSince.Value < TimeSpan.FromMilliseconds(80)))) return;

            timer.Stop();
            _viewRestorePending = false;
            _restoringView = false;
            PageScrollViewer.BringIntoViewOnFocusChange = _focusScrollingBeforeRestore;
            if (_revealCard is string reveal)
            {
                var card = _pageEditors.Values.OfType<Expander>().FirstOrDefault(item => AutomationProperties.GetAutomationId(item) == reveal);
                if (card is not null) { card.StartBringIntoView(); card.Focus(FocusState.Programmatic); }
                _revealCard = null;
            }
        }
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Tick();
    }

    private SettingsTextField ValidatedText(string key, string title, string value, Action<string> changed, Func<string, string?>? validate = null)
    {
        var version = _renderVersion;
        return new(key, title, value, changed, validate,
            () => version != _renderVersion || _isRendering || _restoringView || _viewRestorePending);
    }

    private SettingsTextField ValidatedDecimal(string key, string title, double value, double minimum, double maximum, Action<double> changed, Func<double, string?>? extra = null)
    {
        bool Parse(string text, out double number) => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out number) && double.IsFinite(number);
        return ValidatedText(key, title, value.ToString(CultureInfo.CurrentCulture), text =>
        {
            if (Parse(text, out var number)) changed(number);
        }, text =>
        {
            if (!Parse(text, out var number) || number < minimum || number > maximum)
                return string.Format(CultureInfo.CurrentCulture, T("NATIVE_NUMBER_RANGE", "Enter a number from {0} to {1}."), minimum, maximum);
            return extra?.Invoke(number);
        });
    }
}

/// <summary>Keeps edits and their errors beside the input; only validated values reach settings.</summary>
internal sealed class SettingsTextField : StackPanel
{
    internal TextBox Input { get; }
    internal TextBlock Error { get; }
    internal string LastValidValue { get; private set; }
    internal bool HasError => Error.Visibility == Visibility.Visible;
    private readonly Action<string> _changed;
    private readonly Func<string, string?>? _validate;
    private readonly Func<bool> _suspended;
    public object Header
    {
        get => Input.Header;
        set
        {
            Input.Header = value is string text ? new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } : value;
            AutomationProperties.SetName(Input, value is TextBlock block ? block.Text : value?.ToString() ?? "");
        }
    }
    public string Text { get => Input.Text; set => Input.Text = value; }

    internal SettingsTextField(string key, string title, string value, Action<string> changed, Func<string, string?>? validate, Func<bool> suspended)
    {
        Spacing = 4;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        LastValidValue = value;
        _changed = changed; _validate = validate; _suspended = suspended;
        Input = new TextBox { Text = value, HorizontalAlignment = HorizontalAlignment.Stretch, Tag = this };
        Header = title;
        Error = new TextBlock { Style = (Style)Application.Current.Resources["ValidationTextStyle"], Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(Input, "field:" + key);
        AutomationProperties.SetAutomationId(Error, "error:" + key);
        AutomationProperties.SetName(Input, title);
        AutomationProperties.SetLiveSetting(Error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Children.Add(Input); Children.Add(Error);
        Input.LostFocus += (_, _) => { if (!_suspended() && Input.IsLoaded) TryCommit(); };
        Input.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter && !_suspended()) { TryCommit(); args.Handled = true; }
            else if (args.Key == Windows.System.VirtualKey.Escape) { Input.Text = LastValidValue; SetError(null); args.Handled = true; }
        };
        Input.TextChanged += (_, _) => { if (HasError && !_suspended()) Validate(); };
    }

    internal bool Validate()
    {
        var error = _validate?.Invoke(Input.Text.Trim());
        SetError(error);
        return error is null;
    }

    internal bool TryCommit()
    {
        if (!Validate()) return false;
        var value = Input.Text.Trim();
        if (value != LastValidValue) { _changed(value); LastValidValue = value; }
        Input.Text = value;
        return true;
    }

    private void SetError(string? message)
    {
        Error.Text = message ?? "";
        Error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetHelpText(Input, message ?? "");
    }
}
