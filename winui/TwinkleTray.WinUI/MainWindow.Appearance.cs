using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace TwinkleTray.WinUI;

public sealed partial class MainWindow
{
    private readonly UISettings _systemUi = new();
    private bool _appearanceSubscribed, _preparingPresentation, _presentationHadActivation;
    private uint _presentationRequest;
    private Task _presentationTask = Task.CompletedTask;
    private readonly DispatcherTimer _entranceTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _entranceClock = new();
    private RectInt32 _entranceWorkArea;
    private uint _entranceRequest;
    private WindowSubclass? _animationSubclass;
    internal bool EntranceRunning { get; private set; }
    internal bool LastEntranceAnimated { get; private set; }
    internal bool LastPresentationSettled { get; private set; }
    internal bool LastCloakSucceeded { get; private set; }
    internal RectInt32 RevealedBounds { get; private set; }
    internal RectInt32 RestingBounds { get; private set; }
    internal bool BannerClipActive { get; private set; }

    private void InitializeSystemAppearance()
    {
        try { _systemUi.ColorValuesChanged += SystemColorsChanged; _appearanceSubscribed = true; }
        catch (System.Runtime.InteropServices.COMException) { }
        // Keep the HWND a popup even though WinUI exposes an OverlappedPresenter.
        long style = GetWindowLongPtr(_hwnd, -16).ToInt64();
        SetWindowLongPtr(_hwnd, -16, (nint)((style & ~0x00CF0000L) | 0x80000000L));
        MoveBannerWindow(_hwnd, 0, 0, 0, 0, 0, 0x37); // FRAMECHANGED, no move/resize/activation/z-order.
        _animationSubclass = AnimationWindowProcedure;
        if (!SetWindowSubclass(_hwnd, _animationSubclass, 1, 0)) _animationSubclass = null;
        _entranceTimer.Tick += (_, _) => AdvancePanelEntrance();
        // One whole-surface slide owns the motion; do not combine it with DWM's
        // generic document-window transition or a second content fade.
        int disabled = 1;
        DwmSetWindowAttribute(_hwnd, 3, ref disabled, sizeof(int));
        Closed += (_, _) =>
        {
            ++_presentationRequest;
            _entranceTimer.Stop();
            if (_animationSubclass is not null) RemoveWindowSubclass(_hwnd, _animationSubclass, 1);
            if (!_appearanceSubscribed) return;
            try { _systemUi.ColorValuesChanged -= SystemColorsChanged; }
            catch (System.Runtime.InteropServices.COMException) { }
            finally { _appearanceSubscribed = false; }
        };
    }

