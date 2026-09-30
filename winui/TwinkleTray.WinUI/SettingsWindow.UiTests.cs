using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TwinkleTray.Core;
using TwinkleTray.Hardware;
using TwinkleTray.WinUI.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

namespace TwinkleTray.WinUI;

internal sealed record UiLayoutCase(string Page, string Language, string Theme, string Fixture,
    int WidthDip, int HeightDip, double ActualWidthDip, double ActualHeightDip, double RasterizationScale,
    int CheckedElements, int ScrollableElements, IReadOnlyList<string> Failures);
internal sealed record UiCheck(string Name, bool Passed, string Details);
internal sealed record UiPreview(string Surface, string Path, double RequestedRenderScale, int PixelWidth, int PixelHeight, double ObservedRasterizationScale,
    double RootWidthDip, double RootHeightDip, double EffectivePixelsPerDipX, double EffectivePixelsPerDipY);
internal sealed record UiPalette(string Language, string Theme, string Foreground, string Background, double? ContrastRatio, string Scope);
internal sealed class UiVerificationResult
{
    public List<UiLayoutCase> Cases { get; } = [];
    public List<UiCheck> Checks { get; } = [];
    public List<UiPreview> Previews { get; } = [];
    public List<UiPalette> Palettes { get; } = [];
    public List<string> Errors { get; } = [];
    public bool SystemHighContrast { get; } = new AccessibilitySettings().HighContrast;
    public bool Passed => Errors.Count == 0 && Cases.All(item => item.Failures.Count == 0) && Checks.All(item => item.Passed);
}

public sealed partial class SettingsWindow
{
    private static readonly string[] UiPages = ["general", "monitors", "features", "time", "hotkeys", "idle", "profiles", "sensors", "advanced", "updates", "about"];
    private string _uiLastPageRestoreSnapshot = "";

