using TwinkleTray.Core;
using TwinkleTray.Hardware;

namespace TwinkleTray.WinUI.Services;

internal static class BrightnessControl
{
    internal static HardwareOptions Options(AppSettings settings, IEnumerable<string>? connectedIds = null) => new()
    {
        DisableWmi = settings.DisableWmi,
        DisableDdc = settings.DisableDdc,
        DisableAppleStudio = settings.DisableAppleStudio,
        VcpReadDelayMilliseconds = settings.VcpReadDelayMilliseconds,
        ForceHdrDisplays = settings.Monitors.Where(x => x.Value.ForceHdr).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
        SoftwareFallback = settings.UseSoftwareBrightnessFallback || settings.Monitors.Values.Any(x => x.SoftwareFallback),
        EnableGamma = settings.UseSoftwareBrightnessFallback || settings.Monitors.Values.Any(x => x.MainControl == "gamma" || x.ExtendMinimum || x.SoftwareFallback) || settings.Hotkeys.Any(h => h.Actions.Any(a => a.Target == "gamma")),
        GammaDisplays = settings.Monitors.Where(x => x.Value.MainControl == "gamma" || x.Value.ExtendMinimum || x.Value.SoftwareFallback).Select(x => x.Key)
            .Concat(settings.UseSoftwareBrightnessFallback ? connectedIds ?? [] : []).ToHashSet(StringComparer.OrdinalIgnoreCase),
        BrightnessCodes = settings.Monitors.Where(x => x.Value.BrightnessVcp != 0x10).ToDictionary(x => x.Key, x => x.Value.BrightnessVcp),
        CustomVcpCodes = settings.Monitors.ToDictionary(x => x.Key, x => x.Value.Features.Where(f => f.Value.Enabled).Select(f => f.Key).ToList())
    };

    internal static bool CanControl(MonitorSnapshot monitor, MonitorSettings settings) => settings.MainControl switch
    {
        "sdr" => monitor.SdrBrightness is not null && (monitor.HdrActive || settings.ForceHdr),
        "gamma" => monitor.SupportsGammaBrightness,
        _ => (monitor.SupportsBrightness || (settings.SoftwareFallback && monitor.SupportsGammaBrightness)) && (!settings.ExtendMinimum || monitor.SupportsGammaBrightness)
    };

    internal static double Logical(MonitorSnapshot monitor, MonitorSettings settings) => settings.MainControl switch
    {
        "sdr" => BrightnessCalibration.ToLogical(monitor.SdrBrightness ?? 0, settings),
        "gamma" => BrightnessCalibration.ToLogical(((monitor.GammaBrightness ?? 100) - 20) * 1.25, settings),
        _ when settings.SoftwareFallback && !monitor.SupportsBrightness => BrightnessCalibration.ToLogical(((monitor.GammaBrightness ?? 100) - 20) * 1.25, settings),
        _ when settings.ExtendMinimum => BrightnessCalibration.FromExtendedLevels(monitor.Brightness, monitor.GammaBrightness ?? 100, settings),
        _ => BrightnessCalibration.ToLogical(monitor.Brightness, settings)
    };

    internal static async Task<MonitorSnapshot> SetAsync(MonitorService hardware, MonitorSnapshot monitor, MonitorSettings settings, double value, bool demo, CancellationToken token)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (!CanControl(monitor, settings)) throw new NotSupportedException($"The configured brightness mode is not supported by {monitor.Name} in its current color mode.");
        value = Math.Clamp(value, 0, 100);
        double calibrated = BrightnessCalibration.ToHardware(value, settings);
        if (settings.MainControl == "sdr")
        {
            if (!demo) await hardware.SetSdrBrightnessAsync(monitor.Id, calibrated, token);
            return monitor with { SdrBrightness = calibrated };
        }
        if (settings.MainControl == "gamma" || (settings.SoftwareFallback && !monitor.SupportsBrightness))
        {
            double gamma = 20 + calibrated * .8;
            if (!demo) await hardware.SetGammaBrightnessAsync(monitor.Id, gamma, token);
            return monitor with { GammaBrightness = gamma };
        }
        if (settings.ExtendMinimum)
        {
            var levels = BrightnessCalibration.GetExtendedLevels(value, settings);
            if (!demo)
            {
                await hardware.SetBrightnessAsync(monitor.Id, levels.Hardware, token);
                await hardware.SetGammaBrightnessAsync(monitor.Id, levels.Gamma, token);
            }
            return monitor with { Brightness = levels.Hardware, GammaBrightness = levels.Gamma };
        }
        if (!demo) await hardware.SetBrightnessAsync(monitor.Id, calibrated, token);
        return monitor with { Brightness = calibrated };
    }
}
