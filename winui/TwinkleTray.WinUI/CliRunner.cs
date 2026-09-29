using System.Text.Json;
using TwinkleTray.Core;
using TwinkleTray.Hardware;

namespace TwinkleTray.WinUI;

internal static class CliRunner
{
    internal static IReadOnlyList<MonitorSnapshot> DemoMonitors => new[]
    {
        new MonitorSnapshot("demo:external", "DELL U2723QE", "DDC/CI", 72, true, true, 75),
        new MonitorSnapshot("demo:internal", "Built-in Display", "WMI", 48, true, false, null)
    };

    internal static IEnumerable<MonitorSnapshot> Select(IReadOnlyList<MonitorSnapshot> monitors, CommandLineOptions command)
    {
        if (command.All) return monitors;
        if (command.MonitorNum is int index)
        {
            if (index < 1 || index > monitors.Count) throw new ArgumentException($"Monitor {index} was not found. Use --List to see the current monitor order.");
            return new[] { monitors[index - 1] };
        }
        if (command.MonitorId is string id)
        {
            var selected = monitors.Where(m => m.Id.Contains(id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (selected.Length != 1) throw new ArgumentException($"Monitor ID '{id}' matched {selected.Length} displays. Use a unique ID from --List.");
            return selected;
        }
        return Array.Empty<MonitorSnapshot>();
    }

    internal static async Task<string> RunAsync(CommandLineOptions command)
    {
        using var hardware = new MonitorService();
        var monitors = command.Demo ? DemoMonitors : await hardware.RefreshAsync();
        if (command.List) return JsonSerializer.Serialize(monitors.Select((m, i) => new { Number = i + 1, m.Id, m.Name, m.Connection, m.Brightness, m.SupportsBrightness, m.SupportsContrast, m.Contrast }), new JsonSerializerOptions { WriteIndented = true });
        var settings = command.Demo ? new AppSettings() : new SettingsStore().Load();
        var selected = Select(monitors, command).Where(m => command.Vcp is not null || m.SupportsBrightness).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("No matching displays were detected.");
        foreach (var monitor in selected)
        {
            if (command.Vcp is { } vcp) { if (!command.Demo) await hardware.SetVcpAsync(monitor.Id, vcp.Code, vcp.Value); continue; }
            if (!monitor.SupportsBrightness) continue;
            var preferences = settings.Monitors.GetValueOrDefault(monitor.Id) ?? new MonitorSettings();
            double logical = BrightnessMath.ToLogical(monitor.Brightness, preferences.MinBrightness, preferences.MaxBrightness);
            double value = command.Set ?? logical + (command.Offset ?? 0);
            if (!command.Demo) await hardware.SetBrightnessAsync(monitor.Id, BrightnessMath.ToHardware(value, preferences.MinBrightness, preferences.MaxBrightness));
        }
        return "OK";
    }
}