    internal static async Task<UiVerificationResult> VerifyLayoutForSmokeTestAsync(string previewDirectory)
    {
        if (!Program.Options.SmokeTest) throw new InvalidOperationException("UI verification requires isolated smoke-test mode.");
        var result = new UiVerificationResult();
        string language = LocalizationService.CurrentLanguage;
        int saveCount = 0;
        var window = new SettingsWindow(UiFixtureSettings(), UiFixtureMonitors(), () => saveCount++, () => { }, new SettingsActions
        {
            CheckUpdatesAsync = () => Task.CompletedTask, InstallUpdateAsync = () => Task.CompletedTask,
            ImportSettingsAsync = () => Task.CompletedTask, ImportKnownDisplaysAsync = () => Task.CompletedTask,
            ExportSettingsAsync = () => Task.CompletedTask, ResetSettingsAsync = () => Task.CompletedTask,
            ReapplyImportAsync = () => Task.CompletedTask, GenerateDiagnosticsAsync = () => Task.CompletedTask,
            OpenLogs = () => { }, ApplyProfileAsync = _ => Task.CompletedTask,
            GetCoordinatesAsync = () => Task.FromResult((37.5665, 126.9780)),
            QuerySensorsAsync = () => Task.FromResult<IReadOnlyList<AmbientLightDevice>>([]),
            QueryFeaturesAsync = _ => Task.FromResult<IReadOnlyList<VcpFeature>>([new(0x12, "Contrast", 70, 100, []), new(0x62, "Volume", 50, 100, [])])
        });
        try
        {
            UiVisualVerification.RecordProgress(previewDirectory, "Loading isolated settings window", result);
            await window.VerifyInitialPresentationAsync(result);
            window._settings.UseAcrylic = false;
            window.ApplyTheme();
            foreach (var monitor in window._monitors)
                window._features[monitor.Id] = await window._actions.QueryFeaturesAsync!(monitor.Id);
            foreach (var locale in new[] { "ko", "en" })
                foreach (var theme in new[] { "light", "dark" })
                    foreach (var size in new[] { (Width: 640, Height: 480), (Width: 1040, Height: 740), (Width: 1440, Height: 900) })
                    {
                        window._settings.Language = locale; window._settings.Theme = theme;
                        window.Localize(); window.ApplyTheme();
                        await window.SetUiViewportAsync(size.Width, size.Height);
                        foreach (var page in UiPages)
                        {
                            await window.SelectUiPageAsync(page, expand: true);
                            result.Cases.Add(window.CheckUiLayout(page, locale, theme, "long labels, six monitors, populated editors", size.Width, size.Height));
                            UiVisualVerification.RecordProgress(previewDirectory, $"Settings matrix completed: {locale}/{theme}/{size.Width}×{size.Height}/{page}", result);
                            if (size.Width == 1040 && page == "general")
                                result.Palettes.Add(UiVisualVerification.ObservePalette(window.Root, window.PageContent, locale, theme));
                            if (locale == "ko" && theme == "light" && size.Width == 1040 && page is "general" or "monitors" or "profiles")
                                foreach (double scale in page == "general" ? new[] { 1d, 1.25, 1.5, 2d } : new[] { 1d })
                                    result.Previews.Add(await UiVisualVerification.CaptureAsync(window.Root, previewDirectory, "settings-" + page, scale));
                            if (locale == "ko" && page == "general" && theme == "dark" && size.Width == 1040)
                                result.Previews.Add(await UiVisualVerification.CaptureAsync(window.Root, previewDirectory, "settings-general-dark", 1));
                            if (locale == "ko" && page == "general" && theme == "light" && size.Width == 640)
                                result.Previews.Add(await UiVisualVerification.CaptureAsync(window.Root, previewDirectory, "settings-compact", 1));
                        }
                    }

            window._settings = new AppSettings { Language = "ko", Theme = "light", UseAcrylic = false };
            window._monitors = []; window._features.Clear(); window.Localize(); window.ApplyTheme();
            await window.SetUiViewportAsync(640, 480);
            foreach (var page in UiPages)
            {
                await window.SelectUiPageAsync(page, expand: true);
                result.Cases.Add(window.CheckUiLayout(page, "ko", "light", "empty collections and no monitors", 640, 480));
                UiVisualVerification.RecordProgress(previewDirectory, "Empty settings page completed: " + page, result);
            }
            UiVisualVerification.RecordProgress(previewDirectory, "Starting editor interaction checks", result);
            await window.VerifyUiInteractionsAsync(result, () => saveCount, previewDirectory);
            UiVisualVerification.RecordProgress(previewDirectory, "Editor interaction checks complete", result);
        }
        catch (Exception exception) { result.Errors.Add(exception.ToString()); }
        finally { window.Close(); LocalizationService.Configure(language); }
        return result;
    }

