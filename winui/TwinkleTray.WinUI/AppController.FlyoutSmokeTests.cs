using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TwinkleTray.Core;
using TwinkleTray.WinUI.Services;
using Windows.Graphics;

namespace TwinkleTray.WinUI;

internal sealed partial class AppController
{
    private bool _interactiveSmokeStarted;
    private async Task VerifyFlyoutLifecycleAsync(UiVerificationResult result, string previews)
    {
        if (!(IsSmokeTest && IsDemo)) throw new InvalidOperationException("Flyout lifecycle checks require isolated demo smoke-test mode.");
        UiVisualVerification.RecordProgress(previews, "Starting flyout focus and lifecycle checks", result);

        var tracker = new TrayPointerGestureTracker();
        tracker.Press(true);
        uint firstGesture = tracker.CurrentPress;
        bool pressTracked = firstGesture != 0 && tracker.HasPendingGesture;
        tracker.Release(true);
        bool releaseRetained = tracker.CurrentPress == 0 && tracker.CurrentGesture == firstGesture && tracker.HasPendingGesture;
        uint consumed = tracker.ConsumeActivation();
        result.Checks.Add(new("Tray gesture survives release until activation and is consumed once", pressTracked && releaseRetained && consumed == firstGesture && tracker.ConsumeActivation() == 0 && !tracker.HasPendingGesture,
            $"Press={firstGesture}; retained after release={releaseRetained}; consumed={consumed}. Pure input-state test; no native mouse injection."));

        tracker.Press(true); tracker.Release(false);
        bool outsideReleaseCleared = !tracker.HasPendingGesture && tracker.CurrentGesture == 0 && tracker.ConsumeActivation() == 0;
        tracker.Press(true); tracker.Release(true); tracker.Press(false);
        result.Checks.Add(new("Outside release and a new outside press clear abandoned tray gestures", outsideReleaseCleared && !tracker.HasPendingGesture && tracker.ConsumeActivation() == 0,
            $"Outside release cleared={outsideReleaseCleared}; new outside press pending={tracker.HasPendingGesture}. Pure input-state test."));

        tracker.Press(true); uint abandoned = tracker.CurrentPress; tracker.Release(true);
        tracker.Press(true); uint replacement = tracker.CurrentPress; tracker.Release(true);
        result.Checks.Add(new("A new icon press replaces the previous gesture", replacement != 0 && replacement != abandoned && tracker.ConsumeActivation() == replacement && !tracker.HasPendingGesture,
            $"Old token={abandoned}; replacement={replacement}. Pure input-state test."));

        Window? probe = null, interactiveStart = null;
        MenuFlyout? moreFlyout = null;
        var root = (FrameworkElement)_window.Content;
        _window.SetVerificationHoldOpen(false);
        try
        {
            interactiveStart = await StartInteractiveFocusVerificationAsync();
            await _window.FlushForVerificationAsync();
            _window.HidePanel();
            Settings = new AppSettings { Language = "ko", Theme = "light", UseAcrylic = false, WindowsStyle = "win11", CheckScheduleAtStartup = false };
            _monitors = SettingsWindow.UiFixtureMonitors(2).Select((monitor, index) => monitor with
            {
                Id = "ui-flyout:" + index, Name = "Flyout verification display " + (index + 1), Brightness = 55,
                SupportsBrightness = index != 0, SupportsContrast = false, GammaBrightness = null, HdrActive = false, SdrBrightness = null,
            }).ToArray();
            _features.Clear();
            _window.ApplySettings();
            if (root.FindName("ErrorBar") is InfoBar error) error.IsOpen = false;

            var currentArea = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Nearest);
            var anchor = new RectInt32(currentArea.WorkArea.X + currentArea.WorkArea.Width / 2, currentArea.WorkArea.Y + currentArea.WorkArea.Height / 2, 24, 24);
            var keyboard = new TrayActivation(anchor, true, 0);
            var pointer = new TrayActivation(anchor, false, 0);
            _window.ShowPanel(keyboard);
            await UiVisualVerification.WaitLoadedAsync(root);
            await _window.WaitForPreparationForVerificationAsync();

            nint appearanceHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var revealed = _window.RevealedBounds;
            var targetBounds = _window.RestingBounds;
            FlyoutTestDwmGetWindowAttribute(appearanceHwnd, 14, out int cloaked, sizeof(int));
            var motionBounds = new List<RectInt32>();
            var motionScales = new List<double>();
            bool allMotionClipped = true;
            long motionDeadline = Environment.TickCount64 + 1500;
            do
            {
                FlyoutTestGetWindowRect(appearanceHwnd, out var actual);
                motionBounds.Add(new RectInt32(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top));
                motionScales.Add(root.XamlRoot.RasterizationScale);
                if (_window.EntranceRunning)
                {
                    int kind = FlyoutTestGetWindowRgnBox(appearanceHwnd, out var clip);
                    allMotionClipped &= kind == 1 || (kind >= 2 && clip.Left >= 0 && clip.Top >= 0 &&
                        actual.Left + clip.Right <= currentArea.WorkArea.X + currentArea.WorkArea.Width &&
                        actual.Top + clip.Bottom <= currentArea.WorkArea.Y + currentArea.WorkArea.Height);
                }
                await Task.Delay(16);
            } while (_window.EntranceRunning && Environment.TickCount64 < motionDeadline);
            await _window.WaitForPresentationForVerificationAsync(); await UiVisualVerification.SettleAsync(root);
            var settledBounds = new RectInt32(_window.AppWindow.Position.X, _window.AppWindow.Position.Y, _window.AppWindow.Size.Width, _window.AppWindow.Size.Height);
            bool constantSize = motionBounds.All(bounds => bounds.Width == targetBounds.Width && bounds.Height == targetBounds.Height && bounds.Y == targetBounds.Y);
            bool movesLeft = motionBounds.Zip(motionBounds.Skip(1)).All(pair => pair.First.X >= pair.Second.X);
            int gap = (int)Math.Round(12 * root.XamlRoot.RasterizationScale);
            bool bottomRight = settledBounds.X == currentArea.WorkArea.X + currentArea.WorkArea.Width - settledBounds.Width - gap &&
                settledBounds.Y == currentArea.WorkArea.Y + currentArea.WorkArea.Height - settledBounds.Height - gap;
            result.Checks.Add(new("The banner settles bottom-right without stretching or changing its content scale", _window.LastCloakSucceeded && _window.LastPresentationSettled && cloaked == 0 && targetBounds.Equals(settledBounds) && constantSize && motionScales.Distinct().Count() == 1 && bottomRight,
                $"Cloak={_window.LastCloakSucceeded}/{cloaked}; layout settled={_window.LastPresentationSettled}; target={targetBounds}; revealed={revealed}; settled={settledBounds}; samples={motionBounds.Count}; constant size={constantSize}; scales={string.Join(",", motionScales.Distinct())}; bottom-right={bottomRight}. Native window geometry; not a visual smoothness claim."));
            var systemUi = new Windows.UI.ViewManagement.UISettings();
            bool expectedAnimation = systemUi.AnimationsEnabled && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            bool slideObserved = motionBounds.Count > 1 && motionBounds[0].X > settledBounds.X && movesLeft;
            int regionAfter = FlyoutTestGetWindowRgnBox(appearanceHwnd, out _);
            result.Checks.Add(new("The whole native banner slides from the right and releases its temporary clip", _window.LastEntranceAnimated == expectedAnimation && !_window.EntranceRunning && (!expectedAnimation || slideObserved) && allMotionClipped && !_window.BannerClipActive && regionAfter == 0,
                $"System animations={systemUi.AnimationsEnabled}; animated={_window.LastEntranceAnimated}; native slide observed={slideObserved}; monotonic={movesLeft}; native region stayed inside work area={allMotionClipped}; clip active={_window.BannerClipActive}; final region result={regionAfter}. Actual HWND positions include the backdrop; Windows preferences were not changed."));
            var presenter = (OverlappedPresenter)_window.AppWindow.Presenter;
            long nativeStyle = FlyoutTestGetWindowLongPtr(appearanceHwnd, -16).ToInt64();
            bool hasPopupStyle = !presenter.HasBorder && !presenter.HasTitleBar && (nativeStyle & 0x80000000L) != 0 && (nativeStyle & 0x00CF0000L) == 0;
            var systemBackground = systemUi.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            var expectedSystemTheme = 5 * systemBackground.G + 2 * systemBackground.R + systemBackground.B >= 8 * 128 ? ElementTheme.Light : ElementTheme.Dark;
            Settings.Theme = "dark"; _window.RefreshAppearanceForVerification();
            await _window.WaitForPresentationForVerificationAsync(); await UiVisualVerification.SettleAsync(root);
            bool darkWorks = root.ActualTheme == ElementTheme.Dark;
            FlyoutTestDwmGetWindowAttribute(appearanceHwnd, 20, out int darkFrame, sizeof(int));
            Settings.Theme = "light"; _window.RefreshAppearanceForVerification();
            await _window.WaitForPresentationForVerificationAsync(); await UiVisualVerification.SettleAsync(root);
            bool lightWorks = root.ActualTheme == ElementTheme.Light;
            FlyoutTestDwmGetWindowAttribute(appearanceHwnd, 20, out int lightFrame, sizeof(int));
            Settings.Theme = "system"; _window.RefreshAppearanceForVerification();
            await _window.WaitForPresentationForVerificationAsync(); await UiVisualVerification.SettleAsync(root);
            bool systemWorks = root.ActualTheme == expectedSystemTheme;
            var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
            bool accentMatches = accent.Equals(systemUi.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent));
            result.Checks.Add(new("A borderless native popup follows light, dark and system theme with the Windows accent", hasPopupStyle && darkWorks && darkFrame == 1 && lightWorks && lightFrame == 0 && systemWorks && accentMatches,
                $"Popup without document chrome={hasPopupStyle}; native style=0x{nativeStyle:X}; dark content/DWM={darkWorks}/{darkFrame}; light={lightWorks}/{lightFrame}; system={systemWorks}/{expectedSystemTheme}; accent={accentMatches}. App overrides exercised; OS theme/accent not changed."));
            Settings.UseAcrylic = true; _window.RefreshAppearanceForVerification();
            bool acrylicExpected = Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported() && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            result.Checks.Add(new("The tray uses desktop Acrylic with a solid fallback", acrylicExpected ? _window.SystemBackdrop is DesktopAcrylicBackdrop && ((UIElement)root.FindName("SolidBackground")).Visibility == Visibility.Collapsed : _window.SystemBackdrop is null,
                $"Acrylic supported/expected={acrylicExpected}; backdrop={_window.SystemBackdrop?.GetType().Name}; OS controls final transparency. XAML bitmap previews exclude the compositor backdrop and native frame."));
            Settings.Theme = "light"; Settings.UseAcrylic = false; _window.RefreshAppearanceForVerification();
            _window.HidePanel(); _window.ShowPanel(keyboard); _window.HidePanel();
            await _window.WaitForPresentationForVerificationAsync();
            result.Checks.Add(new("Dismissing during preparation cannot reveal a stale flyout", !_window.IsShown && !_window.EntranceRunning,
                "Show then immediate Hide uses the real asynchronous presentation path; cancelled request must remain hidden."));
            _window.ShowPanel(keyboard);
            await _window.WaitForPreparationForVerificationAsync();
            _window.HidePanel();
            await Task.Delay(350);
            result.Checks.Add(new("Dismissing a moving banner cancels motion and removes its clipping region", !_window.IsShown && !_window.EntranceRunning && !_window.BannerClipActive && FlyoutTestGetWindowRgnBox(appearanceHwnd, out _) == 0,
                "Hide during the real entrance, then wait beyond its duration; no stale timer may reveal or move the banner."));
            _window.ShowPanel(keyboard);
            await _window.WaitForPresentationForVerificationAsync();

