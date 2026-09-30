namespace TwinkleTray.Core;

public readonly record struct BrightnessLevels(double Hardware, double Gamma);

public static class BrightnessCalibration
{
    public const double MinimumGamma = 20;
    public static double ToHardware(double logical, MonitorSettings settings) => Map(logical, settings, false);
    public static double ToLogical(double hardware, MonitorSettings settings) => Map(hardware, settings, true);

    private static double Map(double value, MonitorSettings settings, bool reverse)
    {
        ArgumentNullException.ThrowIfNull(settings);
        value = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 100);
        var points = (settings.Calibration ?? []).Where(p => p is not null && double.IsFinite(p.Input) && double.IsFinite(p.Output))
            .Select(p => new CalibrationPoint { Input = Math.Clamp(p.Input, 0, 100), Output = Math.Clamp(p.Output, 0, 100) }).ToList();
        var (min, max) = BrightnessMath.NormalizeRange(settings.MinBrightness, settings.MaxBrightness);
        if (min > 0 || !points.Any(p => p.Input == 0)) points.Add(new() { Input = 0, Output = min });
        if (max < 100 || !points.Any(p => p.Input == 100)) points.Add(new() { Input = 100, Output = max });
        points = points.GroupBy(p => p.Input).Select(group => group.Last()).OrderBy(p => p.Input).ToList();
        for (int i = 0; i < points.Count - 1; i++)
        {
            var a = points[i]; var b = points[i + 1];
            var start = reverse ? a.Output : a.Input;
            var end = reverse ? b.Output : b.Input;
            if (value < Math.Min(start, end) || value > Math.Max(start, end)) continue;
            var from = reverse ? a.Input : a.Output;
            var to = reverse ? b.Input : b.Output;
            return start == end ? (from + to) / 2 : from + (value - start) / (end - start) * (to - from);
        }
        var closest = points.MinBy(p => Math.Abs((reverse ? p.Output : p.Input) - value))!;
        return reverse ? closest.Input : closest.Output;
    }

    public static BrightnessLevels GetExtendedLevels(double logical, MonitorSettings settings)
    {
        logical = double.IsNaN(logical) ? 0 : Math.Clamp(logical, 0, 100);
        if (!settings.ExtendMinimum || settings.MainControl == "gamma") return new(ToHardware(logical, settings), 100);
        int breakpoint = Math.Clamp(settings.ExtendMinimumBreakpoint, 1, 90);
        var hardwareLogical = logical >= breakpoint ? (logical - breakpoint) * 100 / (100 - breakpoint) : 0;
        var gamma = logical >= breakpoint ? 100 : MinimumGamma + logical * (100 - MinimumGamma) / breakpoint;
        return new(ToHardware(hardwareLogical, settings), gamma);
    }

    public static double FromExtendedLevels(double hardware, double gamma, MonitorSettings settings)
    {
        var hardwareLogical = ToLogical(hardware, settings);
        if (!settings.ExtendMinimum || settings.MainControl == "gamma") return hardwareLogical;
        int breakpoint = Math.Clamp(settings.ExtendMinimumBreakpoint, 1, 90);
        return gamma < 100 && hardwareLogical <= 0
            ? Math.Clamp((gamma - MinimumGamma) * breakpoint / (100 - MinimumGamma), 0, breakpoint)
            : breakpoint + hardwareLogical * (100 - breakpoint) / 100;
    }

    public static int LinkedFeatureValue(double brightness, FeatureSettings feature)
    {
        var fraction = Math.Clamp(brightness / Math.Clamp(feature.MaxVisual, 1, 100), 0, 1);
        return (int)Math.Round(feature.Min + (feature.Max - feature.Min) * fraction, MidpointRounding.AwayFromZero);
    }
}
