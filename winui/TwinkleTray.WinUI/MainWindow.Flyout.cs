using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TwinkleTray.WinUI.Services;
using Windows.Graphics;

namespace TwinkleTray.WinUI;

public sealed partial class MainWindow
{
    private RectInt32 _anchorBounds;
    private uint _dismissedTrayGesture;
    private bool _verificationHoldOpen;
    private string? _lastFocusedControl;
    private readonly DispatcherTimer _popupFocusWatch = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private bool _watchInitialized;
    private bool _panelFocusPending, _pendingFocusKeyboard, _focusInputTrackingInitialized, _refreshFocusInterrupted;
    private uint _panelFocusRequest;

    private void Panel_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_closing || _preparingPresentation) return;
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _popupFocusWatch.Stop();
            return;
        }
        if (!IsShown || _verificationHoldOpen) return;
        if (PanelOwnsForeground())
        {
            // A native popup can temporarily own activation. Keep watching so an
            // outside click still dismisses the panel while that popup is open.
            if (!_watchInitialized)
            {
                _popupFocusWatch.Tick += (_, _) => CheckLightDismiss();
                _watchInitialized = true;
            }
            _popupFocusWatch.Start();
        }
        else CheckLightDismiss();
    }

    private bool PanelOwnsForeground()
    {
        nint foreground = GetForegroundWindow();
        return foreground == _hwnd || (foreground != 0 && GetAncestor(foreground, 3) == _hwnd);
    }

    private void CheckLightDismiss()
    {
        if (!IsShown || _verificationHoldOpen) { _popupFocusWatch.Stop(); return; }
        if (PanelOwnsForeground()) return;
        _dismissedTrayGesture = _controller.CurrentTrayPointerGesture;
        HidePanel();
    }

    public void ShowPanel()
    {
        _controller.TryGetTrayIconBounds(out var anchor);
        ShowPanel(new TrayActivation(anchor, false, 0));
    }

    internal void ShowPanel(TrayActivation activation)
    {
        bool alreadyVisible = IsShown && !_preparingPresentation;
        _dismissedTrayGesture = 0;
        _anchorBounds = activation.Anchor;
        IsShown = true;
        _controller.SetTrayPanelVisible(true);
        RestorePanelFocus(activation.Keyboard);
        if (alreadyVisible)
        {
            PositionPanel(useAnchor: true);
            Activate(); SetForegroundWindow(_hwnd);
        }
        else BeginPanelPresentation();
    }

    private void RestorePanelFocus(bool keyboard)
    {
        if (!_focusInputTrackingInitialized)
        {
            // A queued opening/refresh must not move focus after a new user action.
            Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => InterruptPanelFocus()), true);
            Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
            {
                if (args.Key != Windows.System.VirtualKey.F5) InterruptPanelFocus();
            }), true);
            _focusInputTrackingInitialized = true;
        }
        _panelFocusPending = true;
        _pendingFocusKeyboard = keyboard;
        _panelFocusRequest++;
        QueuePendingPanelFocus();
    }

    private void InterruptPanelFocus()
    {
        _panelFocusPending = false;
        _panelFocusRequest++;
        if (_refreshing) _refreshFocusInterrupted = true;
    }

    private void QueuePendingPanelFocus()
    {
        if (!_panelFocusPending) return;
        uint request = _panelFocusRequest;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_panelFocusPending || request != _panelFocusRequest || !IsShown || _closing || _refreshing || _preparingPresentation || EntranceRunning) return;
            Control? target = _lastFocusedControl is { } id && _focusTargets.TryGetValue(id, out var last) && last.IsEnabled && last.Visibility == Visibility.Visible ? last : null;
            target ??= _sliders.Values.FirstOrDefault(slider => slider.IsEnabled);
            target ??= SettingsButton;
            _panelFocusPending = false;
            target.Focus(_pendingFocusKeyboard ? FocusState.Keyboard : FocusState.Pointer);
        });
    }

    public void HidePanel()
    {
        if (Root.XamlRoot is not null && FocusManager.GetFocusedElement(Root.XamlRoot) is Control focused)
        {
            string id = AutomationProperties.GetAutomationId(focused);
            if (id.Length > 0) _lastFocusedControl = id;
        }
        _popupFocusWatch.Stop();
        _panelFocusPending = false;
        _panelFocusRequest++;
        IsShown = false;
        _controller.SetTrayPanelVisible(false);
        AppWindow.Hide();
        CancelPanelPresentation();
        SetPanelCloaked(false);
    }

    public void TogglePanel()
    {
        if (IsShown) HidePanel(); else ShowPanel();
    }

    internal void TogglePanel(TrayActivation activation)
    {
        bool samePointerGesture = !activation.Keyboard && activation.PointerGesture != 0 && activation.PointerGesture == _dismissedTrayGesture;
        _dismissedTrayGesture = 0;
        if (samePointerGesture) return;
        if (IsShown) HidePanel(); else ShowPanel(activation);
    }

    private void DismissFromKeyboard()
    {
        foreach (var (slider, editor) in _numberEditors)
            if (HasFocusWithin(editor)) editor.Text = FormatValue(slider.Value);
        HidePanel();
        _controller.ReturnFocusToTray();
    }

    internal void SetVerificationHoldOpen(bool value)
    {
        if (!_controller.IsSmokeTest) throw new InvalidOperationException("Only the isolated smoke harness may hold the panel open.");
        _verificationHoldOpen = value;
    }

    internal void DismissForVerification(uint pointerGesture)
    {
        if (!_controller.IsSmokeTest) throw new InvalidOperationException("Only the isolated smoke harness may simulate a tray dismissal.");
        _dismissedTrayGesture = pointerGesture;
        HidePanel();
    }

    internal void DismissFromKeyboardForVerification()
    {
        if (!_controller.IsSmokeTest) throw new InvalidOperationException("Only the isolated smoke harness may request a keyboard dismissal.");
        DismissFromKeyboard();
    }

    internal void RecalculateLayoutForVerification() => PositionPanel();

    private void PositionPanel(bool useAnchor = false)
    {
        if (_positioning || _closing || EntranceRunning) return;
        _positioning = true;
        try
        {
            var currentDisplay = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            bool hasAnchor = _anchorBounds.Width > 0 && _anchorBounds.Height > 0;
            var anchorPoint = new PointInt32(_anchorBounds.X + _anchorBounds.Width / 2, _anchorBounds.Y + _anchorBounds.Height / 2);
            var display = useAnchor && hasAnchor ? DisplayArea.GetFromPoint(anchorPoint, DisplayAreaFallback.Nearest) : currentDisplay;
            if (currentDisplay.DisplayId != display.DisplayId) AppWindow.Move(new PointInt32(display.WorkArea.X + 12, display.WorkArea.Y + 12));
            double scale = GetDpiForWindow(_hwnd) / 96d;
            if (scale < .5) scale = Root.XamlRoot?.RasterizationScale ?? 1;
            var work = display.WorkArea;
            int gap = Math.Max(1, (int)Math.Round(12 * scale));
            int width = Math.Min((int)Math.Round(360 * scale), Math.Max(1, work.Width - gap * 2));
            var outerSize = AppWindow.Size;
            var clientSize = AppWindow.ClientSize;
            int nonClientWidth = Math.Max(0, outerSize.Width - clientSize.Width);
            int nonClientHeight = Math.Max(0, outerSize.Height - clientSize.Height);
            double clientWidth = Math.Max(1, width - nonClientWidth) / scale;
            double innerWidth = Math.Max(1, clientWidth - BodyScroll.Margin.Left - BodyScroll.Margin.Right);
            ToolbarSurface.Measure(new Windows.Foundation.Size(clientWidth, double.PositiveInfinity));
            PanelBody.Measure(new Windows.Foundation.Size(innerWidth, double.PositiveInfinity));
            double chromeHeight = ToolbarSurface.DesiredSize.Height + BodyScroll.Margin.Top + BodyScroll.Margin.Bottom;
            double desiredHeight = chromeHeight + PanelBody.DesiredSize.Height;
            int height = Math.Min((int)Math.Ceiling(desiredHeight * scale) + nonClientHeight, Math.Max(1, work.Height - gap * 2));
            double clientHeight = Math.Max(0, height - nonClientHeight) / scale;
            IsContentScrollable = PanelBody.DesiredSize.Height > Math.Max(0, clientHeight - chromeHeight) + .5;
            LayoutMetrics = (desiredHeight, width, height, work.Width, work.Height, scale);

            int right = work.X + work.Width, bottom = work.Y + work.Height;
            bool banner = _controller.Settings.WindowsStyle != "win10";
            // The icon chooses the display. Windows 11's banner rests at that
            // display's bottom-right, including when the icon is in overflow.
            int x = !banner && hasAnchor ? _anchorBounds.X + _anchorBounds.Width - width : right - width - gap;
            int y = bottom - height - gap;
            if (!banner && hasAnchor)
            {
                if (_anchorBounds.Y + _anchorBounds.Height <= work.Y) y = work.Y + gap;
                else if (_anchorBounds.Y >= bottom) y = bottom - height - gap;
                else if (_anchorBounds.X + _anchorBounds.Width <= work.X)
                { x = work.X + gap; y = _anchorBounds.Y; }
                else if (_anchorBounds.X >= right)
                { x = right - width - gap; y = _anchorBounds.Y; }
                else
                {
                    // Overflow icons are inside the work area. Prefer above the
                    // actual icon and fall below it if there is insufficient room.
                    y = _anchorBounds.Y - height - gap;
                    if (y < work.Y + gap) y = _anchorBounds.Y + _anchorBounds.Height + gap;
                }
            }
            x = Math.Clamp(x, work.X + gap, Math.Max(work.X + gap, right - width - gap));
            y = Math.Clamp(y, work.Y + gap, Math.Max(work.Y + gap, bottom - height - gap));
            if (AppWindow.Position.X != x || AppWindow.Position.Y != y || AppWindow.Size.Width != width || AppWindow.Size.Height != height)
                AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        finally { _positioning = false; }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
}