            T Element<T>(string id) where T : FrameworkElement => UiVisualVerification.AuthoredElements(root).OfType<T>()
                .Single(element => AutomationProperties.GetAutomationId(element) == id);
            bool HasFocus(DependencyObject target)
            {
                for (var focused = FocusManager.GetFocusedElement(root.XamlRoot) as DependencyObject; focused is not null; focused = VisualTreeHelper.GetParent(focused))
                    if (ReferenceEquals(focused, target)) return true;
                return false;
            }
            async Task<bool> WaitUntilAsync(Func<bool> condition, int milliseconds = 2000)
            {
                long deadline = Environment.TickCount64 + milliseconds;
                while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(20);
                return condition();
            }

            var disabledSlider = Element<Slider>("brightness:ui-flyout:0");
            var enabledSlider = Element<Slider>("brightness:ui-flyout:1");
            bool initialFocus = await WaitUntilAsync(() => HasFocus(enabledSlider));
            result.Checks.Add(new("Keyboard activation focuses the first enabled brightness control", initialFocus && !disabledSlider.IsEnabled && enabledSlider.IsEnabled,
                $"Focused enabled slider={initialFocus}; first slider enabled={disabledSlider.IsEnabled}; second enabled={enabledSlider.IsEnabled}. Actual XAML focus."));

