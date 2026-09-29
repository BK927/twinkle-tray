using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TwinkleTray.Core;
using Windows.Graphics;

namespace TwinkleTray.WinUI;

internal sealed class OverlayWindow : Window
{
    private readonly StackPanel _rows = new() { Spacing = 10 };
    private readonly Border _root;
    private readonly DispatcherTimer _timer = new();
    private readonly nint _hwnd;

    public OverlayWindow()
    {
        Title = "Twinkle Tray brightness overlay";
        _root = new Border { Child = _rows, Padding = new Thickness(20, 14, 20, 14), CornerRadius = new CornerRadius(8), Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        Content = _root;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false; presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;
        SetWindowLongPtr(_hwnd, -20, (nint)(GetWindowLongPtr(_hwnd, -20).ToInt64() | 0x08000080));
        _timer.Tick += (_, _) => { _timer.Stop(); AppWindow.Hide(); };
    }

    public void ShowLevels(IEnumerable<(string Name, double Brightness)> levels, AppSettings settings)
    {
        _rows.Children.Clear();
        _root.RequestedTheme = settings.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        int count = 0;
        foreach (var level in levels)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = "\uE706", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(new TextBlock { Text = level.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp(level.Brightness, 0, 100) });
            Grid.SetColumn(content, 1); row.Children.Add(content);
            var text = new TextBlock { Text = $"{level.Brightness:0}", VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(text, 2); row.Children.Add(text);
            _rows.Children.Add(row); count++;
        }
        if (count == 0) return;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = GetDpiForWindow(_hwnd) / 96d;
        int width = (int)(320 * scale), height = (int)((28 + count * 52) * scale);
        int x = settings.WindowsStyle == "win10" ? area.X + (int)(40 * scale) : area.X + (area.Width - width) / 2;
        int y = settings.WindowsStyle == "win10" ? area.Y + (int)(40 * scale) : area.Y + area.Height - height - (int)(28 * scale);
        SetWindowPos(_hwnd, new nint(-1), x, y, width, height, 0x0010 | 0x0040);
        _timer.Stop(); _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(settings.OverlayTimeoutSeconds, 1, 30)); _timer.Start();
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
