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
        base.OnTargetConnected(target, root);
        _controller = new DesktopAcrylicController();
        _controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, root));
        ApplyColor();
        _controller.AddSystemBackdropTarget(target);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
        base.OnTargetDisconnected(target);
    }
}
