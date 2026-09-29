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

        Window? probe = null;
        MenuFlyout? moreFlyout = null;
        var root = (FrameworkElement)_window.Content;
        _window.SetVerificationHoldOpen(false);
        try
        {
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
            _window.SetRefreshing(false);
            bool refreshFocusRestored = await WaitUntilAsync(() => HasFocus(enabledSlider));
            result.Checks.Add(new("Opening focus waits for an immediate refresh and restores the enabled brightness control", disabledDuringRefresh && refreshFocusRestored && enabledSlider.IsEnabled,
                $"Disabled during refresh={disabledDuringRefresh}; brightness focus restored={refreshFocusRestored}; enabled after refresh={enabledSlider.IsEnabled}. Production ShowPanel + synchronous SetRefreshing sequence; demo controls only."));

            var editor = Element<TextBox>("brightness:ui-flyout:1:value");
            bool editorFocused = editor.Focus(FocusState.Keyboard);
            await UiVisualVerification.SettleAsync(root);
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
            await UiVisualVerification.SettleAsync(root); // Finish the queued opening-focus restoration before transferring activation.
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
                $"Panel HWND=0x{panelHwnd.ToInt64():X}; probe HWND=0x{probeHwnd.ToInt64():X}; final foreground=0x{FlyoutTestGetForegroundWindow().ToInt64():X}; panel initially foreground={panelWasForeground}; probe Activated observed={probeActivated}; panel Deactivated observed={panelDeactivated}; probe foreground observed={probeForegroundObserved}; SetForegroundWindow returned={foregroundRequestAccepted}; panel shown={_window.IsShown}. Trace: {string.Join(" | ", activationTrace)}. Real own-window activation/foreground APIs; no simulated deactivation or external mouse input."));
            bool dismissedBeforeReopen = !_window.IsShown;
            var reopenWatch = Stopwatch.StartNew();
            _window.TogglePanel(pointer);
            bool immediateReopen = _window.IsShown;
            result.Checks.Add(new("An unrelated dismissal does not block the next tray activation", panelDeactivated && dismissedBeforeReopen && immediateReopen,
                $"Native panel Deactivated observed={panelDeactivated}; hidden before toggle={dismissedBeforeReopen}; shown synchronously after toggle={immediateReopen}; call elapsed={reopenWatch.Elapsed.TotalMilliseconds:0.##}ms. Reopen behavior is checked independently of the probe's final foreground result above. Activation request supplied by harness; no native tray click injection."));
            await UiVisualVerification.SettleAsync(root);
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
            await UiVisualVerification.SettleAsync(root);

            _window.DismissForVerification(701);
            _window.TogglePanel(keyboard with { PointerGesture = 701 });
            result.Checks.Add(new("Keyboard activation ignores an old pointer suppression token", _window.IsShown,
                "Matching numeric token supplied deliberately with Keyboard=true; production TogglePanel must open."));
            _window.DismissForVerification(702);
            _window.ShowPanel(pointer with { PointerGesture = 702 });
            result.Checks.Add(new("An explicit panel request opens despite a previous dismissal token", _window.IsShown,
                "Production ShowPanel used, matching the tray menu command rather than toggle semantics."));
            await UiVisualVerification.SettleAsync(root);

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
            await UiVisualVerification.SettleAsync(root);
            result.Checks.Add(new("Closing an internal popup keeps the brightness panel open", closed && _window.IsShown,
                $"Menu Closed observed={closed}; parent shown={_window.IsShown}. Programmatic popup dismissal."));

            editor = Element<TextBox>("brightness:ui-flyout:1:value");
            bool draftFocused = editor.Focus(FocusState.Keyboard);
            await UiVisualVerification.SettleAsync(root);
            double brightnessBefore = LogicalBrightness(_monitors[1]);
            string acceptedText = editor.Text;
            editor.Text = "17";
            _window.DismissFromKeyboardForVerification();
            bool escapeHid = !_window.IsShown;
            await _window.FlushForVerificationAsync();
            await UiVisualVerification.SettleAsync(root);
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
            await UiVisualVerification.SettleAsync(root);
            _window.RecalculateLayoutForVerification();
            await UiVisualVerification.SettleAsync(root);
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
            UiVisualVerification.RecordProgress(previews, "Flyout focus and lifecycle checks complete", result);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FlyoutTestPoint { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")] private static extern bool FlyoutTestGetCursorPos(out FlyoutTestPoint point);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint FlyoutTestGetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] private static extern bool FlyoutTestSetForegroundWindow(nint hwnd);
}
