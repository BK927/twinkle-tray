using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace TwinkleTray.WinUI;

public sealed partial class SettingsWindow
{
    private readonly CancellationTokenSource _presentationLifetime = new();
    private nint _settingsHwnd;
    private bool _settingsPresentationRunning, _settingsPresentationComplete, _settingsSurfaceReady;
    private Task _settingsPresentationTask = Task.CompletedTask;
    internal bool InitialPresentationCloaked { get; private set; }
    internal bool InitialPresentationLayoutReady { get; private set; }
    internal bool InitialPresentationUncloakSucceeded { get; private set; }
    internal bool RenderedFrameReady { get; private set; }
    internal bool InitialPresentationRevealed { get; private set; }
    internal bool InitialPresentationCancelled { get; private set; }
    internal bool InitialPresentationHadSolidBackground { get; private set; }
    internal RectInt32 InitialPresentationBounds { get; private set; }
    internal RectInt32 InitialRevealBounds { get; private set; }

    private void InitializeSettingsPresentation()
    {
        _settingsHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Closed += (_, _) =>
        {
            if (_settingsPresentationRunning) InitialPresentationCancelled = true;
            _presentationLifetime.Cancel();
            _presentationLifetime.Dispose();
        };
    }

    internal void ShowPrepared()
    {
        if (_closing) return;
        if (_settingsPresentationComplete || _settingsPresentationRunning)
        {
            Activate();
            return;
        }

        // Size before the HWND first becomes visible. Loaded used to resize an
        // already activated, default-sized window and exposed its empty surface.
        InitialPresentationLayoutReady = InitialPresentationRevealed = RenderedFrameReady = false;
        InitialPresentationUncloakSucceeded = InitialPresentationCancelled = false;
        InitialPresentationBounds = SettingsWindowBounds();
        InitialPresentationHadSolidBackground = SolidBackground.Visibility == Visibility.Visible;
        InitialPresentationCloaked = SetSettingsCloaked(true);
        _settingsPresentationRunning = true;
        // Retain the initiating user input's activation grant; the preparation
        // task never reactivates over another app after its asynchronous wait.
        var cancellationToken = _presentationLifetime.Token;
        try
        {
            Activate();
            _settingsPresentationTask = PrepareFirstSettingsFrameAsync(cancellationToken);
        }
        catch
        {
            _settingsPresentationRunning = false;
            SetSettingsCloaked(false);
            throw;
        }
    }

    private async Task PrepareFirstSettingsFrameAsync(CancellationToken cancellationToken)
    {
        try
        {
            var elapsed = Stopwatch.StartNew();
            RectInt32 previous = default;
            bool measured = false;
            int stableFrames = 0;
            while (elapsed.Elapsed < TimeSpan.FromMilliseconds(1500))
            {
                await Task.Delay(16, cancellationToken);
                if (_closing) return;
                if (!Root.IsLoaded || Root.XamlRoot is null) continue;
                if (!measured)
                {
                    // Force at most one initial measure, then let WinUI process
                    // its normal layout/restore callbacks between observations.
                    Root.UpdateLayout();
                    measured = true;
                }
                var bounds = SettingsWindowBounds();
                double scale = Root.XamlRoot.RasterizationScale;
                bool arranged = Root.ActualWidth > 0 && Root.ActualHeight > 0 &&
                    PageContent.ActualWidth > 0 && PageContent.ActualHeight > 0 &&
                    Math.Abs(Root.ActualWidth * scale - AppWindow.ClientSize.Width) <= 2 &&
                    Math.Abs(Root.ActualHeight * scale - AppWindow.ClientSize.Height) <= 2;
                stableFrames = arranged && bounds.Equals(previous) ? stableFrames + 1 : 0;
                previous = bounds;
                if (stableFrames >= 2) { InitialPresentationLayoutReady = true; break; }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_closing) return;
            if (!RevealFirstSettingsFrame()) return;

            // Rendering callbacks establish that the renderer has advanced,
            // unlike a fixed delay. This does not measure native screen pixels.
            RenderedFrameReady = await WaitForSettingsRenderAsync(cancellationToken);
            if (_closing) return;
            _settingsSurfaceReady = InitialPresentationLayoutReady && RenderedFrameReady;
            ApplySettingsBackdrop();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Program.Log(exception);
            if (!_closing)
            {
                RevealFirstSettingsFrame();
                // Retain the solid theme background if material preparation failed.
                _settingsSurfaceReady = false;
                SolidBackground.Visibility = Visibility.Visible;
            }
        }
        finally { _settingsPresentationRunning = false; }
    }

    private bool RevealFirstSettingsFrame()
    {
        InitialRevealBounds = SettingsWindowBounds();
        InitialPresentationUncloakSucceeded = SetSettingsCloaked(false);
        InitialPresentationRevealed = !InitialPresentationCloaked || InitialPresentationUncloakSucceeded;
        _settingsPresentationComplete = InitialPresentationRevealed;
        if (!InitialPresentationRevealed)
            Program.Log(new InvalidOperationException("The settings window could not be uncloaked after initial layout."));
        return InitialPresentationRevealed;
    }

    private async Task<bool> WaitForSettingsRenderAsync(CancellationToken cancellationToken)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int frames = 0;
        EventHandler<object> rendered = (_, _) =>
        {
            if (!_closing && Root.IsLoaded && ++frames >= 2) ready.TrySetResult();
        };
        CompositionTarget.Rendering += rendered;
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
            return true;
        }
        catch (TimeoutException) { return false; }
        finally { CompositionTarget.Rendering -= rendered; }
    }

    internal Task WaitForInitialPresentationForVerificationAsync()
    {
        if (!Program.Options.SmokeTest) throw new InvalidOperationException("Settings presentation verification requires smoke-test mode.");
        return _settingsPresentationTask;
    }

    private RectInt32 SettingsWindowBounds() => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    private bool SetSettingsCloaked(bool value)
    {
        if (_closing) return false;
        int cloaked = value ? 1 : 0;
        return SetSettingsWindowAttribute(_settingsHwnd, 13, ref cloaked, sizeof(int)) >= 0;
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int SetSettingsWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