    private async Task VerifyInitialPresentationAsync(UiVerificationResult result)
    {
        _settings.UseAcrylic = true;
        ApplyTheme();
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var beforeShow = SettingsWindowBounds();
        bool hiddenBeforeShow = !SettingsTestIsWindowVisible(hwnd);
        ShowPrepared();
        int initialResult = SettingsTestDwmGetWindowAttribute(hwnd, 14, out int initialCloak, sizeof(int));
        await WaitForInitialPresentationForVerificationAsync();
        await UiVisualVerification.WaitLoadedAsync(Root);
        int revealedResult = SettingsTestDwmGetWindowAttribute(hwnd, 14, out int revealedCloak, sizeof(int));
        result.Checks.Add(new("Settings first show retains its prepared bounds through cloaked layout and native reveal",
            hiddenBeforeShow && InitialPresentationCloaked && initialResult >= 0 && initialCloak != 0 &&
            InitialPresentationLayoutReady && InitialPresentationHadSolidBackground && InitialPresentationRevealed &&
            revealedResult >= 0 && revealedCloak == 0 && SettingsTestIsWindowVisible(hwnd) &&
            beforeShow.Equals(InitialPresentationBounds) && beforeShow.Equals(InitialRevealBounds),
            $"Initially hidden={hiddenBeforeShow}; cloak before/after={initialCloak}/{revealedCloak}; layout ready={InitialPresentationLayoutReady}; themed underlay={InitialPresentationHadSolidBackground}; before={beforeShow}; reveal={InitialRevealBounds}. Native visibility, DWM cloak and geometry; not a black-pixel capture."));

        // Reusing an existing settings window must retain a user's chosen size.
        AppWindow.Resize(new SizeInt32(beforeShow.Width + 24, beforeShow.Height + 24));
        await UiVisualVerification.SettleAsync(Root);
        var resized = SettingsWindowBounds();
        ShowPrepared();
        await WaitForInitialPresentationForVerificationAsync();
        result.Checks.Add(new("Reopening settings preserves the existing window bounds", resized.Equals(SettingsWindowBounds()),
            $"Requested again at {resized}; observed={SettingsWindowBounds()}. Same window instance."));

        var cancelled = new SettingsWindow(new AppSettings { Language = "ko", UseAcrylic = true }, [], () => { }, () => { });
        cancelled.ShowPrepared();
        cancelled.Close();
        await cancelled.WaitForInitialPresentationForVerificationAsync();
        result.Checks.Add(new("Closing settings during preparation cancels its pending reveal",
            cancelled.InitialPresentationCancelled && !cancelled.InitialPresentationRevealed,
            $"Cancelled={cancelled.InitialPresentationCancelled}; revealed after close={cancelled.InitialPresentationRevealed}. Immediate close before the first asynchronous frame."));
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool SettingsTestIsWindowVisible(nint hwnd);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int SettingsTestDwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    private async Task SetUiViewportAsync(int width, int height)
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        AppWindow.Resize(new SizeInt32((int)Math.Ceiling(width * scale) + 32, (int)Math.Ceiling(height * scale) + 64));
        // This constrains the actual arranged XAML root in DIPs. It does not alter Windows DPI.
        Root.Width = width; Root.Height = height;
        Root.HorizontalAlignment = HorizontalAlignment.Left; Root.VerticalAlignment = VerticalAlignment.Top;
        await UiVisualVerification.SettleAsync(Root);
    }

    private async Task SelectUiPageAsync(string page, bool expand, bool resetScroll = true)
    {
        var item = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>().Single(value => (string?)value.Tag == page);
        if (ReferenceEquals(Navigation.SelectedItem, item)) { _page = page; RenderPage(); }
        else Navigation.SelectedItem = item;
        _uiLastPageRestoreSnapshot = UiRestoreSnapshot();
        await WaitForUiRestorationAsync("Selecting page " + page);
        if (expand)
        {
            foreach (var expander in UiVisualVerification.AuthoredElements(PageContent, includeCollapsed: true).OfType<Expander>()) expander.IsExpanded = true;
            await Task.Delay(180); // Expander templates may animate their clipping rectangle.
            await UiVisualVerification.SettleAsync(Root);
        }
        if (resetScroll) FindPageScrollViewer()?.ChangeView(null, 0, null, true);
        await UiVisualVerification.SettleAsync(Root);
    }