    private void SystemColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closing) ApplySystemAppearance();
    });

    private void ApplySystemAppearance()
    {
        if (_closing) return;
        // Theme brushes and the default WinUI controls continue to own the accent
        // palette. Do not replace SystemAccentColor with a fixed branding color.
        var background = _systemUi.GetColorValue(UIColorType.Background);
        var systemTheme = 5 * background.G + 2 * background.R + background.B >= 8 * 128
            ? ElementTheme.Light : ElementTheme.Dark;
        Root.RequestedTheme = _controller.Settings.Theme switch
        {
            "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => systemTheme,
        };
        ApplyNativeFrameTheme();
        ApplyBackdrop();
    }

    private void ApplyNativeFrameTheme()
    {
        if (_closing) return;
        int dark = Root.ActualTheme == ElementTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, 20, ref dark, sizeof(int));
        int systemBorder = -1;
        DwmSetWindowAttribute(_hwnd, 34, ref systemBorder, sizeof(int));
    }

    private bool SetPanelCloaked(bool value)
    {
        if (_closing) return false;
        int enabled = value ? 1 : 0;
        return DwmSetWindowAttribute(_hwnd, 13, ref enabled, sizeof(int)) >= 0;
    }

    private void BeginPanelPresentation()
    {
        CancelPanelPresentation();
        uint request = ++_presentationRequest;
        _preparingPresentation = true;
        LastPresentationSettled = false;
        LastCloakSucceeded = SetPanelCloaked(true);
        PositionPanel(useAnchor: true);
        AppWindow.Show(false);
        // Acquire foreground within the initiating input call. Deferring this
        // until after an await can lose Windows' foreground-activation permission.
        Activate(); SetForegroundWindow(_hwnd);
        _presentationHadActivation = GetForegroundWindow() == _hwnd;
        _presentationTask = PresentPanelAsync(request);
    }

    private async Task PresentPanelAsync(uint request)
    {
        try
        {
            RectInt32 previous = default;
            int stableFrames = 0;
            long deadline = Environment.TickCount64 + 250;
            do
            {
                await Task.Delay(16);
                if (request != _presentationRequest || !IsShown || _closing) return;
                if (Root.XamlRoot is null || !Root.IsLoaded) continue;
                Root.UpdateLayout();
                PositionPanel();
                var bounds = CurrentPanelBounds();
                double scale = Root.XamlRoot.RasterizationScale;
                bool arranged = Math.Abs(Root.ActualWidth * scale - AppWindow.ClientSize.Width) <= 2 &&
                    Math.Abs(Root.ActualHeight * scale - AppWindow.ClientSize.Height) <= 2;
                stableFrames = arranged && bounds.Equals(previous) ? stableFrames + 1 : 0;
                previous = bounds;
                if (stableFrames >= 2) { LastPresentationSettled = true; break; }
            } while (LastCloakSucceeded && Environment.TickCount64 < deadline);

            if (request != _presentationRequest || !IsShown || _closing) return;
            if (_presentationHadActivation && !PanelOwnsForeground())
            {
                // An outside action during preparation wins over the pending reveal.
                HidePanel();
                return;
            }
            StartPanelEntrance(request);
            RevealedBounds = CurrentPanelBounds();
            SetPanelCloaked(false);
            _preparingPresentation = false;
            Activate(); SetForegroundWindow(_hwnd);
            QueuePendingPanelFocus();
        }
        catch (Exception exception)
        {
            Program.Log(exception);
            if (request != _presentationRequest || !IsShown || _closing) return;
            ResetPanelEntrance();
            SetPanelCloaked(false);
            _preparingPresentation = false;
            Activate(); SetForegroundWindow(_hwnd);
            QueuePendingPanelFocus();
        }
    }

    private RectInt32 CurrentPanelBounds() => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    private void StartPanelEntrance(uint request)
    {
        ResetPanelEntrance();
        RestingBounds = CurrentPanelBounds();
        LastEntranceAnimated = _systemUi.AnimationsEnabled && !_accessibility.HighContrast && _animationSubclass is not null;
        if (!LastEntranceAnimated) return;
        _entranceWorkArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        _entranceRequest = request;
        EntranceRunning = true;
        SetBannerPose(0);
        if (!EntranceRunning) return;
        _entranceClock.Restart();
        _entranceTimer.Start();
    }

    private void AdvancePanelEntrance()
    {
        if (!EntranceRunning || _closing || !IsShown || _entranceRequest != _presentationRequest) { ResetPanelEntrance(); return; }
        double progress = Math.Clamp(_entranceClock.Elapsed.TotalMilliseconds / 300, 0, 1);
        if (progress < 1) { SetBannerPose(progress); return; }
        // Restore the target monitor before accepting DPI changes again.
        MoveBannerWindow(_hwnd, 0, RestingBounds.X, RestingBounds.Y, 0, 0, 0x215);
        ResetPanelEntrance();
        PositionPanel();
        QueuePendingPanelFocus();
    }

    private void SetBannerPose(double progress)
    {
        double remaining = Math.Pow(1 - progress, 5);
        int offset = (int)Math.Round((_entranceWorkArea.X + _entranceWorkArea.Width - RestingBounds.X + 1) * remaining);
        int x = RestingBounds.X + offset;
        // Clip the whole native surface, including Acrylic, at the chosen work
        // area's edge. It must not appear on an adjacent monitor while sliding.
        int visibleWidth = Math.Clamp(_entranceWorkArea.X + _entranceWorkArea.Width - x, 0, RestingBounds.Width);
        nint region = CreateRectRgn(0, 0, visibleWidth, RestingBounds.Height);
        if (region == 0 || SetWindowRgn(_hwnd, region, true) == 0)
        {
            if (region != 0) DeleteObject(region);
            // A failed clip must never expose a banner on the next screen.
            MoveBannerWindow(_hwnd, 0, RestingBounds.X, RestingBounds.Y, 0, 0, 0x215);
            ResetPanelEntrance();
            QueuePendingPanelFocus();
            return;
        }
        BannerClipActive = true; // The system owns the successfully assigned region.
        MoveBannerWindow(_hwnd, 0, x, RestingBounds.Y, 0, 0, 0x215);
    }

    private void ResetPanelEntrance()
    {
        EntranceRunning = false;
        _entranceTimer.Stop();
        _entranceClock.Stop();
        if (BannerClipActive && !_closing) SetWindowRgn(_hwnd, 0, true);
        BannerClipActive = false;
    }

    private nint AnimationWindowProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // Only the temporary off-screen entrance pose can cross a monitor edge.
        // Keep the settled target scale until the window returns to that monitor.
        // Ordinary placement and real DPI changes continue through WinUI normally.
        if (message == 0x02E0 && EntranceRunning) return 0; // WM_DPICHANGED
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private delegate nint WindowSubclass(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, WindowSubclass callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, WindowSubclass callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool MoveBannerWindow(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);

    private void CancelPanelPresentation()
    {
        ++_presentationRequest;
        _preparingPresentation = false;
        ResetPanelEntrance();
    }

    internal Task WaitForPreparationForVerificationAsync()
    {
        if (!_controller.IsSmokeTest) throw new InvalidOperationException("Only the isolated smoke harness may await presentation.");
        return _presentationTask;
    }

    internal async Task WaitForPresentationForVerificationAsync()
    {
        await WaitForPreparationForVerificationAsync();
        long deadline = Environment.TickCount64 + 1500;
        while (EntranceRunning && Environment.TickCount64 < deadline) await Task.Delay(16);
        if (EntranceRunning) throw new TimeoutException("The banner entrance did not finish.");
    }

    internal void RefreshAppearanceForVerification()
    {
        if (!_controller.IsSmokeTest) throw new InvalidOperationException("Only the isolated smoke harness may refresh appearance.");
        ApplySystemAppearance();
    }
}
