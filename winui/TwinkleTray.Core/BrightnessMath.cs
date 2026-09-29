namespace TwinkleTray.Core;

/// <summary>Maps the user-facing 0–100 slider to a monitor's calibrated percentage range.</summary>
public static class BrightnessMath
{
    public static int ToHardware(double logical, int min, int max)
    {
        (min, max) = NormalizeRange(min, max);
        var result = min + ClampPercent(logical) * (max - min) / 100.0;
        return (int)Math.Round(result, MidpointRounding.AwayFromZero);
    }

    public static double ToLogical(double hardware, int min, int max)
    {
        (min, max) = NormalizeRange(min, max);
        return min == max ? 0 : ClampPercent((ClampPercent(hardware) - min) * 100 / (max - min));
    }

    public static (int Min, int Max) NormalizeRange(int min, int max)
    {
        min = Math.Clamp(min, 0, 100);
        max = Math.Clamp(max, 0, 100);
        return min <= max ? (min, max) : (max, min);
    }

    private static double ClampPercent(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 100);
}
