using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace TwinkleTray.WinUI;

public sealed partial class MainWindow
{
    private readonly UISettings _systemUi = new();
    private bool _appearanceSubscribed, _preparingPresentation, _presentationHadActivation;
    private uint _presentationRequest;
    private Task _presentationTask = Task.CompletedTask;
    private Visual? _panelVisual;
    internal bool EntranceRunning { get; private set; }
    internal bool LastEntranceAnimated { get; private set; }
    internal bool LastPresentationSettled { get; private set; }
    internal bool LastCloakSucceeded { get; private set; }
    internal RectInt32 RevealedBounds { get; private set; }

    private void InitializeSystemAppearance()
    {
        try { _systemUi.ColorValuesChanged += SystemColorsChanged; _appearanceSubscribed = true; }
        catch (System.Runtime.InteropServices.COMException) { }
        // One Fluent content entrance owns the motion; do not combine it with a
        // generic top-level window transition while changing its initial bounds.
        int disabled = 1;
        DwmSetWindowAttribute(_hwnd, 3, ref disabled, sizeof(int));
        Closed += (_, _) =>
        {
            ++_presentationRequest;
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
            RevealedBounds = CurrentPanelBounds();
            StartPanelEntrance(request);
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
        LastEntranceAnimated = _systemUi.AnimationsEnabled && !_accessibility.HighContrast;
        if (!LastEntranceAnimated) return;
        ElementCompositionPreview.SetIsTranslationEnabled(PanelLayout, true);
        _panelVisual = ElementCompositionPreview.GetElementVisual(PanelLayout);
        var compositor = _panelVisual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(Vector2.Zero, new Vector2(0, 1));
        // WinUI ControlFastAnimationDuration: 167 ms, Fast Out / Slow In.
        var duration = TimeSpan.FromMilliseconds(167);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = duration; fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1, easing);
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Duration = duration;
        slide.InsertKeyFrame(0, new Vector3(0, 8, 0)); slide.InsertKeyFrame(1, Vector3.Zero, easing);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        EntranceRunning = true;
        _panelVisual.StartAnimation("Opacity", fade);
        _panelVisual.StartAnimation("Translation", slide);
        batch.Completed += (_, _) =>
        {
            if (request == _presentationRequest) EntranceRunning = false;
            batch.Dispose();
        };
        batch.End();
    }

    private void ResetPanelEntrance()
    {
        EntranceRunning = false;
        if (_panelVisual is null || _closing) return;
        _panelVisual.StopAnimation("Opacity");
        _panelVisual.StopAnimation("Translation");
        _panelVisual.Opacity = 1;
        _panelVisual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private void CancelPanelPresentation()
    {
        ++_presentationRequest;
        _preparingPresentation = false;
        ResetPanelEntrance();
    }

    internal Task WaitForPresentationForVerificationAsync()
    {
        if (!_controller.IsSmokeTest) throw new InvalidOperationException("Only the isolated smoke harness may await presentation.");
        return _presentationTask;
    }

    internal void RefreshAppearanceForVerification()
    {
        if (!_controller.IsSmokeTest) throw new InvalidOperationException("Only the isolated smoke harness may refresh appearance.");
        ApplySystemAppearance();
    }
}
