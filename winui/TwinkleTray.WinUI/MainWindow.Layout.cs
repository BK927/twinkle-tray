using TwinkleTray.Core;
using TwinkleTray.Hardware;
using TwinkleTray.WinUI.Services;

namespace TwinkleTray.WinUI;

public sealed partial class MainWindow
{
    // Capture only the inputs that determine controls. Comparing values directly
    // avoids serializing a new object graph for every brightness update, while
    // still noticing in-place settings edits without an invalidation protocol.
    private readonly record struct LayoutKey(bool Linked, string AllDisplays, string Contrast, string Unsupported, string Empty);
    private readonly record struct MonitorLayoutKey(string Id, string Name, string Connection, bool SupportsContrast,
        bool CanControl, bool ShowName, bool ShowValue, bool ShowContrast, string IconGlyph, bool ContrastIsFeature);
    private readonly record struct FeatureLayoutKey(byte Code, string Name, int Min, int Max,
        string IconGlyph, string IconType, string IconPath, string IconText);
    private sealed record HardwareLayoutSnapshot(string Name, uint Maximum, uint[] AllowedValues);
    private sealed record FeatureLayoutSnapshot(FeatureLayoutKey Key, HardwareLayoutSnapshot[] Hardware);
    private sealed record MonitorLayoutSnapshot(MonitorLayoutKey Key, FeatureLayoutSnapshot[] Features);
    private sealed record LayoutSnapshot(LayoutKey Key, MonitorLayoutSnapshot[] Monitors);

    private LayoutKey CurrentLayoutKey() => new(_controller.Settings.LinkedBrightness,
        T("GENERIC_ALL_DISPLAYS", "All displays"), T("PANEL_LABEL_CONTRAST", "Contrast"),
        T("GENERIC_NOT_SUPPORTED", "Not supported"),
        T("GENERIC_NO_COMPATIBLE_DISPLAYS", "No compatible displays found. Check that DDC/CI is enabled in your monitor settings."));

    private MonitorLayoutKey CurrentMonitorLayoutKey(MonitorSnapshot monitor, MonitorSettings preferences) => new(
        monitor.Id, string.IsNullOrWhiteSpace(preferences.Name) ? monitor.Name : preferences.Name,
        monitor.Connection, monitor.SupportsContrast, BrightnessControl.CanControl(monitor, preferences),
        preferences.ShowName, preferences.ShowValue, preferences.ShowContrast, preferences.IconGlyph,
        preferences.Features.TryGetValue(0x12, out var contrast) && contrast.Enabled);

    private static FeatureLayoutKey CurrentFeatureLayoutKey(byte code, FeatureSettings settings) => new(code,
        settings.Name, settings.Min, settings.Max, settings.IconGlyph, settings.IconType, settings.IconPath, settings.IconText);

    private bool LayoutMatches(IReadOnlyList<MonitorSnapshot> monitors)
    {
        if (_layoutSnapshot is not { } previous || previous.Key != CurrentLayoutKey() || previous.Monitors.Length != monitors.Count) return false;
        for (int i = 0; i < monitors.Count; i++)
        {
            var monitor = monitors[i];
            var preferences = _controller.Preferences(monitor.Id);
            var saved = previous.Monitors[i];
            if (saved.Key != CurrentMonitorLayoutKey(monitor, preferences)) return false;
            int featureIndex = 0;
            var hardware = _controller.Features(monitor.Id);
            foreach (var pair in preferences.Features)
            {
                if (!pair.Value.Enabled || pair.Value.LinkedToBrightness) continue;
                if (featureIndex >= saved.Features.Length) return false;
                var feature = saved.Features[featureIndex++];
                if (feature.Key != CurrentFeatureLayoutKey(pair.Key, pair.Value) || !HardwareLayoutMatches(feature.Hardware, hardware, pair.Key)) return false;
            }
            if (featureIndex != saved.Features.Length) return false;
        }
        return true;
    }

    private static bool HardwareLayoutMatches(HardwareLayoutSnapshot[] saved, IReadOnlyList<VcpFeature> current, byte code)
    {
        int index = 0;
        for (int i = 0; i < current.Count; i++)
        {
            var feature = current[i];
            if (feature.Code != code) continue;
            if (index >= saved.Length) return false;
            var previous = saved[index++];
            if (previous.Name != feature.Name || previous.Maximum != feature.Maximum || previous.AllowedValues.Length != feature.AllowedValues.Count) return false;
            for (int j = 0; j < previous.AllowedValues.Length; j++)
                if (previous.AllowedValues[j] != feature.AllowedValues[j]) return false;
        }
        return index == saved.Length;
    }

    private LayoutSnapshot CaptureLayout(IReadOnlyList<MonitorSnapshot> monitors)
    {
        var saved = new MonitorLayoutSnapshot[monitors.Count];
        for (int i = 0; i < monitors.Count; i++)
        {
            var monitor = monitors[i];
            var preferences = _controller.Preferences(monitor.Id);
            var features = new List<FeatureLayoutSnapshot>();
            var hardware = _controller.Features(monitor.Id);
            foreach (var pair in preferences.Features)
            {
                if (!pair.Value.Enabled || pair.Value.LinkedToBrightness) continue;
                // AllowedValues can be a mutable list. Copy its contents so an
                // in-place capability update still rebuilds the corresponding UI.
                var capabilities = hardware.Where(feature => feature.Code == pair.Key)
                    .Select(feature => new HardwareLayoutSnapshot(feature.Name, feature.Maximum, feature.AllowedValues.ToArray())).ToArray();
                features.Add(new(CurrentFeatureLayoutKey(pair.Key, pair.Value), capabilities));
            }
            saved[i] = new(CurrentMonitorLayoutKey(monitor, preferences), features.ToArray());
        }
        return new(CurrentLayoutKey(), saved);
    }
}