            // Match the production tray callback: ShowPanel queues focus, then
            // RefreshAsync synchronously disables controls before its hardware await.
            _window.HidePanel(); _window.ShowPanel(keyboard); _window.SetRefreshing(true);
            await Task.Delay(100);
            bool disabledDuringRefresh = !enabledSlider.IsEnabled;
            bool brightnessListRetainsOpacity = ((UIElement)root.FindName("MonitorList")).Opacity == 1;
            _window.SetRefreshing(false);
            bool refreshFocusRestored = await WaitUntilAsync(() => HasFocus(enabledSlider));
            result.Checks.Add(new("Opening focus waits for an immediate refresh and restores the enabled brightness control", disabledDuringRefresh && refreshFocusRestored && enabledSlider.IsEnabled,
                $"Disabled during refresh={disabledDuringRefresh}; brightness focus restored={refreshFocusRestored}; enabled after refresh={enabledSlider.IsEnabled}. Production ShowPanel + synchronous SetRefreshing sequence; demo controls only."));
            result.Checks.Add(new("Display refresh preserves list opacity instead of flashing the whole panel", brightnessListRetainsOpacity && ((UIElement)root.FindName("MonitorList")).Opacity == 1,
                "Native controls retain their own disabled states; only the dedicated progress indicator changes visibility through opacity."));

