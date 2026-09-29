using System.Text.Json;
using TwinkleTray.Core;
using TwinkleTray.Hardware;
using TwinkleTray.WinUI.Services;

namespace TwinkleTray.WinUI;

internal static class CliRunner
{
    internal static IReadOnlyList<MonitorSnapshot> DemoMonitors => new[]
    {
        new MonitorSnapshot("demo:external", "DELL U2723QE", "DDC/CI", 72, true, true, 75) { DeviceName = "DEMO1", DeviceInstanceId = "DISPLAY\\DEMO\\1", HdrSupported = true, HdrActive = true, SdrBrightness = 50, GammaBrightness = 100 },
        new MonitorSnapshot("demo:internal", "Built-in Display", "WMI", 48, true, false, null) { DeviceName = "DEMO2", DeviceInstanceId = "DISPLAY\\DEMO\\2", GammaBrightness = 100 }
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
        var settings = command.Demo ? new AppSettings() : new SettingsStore().Load();
        MonitorSettings Preferences(string id)
        {
            var configured = settings.Monitors.GetValueOrDefault(id) ?? new MonitorSettings();
            return settings.UseSoftwareBrightnessFallback ? configured with { SoftwareFallback = true } : configured;
        }
        hardware.Configure(BrightnessControl.Options(settings));
        var monitors = command.Demo ? DemoMonitors : await hardware.RefreshAsync();
        if (command.List) return JsonSerializer.Serialize(monitors.Select((m, i) => new { Number = i + 1, m.Id, m.Name, m.Connection, Brightness = BrightnessControl.Logical(m, Preferences(m.Id)), RawBrightness = m.Brightness, m.SupportsBrightness, m.SupportsContrast, m.Contrast, m.HdrSupported, m.HdrActive, m.SdrBrightness, m.GammaBrightness }), new JsonSerializerOptions { WriteIndented = true });
        if (command.UseTime)
        {
            var levels = ScheduleEvaluator.GetCurrentLevels(settings, DateTime.Now, monitors.Where(m => settings.Monitors.GetValueOrDefault(m.Id)?.Hidden != true).Select(m => m.Id));
            foreach (var pair in levels)
            {
                var monitor = monitors.Single(m => m.Id == pair.Key);
                var preferences = Preferences(monitor.Id);
                if (BrightnessControl.CanControl(monitor, preferences)) await BrightnessControl.SetAsync(hardware, monitor, preferences, pair.Value, command.Demo, default);
            }
            return "OK";
        }
        var selected = Select(monitors, command).Where(m => command.Vcp is not null || BrightnessControl.CanControl(m, Preferences(m.Id))).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("No matching displays were detected.");
        foreach (var monitor in selected)
        {
            if (command.Vcp is { } vcp) { if (!command.Demo) await hardware.SetVcpAsync(monitor.Id, vcp.Code, vcp.Value); continue; }
            var preferences = Preferences(monitor.Id);
            double logical = BrightnessControl.Logical(monitor, preferences);
            double value = command.Set ?? logical + (command.Offset ?? 0);
            await BrightnessControl.SetAsync(hardware, monitor, preferences, value, command.Demo, default);
            settings.LastBrightness[monitor.Id] = Math.Clamp(value, 0, 100);
        }
        if (!command.Demo && command.Vcp is null) new SettingsStore().Save(settings);
        return "OK";
    }
}
