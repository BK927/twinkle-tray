namespace TwinkleTray.Core;

public static class SensorCurve
{
    /// <summary>Upstream uses a linear lux curve, clamped at each threshold.</summary>
    public static int ToBrightness(double lux, SensorMonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!double.IsFinite(lux) || lux < 0) throw new ArgumentOutOfRangeException(nameof(lux));
        if (!double.IsFinite(settings.MinLux) || !double.IsFinite(settings.MaxLux) || settings.MinLux < 0 || settings.MaxLux <= settings.MinLux)
            throw new ArgumentException("The maximum lux must be greater than the minimum lux.", nameof(settings));
        double fraction = Math.Clamp((lux - settings.MinLux) / (settings.MaxLux - settings.MinLux), 0, 1);
        return (int)Math.Round(Math.Clamp(settings.MinBrightness + fraction * (settings.MaxBrightness - settings.MinBrightness), 0, 100), MidpointRounding.AwayFromZero);
    }
}
