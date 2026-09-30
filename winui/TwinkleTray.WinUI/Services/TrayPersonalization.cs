using Microsoft.Win32;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace TwinkleTray.WinUI.Services;

internal readonly record struct TrayPersonalization(ElementTheme Theme, bool ColoredSurface, bool Transparency)
{
    internal static TrayPersonalization Read(UISettings ui)
    {
        var background = ui.GetColorValue(UIColorType.Background);
        var fallback = 5 * background.G + 2 * background.R + background.B >= 8 * 128 ? ElementTheme.Light : ElementTheme.Dark;
        try
        {
            // Match the original tray's Shell preference, which can differ from
            // AppsUseLightTheme. Never change the user's personalization.
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var theme = key?.GetValue("SystemUsesLightTheme") is int light ? (light == 0 ? ElementTheme.Dark : ElementTheme.Light) : fallback;
            return new(theme, key?.GetValue("ColorPrevalence") is int color && color != 0,
                key?.GetValue("EnableTransparency") is not int transparent || transparent != 0);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new(fallback, false, true);
        }
    }
}