    private async Task WaitForUiRestorationAsync(string stage)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            while ((_viewRestorePending || _restoringView) && watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                // Restoration performs its own layout and settling. Forcing layout
                // on every poll can starve the low-priority callback being awaited.
                await Task.Delay(25);
            }
            if (_viewRestorePending || _restoringView) break;
            await UiVisualVerification.SettleAsync(Root);
            if (!_viewRestorePending && !_restoringView) return;
        }
        throw new TimeoutException($"{stage}: editor restoration did not complete within three seconds. {UiRestoreSnapshot()}");
    }

    private ScrollViewer? FindPageScrollViewer() => Navigation.Content as ScrollViewer;

    private UiLayoutCase CheckUiLayout(string page, string language, string theme, string fixture, int width, int height)
    {
        var failures = new List<string>();
        if (!Root.IsLoaded || Root.XamlRoot is null) failures.Add("The XAML root is not loaded.");
        if (Math.Abs(Root.ActualWidth - width) > 1 || Math.Abs(Root.ActualHeight - height) > 1) failures.Add("The arranged root does not match the requested DIP viewport.");
        if (!Root.ActualTheme.ToString().Equals(theme, StringComparison.OrdinalIgnoreCase)) failures.Add("The requested XAML theme was not applied.");
        if (PageContent.Children.Count == 0 || PageContent.ActualWidth < 1 || PageContent.ActualHeight < 1) failures.Add("The page has no arranged content.");
        int count = 0;
        foreach (var element in UiVisualVerification.AuthoredElements(PageContent))
        {
            if (!UiVisualVerification.RequiresVisibleBounds(element)) continue;
            count++;
            string name = UiVisualVerification.Describe(element);
            if (!double.IsFinite(element.ActualWidth) || !double.IsFinite(element.ActualHeight) || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            { failures.Add(name + ": zero or nonfinite arranged bounds."); continue; }
            var bounds = element.TransformToVisual(PageContent).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || bounds.Left < -2 || bounds.Right > PageContent.ActualWidth + 2)
                failures.Add(name + ": extends outside the page's horizontal viewport.");
            // Scroll content is intentionally taller than its viewport; authored child layout must still fit its own parent.
            if (element.Parent is FrameworkElement parent && parent is not ScrollViewer && parent.ActualWidth > 0 && parent.ActualHeight > 0)
            {
                var local = element.TransformToVisual(parent).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                if (local.Left < -2 || local.Right > parent.ActualWidth + 2 || local.Top < -2 || local.Bottom > parent.ActualHeight + 2)
                    failures.Add(name + ": authored child exceeds its parent's arranged bounds.");
            }
        }
        var scroll = FindPageScrollViewer();
        if (scroll is null || scroll.ViewportWidth <= 0 || scroll.ViewportHeight <= 0) failures.Add("The page scroll viewport is not measurable.");
        else
        {
            var bounds = scroll.TransformToVisual(Root).TransformBounds(new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight));
            if (bounds.Left < -2 || bounds.Right > Root.ActualWidth + 2 || bounds.Top < -2 || bounds.Bottom > Root.ActualHeight + 2)
                failures.Add("The page scroll viewport extends outside the window's DIP viewport.");
            if (PageContent.ActualHeight > scroll.ViewportHeight + 2 && scroll.ScrollableHeight <= 0) failures.Add("Tall page content cannot scroll.");
        }
        return new(page, language, theme, fixture, width, height, Root.ActualWidth, Root.ActualHeight, Root.XamlRoot?.RasterizationScale ?? 0,
            count, scroll?.ScrollableHeight > 0 ? 1 : 0, failures.Distinct().ToArray());
    }

    private T UiElement<T>(string id) where T : FrameworkElement => UiVisualVerification.AuthoredElements(PageContent, includeCollapsed: true)
        .OfType<T>().Single(element => AutomationProperties.GetAutomationId(element) == id);

    private async Task VerifyUiInteractionsAsync(UiVerificationResult result, Func<int> saveCount, string previewDirectory)
    {
        // These are real loaded controls and production focus/commit handlers, with inert persistence callbacks.
        _pageStates.Clear(); _expandedCards.Clear(); _lastFocusKeys.Clear();
        _settings = UiFixtureSettings(); _settings.Sensor.Provider = "yocto";
        _monitors = UiFixtureMonitors(); Localize(); ApplyTheme();
        await SetUiViewportAsync(1040, 740);
        await SelectUiPageAsync("sensors", expand: true);
        var endpoint = UiElement<TextBox>("field:sensor:endpoint");
        var field = (SettingsTextField)endpoint.Tag;
        endpoint.StartBringIntoView(); await UiVisualVerification.SettleAsync(Root);
        string savedEndpoint = _settings.Sensor.Endpoint;
        int beforeInvalid = saveCount();
        bool focused = endpoint.Focus(FocusState.Programmatic);
        endpoint.Text = "not a valid hub URL";
        bool blurred = GeneralNavigation.Focus(FocusState.Programmatic);
        await UiVisualVerification.SettleAsync(Root);
        result.Checks.Add(new("Invalid sensor URL stays inline and cannot update settings", focused && blurred && field.HasError &&
            field.Error.ActualHeight > 0 && _settings.Sensor.Endpoint == savedEndpoint && saveCount() == beforeInvalid,
            $"Focused={focused}; blurred={blurred}; inline error={field.HasError}; saves before/after={beforeInvalid}/{saveCount()}"));
        result.Cases.Add(CheckUiLayout("sensors", "ko", "light", "visible inline validation error", 1040, 740));
        result.Previews.Add(await UiVisualVerification.CaptureAsync(Root, previewDirectory, "settings-inline-error", 1));
        endpoint.Focus(FocusState.Programmatic); endpoint.Text = "http://127.0.0.1:5555";
        GeneralNavigation.Focus(FocusState.Programmatic); await UiVisualVerification.SettleAsync(Root);
        result.Checks.Add(new("Corrected sensor URL clears the inline error and commits once", !field.HasError &&
            _settings.Sensor.Endpoint == "http://127.0.0.1:5555" && saveCount() == beforeInvalid + 1,
            $"Inline error={field.HasError}; saves before/after={beforeInvalid}/{saveCount()}"));

        await SetUiViewportAsync(640, 480);
        await SelectUiPageAsync("profiles", expand: true);
        var card = UiElement<Expander>("card:profile:fixture-profile");
        card.IsExpanded = false; await Task.Delay(180);
        UpdateMonitors(_monitors.Reverse().ToArray()); await WaitForUiRestorationAsync("Restoring collapsed profile after monitor refresh");
        var rebuilt = UiElement<Expander>("card:profile:fixture-profile");
        result.Checks.Add(new("Profile expansion survives a monitor refresh by stable ID", !rebuilt.IsExpanded,
            $"Expanded after refresh={rebuilt.IsExpanded}"));
        rebuilt.IsExpanded = true; await Task.Delay(180); await UiVisualVerification.SettleAsync(Root);
        var name = UiElement<TextBox>("field:profile:fixture-profile:name");
        name.StartBringIntoView(); await UiVisualVerification.SettleAsync(Root);
        bool nameFocused = name.Focus(FocusState.Programmatic);
        name.Select(2, 5);
        var scroll = FindPageScrollViewer()!;
        scroll.ChangeView(null, Math.Min(160, scroll.ScrollableHeight), null, true);
        await UiVisualVerification.SettleAsync(Root);
        double savedOffset = scroll.VerticalOffset;
        await SelectUiPageAsync("general", expand: false, resetScroll: false);
        await SelectUiPageAsync("profiles", expand: false, resetScroll: false);
        name = UiElement<TextBox>("field:profile:fixture-profile:name");
        bool restoredFocus = UiVisualVerification.HasFocusWithin(name);
        result.Checks.Add(new("Page navigation restores editor focus, selection and scroll offset", nameFocused && restoredFocus &&
            name.SelectionStart == 2 && name.SelectionLength == 5 && Math.Abs(scroll.VerticalOffset - savedOffset) <= 2,
            $"Focused={restoredFocus}; selection={name.SelectionStart}/{name.SelectionLength}; offset saved/restored={savedOffset}/{scroll.VerticalOffset}; reentry={_uiLastPageRestoreSnapshot}; settled={UiRestoreSnapshot()}"));
        bool singleFocusRequested = name.Focus(FocusState.Programmatic);
        await UiVisualVerification.SettleAsync(Root);
        name.Select(2, 5);
        bool singleBaseline = singleFocusRequested && UiVisualVerification.HasFocusWithin(name) && name.SelectionStart == 2 && name.SelectionLength == 5;
        var previousEditor = name;
        int previousRender = _renderVersion;
        UpdateMonitors(_monitors.Reverse().ToArray());
        string singleState = UiRestoreSnapshot();
        await WaitForUiRestorationAsync("Restoring focused editor after one monitor refresh");
        name = UiElement<TextBox>("field:profile:fixture-profile:name");
        result.Checks.Add(new("A monitor refresh restores the active profile editor selection", singleBaseline && _renderVersion > previousRender && !ReferenceEquals(name, previousEditor) && UiVisualVerification.HasFocusWithin(name) &&
            name.SelectionStart == 2 && name.SelectionLength == 5,
            $"Baseline focus/selection={singleBaseline}; editor rebuilt={!ReferenceEquals(name, previousEditor)}; focus restored={UiVisualVerification.HasFocusWithin(name)}; selection after refresh={name.SelectionStart}/{name.SelectionLength}; captured={singleState}; settled={UiRestoreSnapshot()}"));
        string committedName = _settings.Profiles[0].Name;
        const string draftName = "아직 저장하지 않은 프로필 이름 — pending editor draft";
        bool doubleFocusRequested = name.Focus(FocusState.Programmatic);
        await UiVisualVerification.SettleAsync(Root);
        name.Text = draftName; name.Select(2, 5);
        bool doubleBaseline = doubleFocusRequested && UiVisualVerification.HasFocusWithin(name) && name.Text == draftName && name.SelectionStart == 2 && name.SelectionLength == 5;
        double draftOffset = scroll.VerticalOffset;
        previousEditor = name; previousRender = _renderVersion;
        UpdateMonitors(_monitors.Reverse().ToArray());
        string firstState = UiRestoreSnapshot();
        UpdateMonitors(_monitors.Reverse().ToArray()); // Deliberately do not yield before the second rebuild.
        string secondState = UiRestoreSnapshot();
        await WaitForUiRestorationAsync("Restoring uncommitted draft after two consecutive monitor refreshes");
        name = UiElement<TextBox>("field:profile:fixture-profile:name");
        result.Checks.Add(new("Consecutive refreshes preserve an uncommitted text draft", doubleBaseline && _renderVersion == previousRender + 2 && !ReferenceEquals(name, previousEditor) && UiVisualVerification.HasFocusWithin(name) &&
            name.Text == draftName && _settings.Profiles[0].Name == committedName && name.SelectionStart == 2 && name.SelectionLength == 5 && Math.Abs(scroll.VerticalOffset - draftOffset) <= 2,
            $"Baseline focus/draft/selection={doubleBaseline}; render count={_renderVersion - previousRender}; draft restored={name.Text == draftName}; model unchanged={_settings.Profiles[0].Name == committedName}; selection={name.SelectionStart}/{name.SelectionLength}; offset={scroll.VerticalOffset}/{draftOffset}; first={firstState}; second={secondState}; settled={UiRestoreSnapshot()}"));
    }

    private string UiRestoreSnapshot()
    {
        var state = _pageStates.GetValueOrDefault("profiles");
        string drafts = state is null ? "none" : string.Join(",", state.Drafts.Values.Select(draft => $"length:{draft.Text.Length}/selection:{draft.Start}:{draft.Length}"));
        return $"version:{_renderVersion}/pending:{_viewRestorePending}/restoring:{_restoringView}/focus:{state?.FocusKey ?? "none"}/drafts:[{drafts}]/savedOffset:{state?.Offset}/actualOffset:{PageScrollViewer.VerticalOffset}/scrollable:{PageScrollViewer.ScrollableHeight}/extent:{PageScrollViewer.ExtentHeight}/viewport:{PageScrollViewer.ViewportHeight}";
    }

    internal static IReadOnlyList<MonitorSnapshot> UiFixtureMonitors(int count = 6) => Enumerable.Range(0, count).Select(index =>
        new MonitorSnapshot("ui-fixture:" + index, $"{index + 1}. 작업실 색상 검수용 디스플레이 — Long monitor name for responsive layout and accessible controls", index == 1 ? "Internal (WMI)" : "DDC/CI", 35 + index % 6 * 10, true, index != 1, index != 1 ? 70 : null)
        { DeviceName = "fixture-display-" + index, DeviceInstanceId = "fixture-id-" + index, HdrModeKnown = true, GammaBrightness = 100 }).ToArray();

    internal static AppSettings UiFixtureSettings()
    {
        var settings = new AppSettings { Language = "ko", Theme = "light", UseAcrylic = false, UdpKey = "isolated-ui-fixture-key", ImportedUpstreamJson = "{}" };
        foreach (var monitor in UiFixtureMonitors())
        {
            settings.Monitors[monitor.Id] = new MonitorSettings { Name = monitor.Name, ShowContrast = true, Calibration = [new() { Input = 50, Output = 65 }], Features = new() { [0x12] = new() { Enabled = true, Name = "Contrast — Long custom display feature title" }, [0x62] = new() { Enabled = true, Name = "Volume / 스피커 음량" } } };
            settings.Sensor.Monitors[monitor.Id] = new SensorMonitorSettings { Enabled = true };
        }
        settings.Sensor.Enabled = true; settings.Sensor.Provider = "fake";
        settings.Schedule = [new() { Id = "fixture-time", Time = "20:00", IndividualBrightness = new() { ["ui-fixture:0"] = 35 } }, new() { Id = "fixture-solar", Event = "sunset", OffsetMinutes = -30 }];
        settings.Hotkeys = [new() { Id = "fixture-hotkey", Actions = [new() { Type = "cycle", Values = [20, 50, 100] }, new() { Type = "offset", Target = "vcp", Vcp = 0x62, Value = 5 }] }];
        settings.Profiles = [new() { Id = "fixture-profile", Name = "작업 프로필 — A long descriptive name for focused creative work", Path = @"C:\Fixture Applications\Creative Suite\editor.exe", Brightness = new() { ["ui-fixture:0"] = 35, ["ui-fixture:1"] = 60 } }];
        return settings;
    }
}

