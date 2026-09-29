using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System.Text.Json;
using TwinkleTray.Core;
using TwinkleTray.Hardware;
using TwinkleTray.WinUI.Services;
using Windows.Foundation;

namespace TwinkleTray.WinUI;

internal sealed partial class AppController
{
    private async Task<int> VerifyUiForSmokeTestAsync()
    {
        if (!(IsSmokeTest && IsDemo)) throw new InvalidOperationException("UI verification requires isolated demo smoke-test mode.");
        string previews = Path.Combine(AppContext.BaseDirectory, "test-fixtures", "ui-previews", Guid.NewGuid().ToString("N"));
        var result = await SettingsWindow.VerifyLayoutForSmokeTestAsync(previews);
        var originalSettings = Settings;
        var originalMonitors = _monitors;
        string originalLanguage = LocalizationService.CurrentLanguage;
        var originalFeatures = _features.ToDictionary();
        var overlay = new OverlayWindow();
        try
        {
            foreach (string theme in new[] { "light", "dark" })
                foreach (int count in new[] { 1, 6, 12 })
                {
                    UiVisualVerification.RecordProgress(previews, $"Starting popup checks: {theme}, {count} displays", result);
                    Settings = SettingsWindow.UiFixtureSettings(); Settings.Theme = theme; Settings.OverlayTimeoutSeconds = 30;
                    _monitors = SettingsWindow.UiFixtureMonitors(count); _features.Clear();
                    foreach (var monitor in _monitors)
                    {
                        if (!Settings.Monitors.TryGetValue(monitor.Id, out var preferences)) Settings.Monitors[monitor.Id] = preferences = new MonitorSettings { Name = monitor.Name };
                        preferences.Features[0x12] = new() { Enabled = true, Name = "명암 — Contrast with a long display feature title" };
                        preferences.Features[0x62] = new() { Enabled = true, Name = "스피커 음량 — Monitor speaker volume" };
                        preferences.Features[0x60] = new() { Enabled = true, Name = "외부 입력 — Input source" };
                        _features[monitor.Id] = [new VcpFeature(0x12, "Contrast", 70, 100, []), new VcpFeature(0x62, "Volume", 50, 100, []), new VcpFeature(0x60, "Input source", 17, 18, [15, 17, 18])];
                    }
                    LocalizationService.Configure("ko");
                    _window.ApplySettings(); _window.ShowPanel();
                    await UiVisualVerification.WaitLoadedAsync((FrameworkElement)_window.Content);
                    _window.ShowError("연결 상태를 다시 확인하세요. This deliberately long diagnostic message must remain readable within the work area.");
                    _window.RecalculateLayoutForVerification();
                    await UiVisualVerification.SettleAsync((FrameworkElement)_window.Content);
                    var panel = _window.LayoutMetrics;
                    bool panelFits = panel.WidthPx > 0 && panel.HeightPx > 0 && panel.Scale > 0 && double.IsFinite(panel.Scale) && double.IsFinite(panel.DesiredHeightDip) &&
                        panel.WidthPx <= panel.WorkAreaWidthPx && panel.HeightPx <= panel.WorkAreaHeightPx &&
                        (panel.DesiredHeightDip <= panel.HeightPx / panel.Scale + 2 || _window.IsContentScrollable);
                    var panelObserved = await VerifyObservedPopupAsync(_window, "BodyScroll", panel.WidthPx, panel.HeightPx, !_window.IsContentScrollable);
                    var panelControls = UiVisualVerification.AuthoredElements((FrameworkElement)_window.Content).ToArray();
                    int sliders = panelControls.OfType<Slider>().Count(), choices = panelControls.OfType<ComboBox>().Count();
                    result.Checks.Add(new($"Tray work-area bounds and scrolling: {theme}, {count} displays", panelFits && panelObserved.Passed && sliders == count * 3 && choices == count,
                        $"Desired DIP height={panel.DesiredHeightDip}; planned pixels={panel.WidthPx}×{panel.HeightPx}; planned scrollable={_window.IsContentScrollable}; actual brightness/contrast/volume sliders={sliders} (expected {count * 3}); input choices={choices} (expected {count}). {panelObserved.Details}"));
                    int generation = _window.RenderGeneration;
                    _monitors = _monitors.Select(monitor => monitor with { Brightness = 47 }).ToArray();
                    _window.RenderMonitors();
                    await UiVisualVerification.SettleAsync((FrameworkElement)_window.Content);
                    result.Checks.Add(new($"Tray value refresh preserves its control tree: {theme}, {count} displays", _window.RenderGeneration == generation,
                        $"Generation before={generation}; after={_window.RenderGeneration}"));

                    overlay.ShowLevels(_monitors.Select(monitor => (monitor.Name, monitor.Brightness)), Settings);
                    await UiVisualVerification.WaitLoadedAsync((FrameworkElement)overlay.Content);
                    overlay.RecalculateLayoutForVerification();
                    await UiVisualVerification.SettleAsync((FrameworkElement)overlay.Content);
                    var osd = overlay.LayoutMetrics;
                    bool overlayFits = osd.WidthPx > 0 && osd.HeightPx > 0 && osd.Scale > 0 && double.IsFinite(osd.Scale) && double.IsFinite(osd.DesiredHeightDip) &&
                        osd.WidthPx <= osd.WorkAreaWidthPx && osd.HeightPx <= osd.WorkAreaHeightPx &&
                        (osd.DesiredHeightDip <= osd.HeightPx / osd.Scale + 2 || overlay.IsContentScrollable);
                    var overlayObserved = await VerifyObservedPopupAsync(overlay, "Scroll", osd.WidthPx, osd.HeightPx, !overlay.IsContentScrollable);
                    result.Checks.Add(new($"OSD work-area bounds and scrolling: {theme}, {count} displays", overlayFits && overlayObserved.Passed,
                        $"Desired DIP height={osd.DesiredHeightDip}; planned pixels={osd.WidthPx}×{osd.HeightPx}; planned scrollable={overlay.IsContentScrollable}. {overlayObserved.Details}"));
                    if (theme == "light" && count == 6)
                    {
                        result.Previews.Add(await UiVisualVerification.CaptureAsync((FrameworkElement)_window.Content, previews, "tray", 1));
                        result.Previews.Add(await UiVisualVerification.CaptureAsync((FrameworkElement)overlay.Content, previews, "osd", 1));
                    }
                    UiVisualVerification.RecordProgress(previews, $"Popup checks complete: {theme}, {count} displays", result);
                }
        }
        catch (Exception exception) { result.Errors.Add(exception.ToString()); }
        finally
        {
            UiVisualVerification.RecordProgress(previews, "Restoring isolated smoke-test window state", result);
            overlay.Close(); Settings = originalSettings; _monitors = originalMonitors;
            _features.Clear(); foreach (var pair in originalFeatures) _features[pair.Key] = pair.Value;
            LocalizationService.Configure(originalLanguage); _window.ApplySettings(); _window.HidePanel();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-layout-test.json"), JsonSerializer.Serialize(new
            {
                result.Passed, NativeWinUI = true, ViewportSimulation = true, WindowsDpiChanged = false,
                NativeDpiSessionMatrixTested = false, SystemHighContrast = result.SystemHighContrast, HighContrastSessionTested = false,
                HardwareWrites = 0, UserSettingsWrites = 0,
                Scope = "Real loaded, measured and arranged XAML at simulated DIP viewports. Preview names 100/125/150/200 percent denote requested relative render scales on the current host rasterization scale, not native DPI sessions or a guaranteed pixels-per-DIP ratio. Each preview records its actual pixel dimensions, root DIPs, observed host scale, and effective pixels per DIP.",
                Limitations = "Control-template internals and intentionally clipped scroll content are excluded. Geometry tests do not verify glyph raster quality; compare the captured PNGs visually. Windows high-contrast mode was observed, not changed or exercised.",
                MatrixCases = result.Cases.Count, result.Cases, result.Checks, result.Previews, result.Palettes, result.Errors
            }, new JsonSerializerOptions { WriteIndented = true }));
            UiVisualVerification.RecordProgress(previews, "UI report written", result);
        }
        if (!result.Passed) throw new InvalidOperationException($"UI layout verification failed. Inspect ui-layout-test.json ({result.Cases.Count} cases, {result.Errors.Count} harness errors).");
        return result.Cases.Count;
    }

    private static async Task<UiCheck> VerifyObservedPopupAsync(Window window, string scrollName, int expectedWidth, int expectedHeight, bool expectedToFit)
    {
        var root = (FrameworkElement)window.Content;
        await UiVisualVerification.SettleAsync(root);
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var position = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        var clientSize = window.AppWindow.ClientSize;
        double scale = root.XamlRoot?.RasterizationScale ?? 0;
        var failures = new List<string>();
        if (size.Width <= 0 || size.Height <= 0 || size.Width != expectedWidth || size.Height != expectedHeight)
            failures.Add("The actual native window size does not match the requested size.");
        if (position.X < area.X || position.Y < area.Y || (long)position.X + size.Width > (long)area.X + area.Width || (long)position.Y + size.Height > (long)area.Y + area.Height)
            failures.Add("The actual native window is outside its display's work area.");
        if (!root.IsLoaded || scale <= 0 || !double.IsFinite(scale) || !double.IsFinite(root.ActualWidth) || !double.IsFinite(root.ActualHeight) || root.ActualWidth <= 0 || root.ActualHeight <= 0 ||
            clientSize.Width <= 0 || clientSize.Height <= 0 || Math.Abs(root.ActualWidth * scale - clientSize.Width) > 3 || Math.Abs(root.ActualHeight * scale - clientSize.Height) > 3)
            failures.Add("The arranged XAML root does not fill the actual native client area.");
        string scrolling = "ScrollViewer missing";
        if (root.FindName(scrollName) is not ScrollViewer scroll) failures.Add("The actual popup ScrollViewer is missing.");
        else
        {
            var bounds = scroll.TransformToVisual(root).TransformBounds(new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight));
            if (!double.IsFinite(scroll.ViewportWidth) || !double.IsFinite(scroll.ViewportHeight) || scroll.ViewportWidth <= 0 || scroll.ViewportHeight <= 0 ||
                bounds.Left < -2 || bounds.Top < -2 || bounds.Right > root.ActualWidth + 2 || bounds.Bottom > root.ActualHeight + 2)
                failures.Add("The actual scroll viewport is empty or extends outside the XAML root.");
            double contentHeight = (scroll.Content as FrameworkElement)?.ActualHeight ?? 0;
            if (!double.IsFinite(contentHeight) || contentHeight <= 0) failures.Add("The scroll content is not arranged.");
            bool overflowing = contentHeight > scroll.ViewportHeight + 1 || scroll.ExtentHeight > scroll.ViewportHeight + 1;
            if (overflowing && (scroll.ScrollableHeight <= 0 || scroll.VerticalScrollMode == ScrollMode.Disabled))
                failures.Add("Arranged content exceeds the viewport but cannot scroll.");
            if (expectedToFit && scroll.ScrollableHeight > 1)
                failures.Add("Content expected to fit requires scrolling; native client insets may be missing from the requested size.");
            double oldOffset = scroll.VerticalOffset;
            double requestedOffset = Math.Min(60, scroll.ScrollableHeight);
            bool moved = true;
            if (overflowing && requestedOffset > 0)
            {
                scroll.ChangeView(null, requestedOffset, null, true);
                await UiVisualVerification.SettleAsync(root);
                moved = Math.Abs(scroll.VerticalOffset - requestedOffset) <= 1;
                if (!moved) failures.Add("A real ChangeView request did not reach the expected offset.");
                scroll.ChangeView(null, oldOffset, null, true);
                await UiVisualVerification.SettleAsync(root);
            }
            scrolling = $"viewport={scroll.ViewportWidth:0.##}×{scroll.ViewportHeight:0.##}; arranged content height={contentHeight:0.##}; extent={scroll.ExtentHeight:0.##}; scrollable height={scroll.ScrollableHeight:0.##}; actual scroll movement={moved}";
        }
        return new("Observed native popup geometry", failures.Count == 0,
            $"Observed outer pixels=({position.X},{position.Y}) {size.Width}×{size.Height}; client pixels={clientSize.Width}×{clientSize.Height}; work area=({area.X},{area.Y}) {area.Width}×{area.Height}; root DIP={root.ActualWidth:0.##}×{root.ActualHeight:0.##}; scale={scale}; {scrolling}. " + string.Join(" ", failures));
    }
}
