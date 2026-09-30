using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using TwinkleTray.Core;
using TwinkleTray.WinUI.Services;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TwinkleTray.WinUI;

internal sealed partial class AppController
{
    private async Task VerifyTrayPersonalizationAsync(UiVerificationResult result, string previews)
    {
        if (!(IsSmokeTest && IsDemo)) throw new InvalidOperationException("Appearance checks require isolated demo mode.");
        var root = (FrameworkElement)_window.Content;
        var ui = new UISettings();
        bool highContrast = new AccessibilitySettings().HighContrast;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool colored = key?.GetValue("ColorPrevalence") is int c && c != 0 && !highContrast;
        bool transparent = key?.GetValue("EnableTransparency") is not int t || t != 0;
        var background = ui.GetColorValue(UIColorType.Background);
        var fallbackTheme = 5 * background.G + 2 * background.R + background.B >= 8 * 128 ? ElementTheme.Light : ElementTheme.Dark;
        var shellTheme = key?.GetValue("SystemUsesLightTheme") is int l ? (l == 0 ? ElementTheme.Dark : ElementTheme.Light) : fallbackTheme;
        Settings = new AppSettings { Theme = "system", WindowsStyle = "win11", Language = "ko", UseAcrylic = false };
        _monitors = SettingsWindow.UiFixtureMonitors(2); _features.Clear();
        _window.ApplySettings(); _window.ShowPanel();
        await _window.WaitForPresentationForVerificationAsync();
        await UiVisualVerification.SettleAsync(root);

        async Task<Color> BackgroundPixelAsync()
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(root);
            var buffer = await bitmap.GetPixelsAsync();
            byte[] bytes = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
            int x = (int)(8 * bitmap.PixelWidth / root.ActualWidth), y = (int)(8 * bitmap.PixelHeight / root.ActualHeight);
            int offset = (y * bitmap.PixelWidth + x) * 4;
            return Color.FromArgb(bytes[offset + 3], bytes[offset + 2], bytes[offset + 1], bytes[offset]);
        }
        var expected = ui.GetColorValue(shellTheme == ElementTheme.Dark ? UIColorType.AccentDark2 : UIColorType.AccentLight3);
        var pixel = await BackgroundPixelAsync();
        result.Checks.Add(new("System tray renders the Shell accent surface instead of only coloring the slider", root.ActualTheme == shellTheme && _window.UsesAccentSurface == colored && (!colored || pixel.Equals(expected)),
            $"Shell theme={shellTheme}; actual={root.ActualTheme}; colored preference={colored}; rendered pixel={pixel}; expected accent={expected}. Actual registry and UISettings are read only; rendered solid fallback, not a resource-value-only assertion."));
        result.Previews.Add(await UiVisualVerification.CaptureAsync(root, previews, "tray-system-accent-solid", 1));

        foreach (string theme in new[] { "light", "dark" })
        {
            Settings.Theme = theme; _window.ApplySettings(); await UiVisualVerification.SettleAsync(root);
            pixel = await BackgroundPixelAsync();
            bool neutral = pixel.R == pixel.G && pixel.G == pixel.B;
            result.Checks.Add(new($"Explicit {theme} appearance removes Shell tint from rendered tray", !_window.UsesAccentSurface && (highContrast || neutral),
                $"Tint active={_window.UsesAccentSurface}; background pixel={pixel}. The Windows setting itself was not changed."));
            result.Previews.Add(await UiVisualVerification.CaptureAsync(root, previews, "tray-polished-" + theme, 1));
        }
        Settings.Theme = "system"; Settings.UseAcrylic = true; _window.ApplySettings(); await UiVisualVerification.SettleAsync(root);
        bool acrylicExpected = transparent && !highContrast && Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported();
        bool materialMatches = !acrylicExpected ? _window.SystemBackdrop is null : colored
            ? _window.SystemBackdrop is AccentAcrylicBackdrop material && material.SurfaceColor.Equals(expected)
            : _window.SystemBackdrop is DesktopAcrylicBackdrop;
        result.Checks.Add(new("Acrylic and its fallback use the Shell accent policy", materialMatches,
            $"Transparency preference={transparent}; acrylic expected={acrylicExpected}; colored={colored}; material={_window.SystemBackdrop?.GetType().Name}. Native material selected; compositor pixels are verified separately through a window capture."));
    }
}
