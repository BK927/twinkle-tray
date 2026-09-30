using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TwinkleTray.WinUI.Services;

// Per-window material: SystemBackdrop owns activation/accessibility policy;
// the controller supplies the Shell accent tint absent from the default material.
internal sealed class AccentAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    internal Color SurfaceColor { get; private set; }
    internal AccentAcrylicBackdrop(Color color) => SurfaceColor = color;
    internal void UpdateColor(Color color) { SurfaceColor = color; ApplyColor(); }

    private void ApplyColor()
    {
        if (_controller is null) return;
        _controller.TintColor = SurfaceColor;
        _controller.FallbackColor = SurfaceColor;
        _controller.TintOpacity = .8f;
        _controller.LuminosityOpacity = .85f;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        if (_controller is not null) throw new InvalidOperationException("An accent backdrop instance cannot be shared between windows.");
        base.OnTargetConnected(target, root);
        _controller = new DesktopAcrylicController();
        _controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, root));
        ApplyColor();
        _controller.AddSystemBackdropTarget(target);
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        // The controller already observes the live default configuration supplied
        // in OnTargetConnected. There is no custom policy to copy or reapply here.
        // Do not forward this optional notification back through the base ABI:
        // its native implementation is a no-op, but parameter validation rejects
        // a target whose weak reference has expired during window teardown.
        // WinUI source: default no-op at L87-L91, policy update before notification
        // at L237-L255 (theme) and L343-L359 (activation):
        // https://github.com/microsoft/microsoft-ui-xaml/blob/main/dxaml/xcp/dxaml/lib/SystemBackdrop_Partial.cpp#L87-L91
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        // Detach the default configuration's theme/activation listeners before
        // removing the controller that consumes that configuration.
        base.OnTargetDisconnected(target);
        var controller = _controller;
        _controller = null;
        if (controller is null) return;
        try { controller.RemoveSystemBackdropTarget(target); }
        finally { controller.Dispose(); }
    }
}
