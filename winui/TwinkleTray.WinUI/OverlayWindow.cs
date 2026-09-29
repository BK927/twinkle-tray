using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using TwinkleTray.Core;
using TwinkleTray.WinUI.Services;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace TwinkleTray.WinUI;

internal sealed class OverlayWindow : Window
{
    private readonly StackPanel _rows;
    private readonly Grid _root;
    private readonly Border _surface;
    private readonly ScrollViewer _scroll;
    private readonly List<(TextBlock Name, ProgressBar Bar, TextBlock Value)> _entries = [];
    private readonly DispatcherTimer _timer = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly nint _hwnd;
    private AppSettings? _settings;
    private bool _isShown, _closed, _layoutQueued, _positioning, _accessibilitySubscribed;

    internal bool IsContentScrollable { get; private set; }
    internal (double DesiredHeightDip, int WidthPx, int HeightPx, int WorkAreaWidthPx, int WorkAreaHeightPx, double Scale) LayoutMetrics { get; private set; }

    public OverlayWindow()
    {
        Title = "Twinkle Tray brightness overlay";
        // Keep theme resources live for explicit themes and Windows high contrast.
        _root = (Grid)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Border x:Name="Surface" Background="{ThemeResource ApplicationPageBackgroundThemeBrush}" />
                <Border BorderBrush="{ThemeResource SurfaceStrokeColorDefaultBrush}" BorderThickness="1" IsHitTestVisible="False" />
                <ScrollViewer x:Name="Scroll" Margin="16" VerticalScrollBarVisibility="Auto"
                              HorizontalScrollBarVisibility="Disabled" HorizontalScrollMode="Disabled" IsTabStop="False">
                    <StackPanel x:Name="Rows" Spacing="16" />
                </ScrollViewer>
            </Grid>
            """);
        _rows = (StackPanel)_root.FindName("Rows");
        _surface = (Border)_root.FindName("Surface");
        _scroll = (ScrollViewer)_root.FindName("Scroll");
        Content = _root;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false; presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;
        SetWindowLongPtr(_hwnd, -20, (nint)(GetWindowLongPtr(_hwnd, -20).ToInt64() | 0x08000080));
        _timer.Tick += (_, _) => { _timer.Stop(); _isShown = false; AppWindow.Hide(); };
        _root.Loaded += (_, _) => QueueLayout();
        _root.SizeChanged += (_, _) => QueueLayout();
        _rows.SizeChanged += (_, _) => QueueLayout();
        _root.ActualThemeChanged += (_, _) => ApplyBackdrop();
        try { _accessibility.HighContrastChanged += AccessibilityChanged; _accessibilitySubscribed = true; }
        catch (COMException) { /* Some unpackaged environments cannot register accessibility notifications. */ }
        Closed += (_, _) =>
        {
            _closed = true; _timer.Stop();
            if (!_accessibilitySubscribed) return;
            try { _accessibility.HighContrastChanged -= AccessibilityChanged; }
            catch (COMException) { /* The notification source may already have shut down. */ }
            finally { _accessibilitySubscribed = false; }
        };
    }

    public void ShowLevels(IEnumerable<(string Name, double Brightness)> levels, AppSettings settings)
    {
        var values = levels.ToArray();
        if (values.Length == 0) { _timer.Stop(); _isShown = false; AppWindow.Hide(); return; }
        _settings = settings;
        try { _root.Language = CultureInfo.GetCultureInfo(LocalizationService.CurrentLanguage).Name; }
        catch (CultureNotFoundException) { _root.Language = "en"; }
        _root.RequestedTheme = settings.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        ApplyBackdrop();
        int corner = settings.WindowsStyle == "win10" ? 1 : 2;
        DwmSetWindowAttribute(_hwnd, 33, ref corner, sizeof(int));
        if (_entries.Count != values.Length)
        {
            _rows.Children.Clear(); _entries.Clear();
            foreach (var _ in values)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new FontIcon { Glyph = "\uE706", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
                var content = new StackPanel { Spacing = 8 };
                var name = new TextBlock { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
                var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
                content.Children.Add(name); content.Children.Add(bar);
                Grid.SetColumn(content, 1); row.Children.Add(content);
                var number = new TextBlock { FontSize = 14, MinWidth = 32, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(number, 2); row.Children.Add(number);
                _rows.Children.Add(row);
                _entries.Add((name, bar, number));
            }
            _scroll.ChangeView(null, 0, null, true);
        }
        for (int index = 0; index < values.Length; index++)
        {
            var value = values[index];
            var entry = _entries[index];
            entry.Name.Text = value.Name;
            ToolTipService.SetToolTip(entry.Name, value.Name);
            entry.Bar.Value = double.IsFinite(value.Brightness) ? Math.Clamp(value.Brightness, 0, 100) : 0;
            entry.Value.Text = double.IsFinite(value.Brightness) ? $"{entry.Bar.Value:0}" : "—";
            AutomationProperties.SetName(entry.Bar, value.Name + " " + LocalizationService.Get("PANEL_LABEL_BRIGHTNESS", "Brightness"));
        }
        _isShown = true;
        PositionOverlay(show: true);
        QueueLayout();
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(settings.OverlayTimeoutSeconds, 1, 30));
        _timer.Start();
    }

    private void ApplyBackdrop()
    {
        bool acrylic = _settings?.UseAcrylic == true && !_accessibility.HighContrast && Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported();
        SystemBackdrop = acrylic ? SystemBackdrop ?? new DesktopAcrylicBackdrop() : null;
        _surface.Visibility = acrylic ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AccessibilityChanged(AccessibilitySettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (!_closed) { ApplyBackdrop(); QueueLayout(); } });

    private void QueueLayout()
    {
        if (_closed || _layoutQueued || _positioning) return;
        _layoutQueued = true;
        DispatcherQueue.TryEnqueue(() => { _layoutQueued = false; if (_isShown && !_closed) PositionOverlay(); });
    }

    internal void RecalculateLayoutForVerification() => PositionOverlay();

    private void PositionOverlay(bool show = false)
    {
        if (_positioning || _closed || _settings is null) return;
        _positioning = true;
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            double scale = GetDpiForWindow(_hwnd) / 96d;
            if (scale < .5) scale = _root.XamlRoot?.RasterizationScale ?? 1;
            int gap = Math.Max(1, (int)Math.Round(24 * scale));
            int width = Math.Min((int)Math.Round(360 * scale), Math.Max(1, area.Width - gap * 2));
            var outerSize = AppWindow.Size;
            var clientSize = AppWindow.ClientSize;
            int nonClientWidth = Math.Max(0, outerSize.Width - clientSize.Width);
            int nonClientHeight = Math.Max(0, outerSize.Height - clientSize.Height);
            // Borderless windows may retain a native inset; XAML measures only the client area.
            double clientWidth = Math.Max(1, width - nonClientWidth) / scale;
            double horizontalPadding = _scroll.Margin.Left + _scroll.Margin.Right;
            double verticalPadding = _scroll.Margin.Top + _scroll.Margin.Bottom;
            _rows.Measure(new Windows.Foundation.Size(Math.Max(1, clientWidth - horizontalPadding), double.PositiveInfinity));
            double desiredHeight = _rows.DesiredSize.Height + verticalPadding;
            // Additional monitors remain reachable without filling the desktop.
            int maximumHeight = Math.Max(1, Math.Min((int)Math.Round(480 * scale), area.Height - gap * 2));
            int height = Math.Min((int)Math.Ceiling(desiredHeight * scale) + nonClientHeight, maximumHeight);
            double clientHeight = Math.Max(0, height - nonClientHeight) / scale;
            IsContentScrollable = _rows.DesiredSize.Height > Math.Max(0, clientHeight - verticalPadding) + .5;
            LayoutMetrics = (desiredHeight, width, height, area.Width, area.Height, scale);
            int win10Inset = (int)Math.Round(40 * scale);
            int x = _settings.WindowsStyle == "win10" ? area.X + win10Inset : area.X + (area.Width - width) / 2;
            int y = _settings.WindowsStyle == "win10" ? area.Y + win10Inset : area.Y + area.Height - height - gap;
            x = Math.Clamp(x, area.X, area.X + Math.Max(0, area.Width - width));
            y = Math.Clamp(y, area.Y, area.Y + Math.Max(0, area.Height - height));
            if (show || AppWindow.Position.X != x || AppWindow.Position.Y != y || AppWindow.Size.Width != width || AppWindow.Size.Height != height)
                SetWindowPos(_hwnd, new nint(-1), x, y, width, height, 0x0010 | (show ? 0x0040u : 0u));
        }
        finally { _positioning = false; }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