internal static class UiVisualVerification
{
    internal static void RecordProgress(string directory, string phase, UiVerificationResult result)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "progress.json"), JsonSerializer.Serialize(new
        {
            UpdatedUtc = DateTime.UtcNow, Phase = phase, MatrixCases = result.Cases.Count,
            Checks = result.Checks.Count, CapturedPreviews = result.Previews.Count, Errors = result.Errors.Count
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static async Task WaitLoadedAsync(FrameworkElement root)
    {
        var watch = Stopwatch.StartNew();
        while ((!root.IsLoaded || root.XamlRoot is null) && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
        if (!root.IsLoaded || root.XamlRoot is null) throw new InvalidOperationException("The test window did not load within five seconds.");
        await SettleAsync(root);
    }

    internal static async Task SettleAsync(FrameworkElement root)
    {
        root.UpdateLayout(); await Task.Delay(20);
        root.UpdateLayout(); await Task.Delay(20);
        root.UpdateLayout();
    }

    internal static IEnumerable<FrameworkElement> AuthoredElements(FrameworkElement root, bool includeCollapsed = false)
    {
        if (!includeCollapsed && root.Visibility == Visibility.Collapsed) yield break;
        yield return root;
        IEnumerable<UIElement> children = root switch
        {
            Panel panel => panel.Children,
            Border border when border.Child is not null => [border.Child],
            TextBox text when text.Header is UIElement header => [header],
            NumberBox number when number.Header is UIElement header => [header],
            ComboBox choice when choice.Header is UIElement header => [header],
            TimePicker time when time.Header is UIElement header => [header],
            Expander expander => (expander.Header is UIElement header ? new[] { header } : []).Concat(expander.Content is UIElement content && (includeCollapsed || expander.IsExpanded) ? [content] : []),
            ContentControl control when control.Content is UIElement content => [content],
            _ => []
        };
        foreach (var child in children.OfType<FrameworkElement>())
            foreach (var descendant in AuthoredElements(child, includeCollapsed)) yield return descendant;
    }

    internal static bool RequiresVisibleBounds(FrameworkElement element) => element.Visibility == Visibility.Visible && (element switch
    {
        TextBlock text => !string.IsNullOrWhiteSpace(text.Text),
        InfoBar info => info.IsOpen,
        Panel panel => panel.Children.Any(child => child.Visibility == Visibility.Visible),
        _ => element is Control or Border
    });

    internal static string Describe(FrameworkElement element)
    {
        string id = AutomationProperties.GetAutomationId(element);
        if (id.Length == 0) id = element.Name;
        if (id.Length == 0 && element is TextBlock text) id = text.Text.Length > 64 ? text.Text[..64] : text.Text;
        return element.GetType().Name + (id.Length > 0 ? " [" + id + "]" : "");
    }

    internal static bool HasFocusWithin(FrameworkElement element)
    {
        if (element.XamlRoot is null) return false;
        for (var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(element.XamlRoot) as DependencyObject;
            focused is not null; focused = VisualTreeHelper.GetParent(focused))
            if (ReferenceEquals(focused, element)) return true;
        return false;
    }

    internal static UiPalette ObservePalette(FrameworkElement root, FrameworkElement content, string language, string theme)
    {
        var heading = AuthoredElements(content).OfType<TextBlock>().FirstOrDefault(text => !string.IsNullOrWhiteSpace(text.Text));
        var foreground = heading?.Foreground as SolidColorBrush;
        var background = root is Panel panel ? panel.Background as SolidColorBrush : null;
        background ??= (root.FindName("SolidBackground") as Border)?.Background as SolidColorBrush;
        double? ratio = null;
        if (foreground is not null && background is not null && background.Color.A == 255)
        {
            double Channel(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
            double alpha = foreground.Color.A / 255d * foreground.Opacity * (heading?.Opacity ?? 1);
            double Component(byte front, byte back) => Channel((front * alpha + back * (1 - alpha)) / 255d);
            double text = .2126 * Component(foreground.Color.R, background.Color.R) + .7152 * Component(foreground.Color.G, background.Color.G) + .0722 * Component(foreground.Color.B, background.Color.B);
            double fill = .2126 * Channel(background.Color.R / 255d) + .7152 * Channel(background.Color.G / 255d) + .0722 * Channel(background.Color.B / 255d);
            ratio = (Math.Max(text, fill) + .05) / (Math.Min(text, fill) + .05);
        }
        return new(language, theme, foreground?.Color.ToString() ?? "non-solid/unavailable", background?.Color.ToString() ?? "non-solid/unavailable", ratio,
            "Observed heading/window colors only; no Windows high-contrast mode or full accessibility conformance test.");
    }

    internal static async Task<UiPreview> CaptureAsync(FrameworkElement root, string directory, string surface, double scale)
    {
        await Task.Delay(200); // Let short template transitions finish before capturing the rendered frame.
        await SettleAsync(root);
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root, (int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale));
        var buffer = await bitmap.GetPixelsAsync();
        byte[] pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || pixels.Length == 0)
            throw new InvalidOperationException("RenderTargetBitmap returned an empty preview for " + surface);
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        var file = await folder.CreateFileAsync(surface + "-" + ((int)Math.Round(scale * 100)).ToString() + "pct.png", CreationCollisionOption.ReplaceExisting);
        using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, pixels);
            await encoder.FlushAsync();
        }
        return new(surface, file.Path, scale, bitmap.PixelWidth, bitmap.PixelHeight, root.XamlRoot?.RasterizationScale ?? 0,
            root.ActualWidth, root.ActualHeight, bitmap.PixelWidth / root.ActualWidth, bitmap.PixelHeight / root.ActualHeight);
    }
}