            var editor = Element<TextBox>("brightness:ui-flyout:1:value");
            bool editorFocused = editor.Focus(FocusState.Keyboard);
            await _window.WaitForPresentationForVerificationAsync(); await UiVisualVerification.SettleAsync(root);
            _window.HidePanel(); _window.ShowPanel(keyboard);
            bool restoredFocus = await WaitUntilAsync(() => HasFocus(editor));
            result.Checks.Add(new("Reopening the flyout restores its last enabled control", editorFocused && restoredFocus,
                $"Initial editor focus={editorFocused}; editor restored={restoredFocus}. Actual XAML focus."));

            var probeContent = new Button { Content = "Isolated flyout focus probe", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            probe = new Window { Title = "Twinkle Tray Native demo focus verification", Content = probeContent };
            probe.AppWindow.IsShownInSwitchers = false;
            probe.AppWindow.Resize(new SizeInt32(300, 120));
            nint probeHwnd = WinRT.Interop.WindowNative.GetWindowHandle(probe);
            nint panelHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            _window.ShowPanel(keyboard);
            await _window.WaitForPresentationForVerificationAsync(); await UiVisualVerification.SettleAsync(root); // Finish the queued opening-focus restoration before transferring activation.
            bool panelWasForeground = await WaitUntilAsync(() => _window.IsShown && FlyoutTestGetForegroundWindow() == panelHwnd);
            bool probeActivated = false, probeForegroundObserved = false, panelDeactivated = false;
            var activationTrace = new List<string>();
            var activationWatch = Stopwatch.StartNew();
            nint previousForeground = (nint)(-1);
            bool? previousShown = null;
            void ObserveActivation(string source, WindowActivationState? state = null)
            {
                nint foreground = FlyoutTestGetForegroundWindow();
                probeForegroundObserved |= foreground == probeHwnd;
                if (activationTrace.Count < 24 && (source != "poll" || foreground != previousForeground || _window.IsShown != previousShown))
                    activationTrace.Add($"{activationWatch.ElapsedMilliseconds}ms {source}{(state is null ? "" : ":" + state)} fg=0x{foreground.ToInt64():X} shown={_window.IsShown}");
                previousForeground = foreground;
                previousShown = _window.IsShown;
            }
            void ProbeActivated(object sender, WindowActivatedEventArgs args)
            {
                probeActivated |= args.WindowActivationState != WindowActivationState.Deactivated;
                ObserveActivation("probe", args.WindowActivationState);
            }
            void PanelActivated(object sender, WindowActivatedEventArgs args)
            {
                panelDeactivated |= args.WindowActivationState == WindowActivationState.Deactivated;
                ObserveActivation("panel", args.WindowActivationState);
            }
            bool foregroundRequestAccepted, lightDismissed;
            probe.Activated += ProbeActivated;
            _window.Activated += PanelActivated;
            try
            {
                ObserveActivation("before probe request");
                probe.AppWindow.Show(false);
                ObserveActivation("after nonactivating Show");
                probe.Activate();
                ObserveActivation("after Window.Activate before foreground request");
                foregroundRequestAccepted = FlyoutTestSetForegroundWindow(probeHwnd);
                ObserveActivation("after explicit foreground request");
                lightDismissed = await WaitUntilAsync(() =>
                {
                    ObserveActivation("poll");
                    return panelWasForeground && probeActivated && probeForegroundObserved && panelDeactivated &&
                        !_window.IsShown && FlyoutTestGetForegroundWindow() == probeHwnd;
                });
            }
            finally
            {
                probe.Activated -= ProbeActivated;
                _window.Activated -= PanelActivated;
            }
            result.Checks.Add(new("Activating another native window dismisses the demo flyout", lightDismissed,
                $"Panel HWND=0x{panelHwnd.ToInt64():X}; probe HWND=0x{probeHwnd.ToInt64():X}; final foreground=0x{FlyoutTestGetForegroundWindow().ToInt64():X}; panel initially foreground={panelWasForeground}; probe Activated observed={probeActivated}; panel Deactivated observed={panelDeactivated}; probe foreground observed={probeForegroundObserved}; SetForegroundWindow returned={foregroundRequestAccepted}; panel shown={_window.IsShown}; interactive test-start window={_interactiveSmokeStarted}. Trace: {string.Join(" | ", activationTrace)}. Real own-window activation/foreground APIs; no simulated deactivation. Optional test-start input precedes the checks; dismissal itself uses the native probe, without external mouse input."));
            bool dismissedBeforeReopen = !_window.IsShown;
            var reopenWatch = Stopwatch.StartNew();
            _window.TogglePanel(pointer);
            bool immediateReopen = _window.IsShown;
            result.Checks.Add(new("An unrelated dismissal does not block the next tray activation", panelDeactivated && dismissedBeforeReopen && immediateReopen,
                $"Native panel Deactivated observed={panelDeactivated}; hidden before toggle={dismissedBeforeReopen}; shown synchronously after toggle={immediateReopen}; call elapsed={reopenWatch.Elapsed.TotalMilliseconds:0.##}ms. Reopen behavior is checked independently of the probe's final foreground result above. Activation request supplied by harness; no native tray click injection."));
            await _window.WaitForPresentationForVerificationAsync(); await UiVisualVerification.SettleAsync(root);
            probe.Close(); probe = null;

            tracker.Press(true); tracker.Release(true);
            uint delayedDismissGesture = tracker.CurrentGesture;
            _window.DismissForVerification(delayedDismissGesture);
            _window.TogglePanel(pointer with { PointerGesture = tracker.ConsumeActivation() });
            bool sameGestureStayedClosed = !_window.IsShown;
            tracker.Press(true); tracker.Release(true);
            _window.TogglePanel(pointer with { PointerGesture = tracker.ConsumeActivation() });
            result.Checks.Add(new("Release before deferred dismissal suppresses only that same tray gesture", delayedDismissGesture != 0 && sameGestureStayedClosed && _window.IsShown,
                $"Released token={delayedDismissGesture}; same gesture stayed closed={sameGestureStayedClosed}; next gesture reopened={_window.IsShown}. Simulated request sequence through production methods."));
            await _window.WaitForPresentationForVerificationAsync(); await UiVisualVerification.SettleAsync(root);

            _window.DismissForVerification(701);
            _window.TogglePanel(keyboard with { PointerGesture = 701 });
            result.Checks.Add(new("Keyboard activation ignores an old pointer suppression token", _window.IsShown,
                "Matching numeric token supplied deliberately with Keyboard=true; production TogglePanel must open."));
            _window.DismissForVerification(702);
            _window.ShowPanel(pointer with { PointerGesture = 702 });
            result.Checks.Add(new("An explicit panel request opens despite a previous dismissal token", _window.IsShown,
                "Production ShowPanel used, matching the tray menu command rather than toggle semantics."));
            await _window.WaitForPresentationForVerificationAsync(); await UiVisualVerification.SettleAsync(root);

            var moreButton = (Button)root.FindName("MoreButton");
            moreFlyout = moreButton.Flyout as MenuFlyout ?? throw new InvalidOperationException("The tray More button has no menu flyout.");
            bool menuOpened = false, menuClosed = false;
            moreFlyout.Opened += (_, _) => menuOpened = true;
            moreFlyout.Closed += (_, _) => menuClosed = true;
            moreFlyout.ShowAt(moreButton);
            bool opened = await WaitUntilAsync(() => menuOpened);
            var refreshItem = moreFlyout.Items.OfType<MenuFlyoutItem>().Single(item => item.Name == "RefreshMoreMenuItem");
            bool menuFocused = refreshItem.Focus(FocusState.Keyboard);
            await Task.Delay(200); // Exercise more than one owned-popup focus-watch interval.
            result.Checks.Add(new("The More popup can receive keyboard focus without dismissing its parent", opened && menuFocused && refreshItem.FocusState != FocusState.Unfocused && _window.IsShown && !menuClosed,
                $"Opened={opened}; menu item Focus returned={menuFocused}; observed focus={refreshItem.FocusState}; closed event={menuClosed}; panel shown={_window.IsShown}. Actual MenuFlyout.ShowAt and Focus; no menu command invoked."));
            moreFlyout.Hide();
            bool closed = await WaitUntilAsync(() => menuClosed);
            await _window.WaitForPresentationForVerificationAsync(); await UiVisualVerification.SettleAsync(root);
            result.Checks.Add(new("Closing an internal popup keeps the brightness panel open", closed && _window.IsShown,
                $"Menu Closed observed={closed}; parent shown={_window.IsShown}. Programmatic popup dismissal."));

            editor = Element<TextBox>("brightness:ui-flyout:1:value");
            bool draftFocused = editor.Focus(FocusState.Keyboard);
            await _window.WaitForPresentationForVerificationAsync(); await UiVisualVerification.SettleAsync(root);
            double brightnessBefore = LogicalBrightness(_monitors[1]);
            string acceptedText = editor.Text;
            editor.Text = "17";
            _window.DismissFromKeyboardForVerification();
            bool escapeHid = !_window.IsShown;
            await _window.FlushForVerificationAsync();
            await _window.WaitForPresentationForVerificationAsync(); await UiVisualVerification.SettleAsync(root);
            bool draftCancelled = editor.Text == acceptedText && Math.Abs(LogicalBrightness(_monitors[1]) - brightnessBefore) < .01;
            result.Checks.Add(new("Keyboard dismissal hides the panel and cancels an uncommitted numeric draft", draftFocused && escapeHid && draftCancelled,
                $"Focused={draftFocused}; hidden immediately={escapeHid}; input restored={editor.Text == acceptedText}; brightness={brightnessBefore}→{LogicalBrightness(_monitors[1])}. Production keyboard-dismiss method invoked; physical Escape not injected."));

            FlyoutTestGetCursorPos(out var cursor);
            var cursorArea = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
            var displays = DisplayArea.FindAll();
            // This WinRT vector supports indexed access, but some Windows App SDK
            // projections cannot cast its IEnumerable implementation for LINQ.
            var targetArea = cursorArea;
            for (int index = 0; index < displays.Count; index++)
            {
                var candidate = displays[index];
                if (candidate.DisplayId == cursorArea.DisplayId) continue;
                targetArea = candidate;
                break;
            }
            var targetWork = targetArea.WorkArea;
            var targetAnchor = new RectInt32(targetWork.X + targetWork.Width / 2, targetWork.Y + targetWork.Height / 2, 24, 24);
            _window.ShowPanel(new TrayActivation(targetAnchor, true, 0));
            await _window.WaitForPresentationForVerificationAsync(); await UiVisualVerification.SettleAsync(root);
            _window.RecalculateLayoutForVerification();
            await _window.WaitForPresentationForVerificationAsync(); await UiVisualVerification.SettleAsync(root);
            var observedArea = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Nearest);
            var position = _window.AppWindow.Position;
            var size = _window.AppWindow.Size;
            bool contained = position.X >= targetWork.X && position.Y >= targetWork.Y &&
                (long)position.X + size.Width <= (long)targetWork.X + targetWork.Width && (long)position.Y + size.Height <= (long)targetWork.Y + targetWork.Height;
            result.Checks.Add(new("Keyboard placement follows the supplied icon anchor and stays inside that display", _window.IsShown && observedArea.DisplayId == targetArea.DisplayId && contained && size.Width > 0 && size.Height > 0,
                $"Available displays={displays.Count}; anchor display differs from cursor display={targetArea.DisplayId != cursorArea.DisplayId}; observed target match={observedArea.DisplayId == targetArea.DisplayId}; work-area containment={contained}; actual pixels={size.Width}×{size.Height}. Synthetic anchor, actual native placement; cursor was not moved."));
            result.Checks.Add(new("The transient panel cycles keyboard navigation and stays out of window switchers", root.TabFocusNavigation == KeyboardNavigationMode.Cycle && !_window.AppWindow.IsShownInSwitchers,
                $"Root navigation={root.TabFocusNavigation}; shown in switchers={_window.AppWindow.IsShownInSwitchers}. Configuration check; physical Tab/Alt+Tab sequence not injected."));
        }
        finally
        {
            moreFlyout?.Hide();
            _window.SetVerificationHoldOpen(true);
            _window.HidePanel();
            probe?.Close();
            interactiveStart?.Close();
            UiVisualVerification.RecordProgress(previews, "Flyout focus and lifecycle checks complete", result);
        }
    }

    private async Task<Window?> StartInteractiveFocusVerificationAsync()
    {
        // Opt-in for a shared desktop where Windows correctly denies background
        // focus stealing. A real click starts the suite; never bypass foreground
        // protection or treat logical XAML activation as native foreground focus.
        if (!(IsSmokeTest && IsDemo) || Environment.GetEnvironmentVariable("TWINKLETRAY_SMOKE_INTERACTIVE") != "1") return null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new Button { Content = "Start focus checks", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var content = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)), RequestedTheme = ElementTheme.Dark };
        content.Children.Add(start);
        var window = new Window { Title = "Twinkle Tray Native — focus test ready", Content = content };
        window.AppWindow.Resize(new SizeInt32(420, 160));
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        start.Click += (_, _) =>
        {
            if (FlyoutTestGetForegroundWindow() != hwnd) return;
            start.IsEnabled = false;
            start.Content = "Verifying simulated displays…";
            _interactiveSmokeStarted = true;
            ready.TrySetResult();
        };
        window.Closed += (_, _) => ready.TrySetException(new InvalidOperationException("Interactive focus verification was closed before it started."));
        window.Activate();
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(60)); return window; }
        catch { window.Close(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FlyoutTestPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct FlyoutTestRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool FlyoutTestGetWindowRect(nint window, out FlyoutTestRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowRgnBox")] private static extern int FlyoutTestGetWindowRgnBox(nint window, out FlyoutTestRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint FlyoutTestGetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")] private static extern bool FlyoutTestGetCursorPos(out FlyoutTestPoint point);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint FlyoutTestGetForegroundWindow();
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int FlyoutTestDwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] private static extern bool FlyoutTestSetForegroundWindow(nint hwnd);
}
