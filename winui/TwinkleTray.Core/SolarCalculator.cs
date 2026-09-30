namespace TwinkleTray.Core;

/// <summary>Solar times using the SunCalc 1.9 astronomical model used by upstream. See ThirdPartyNotices.txt.</summary>
public static class SolarCalculator
{
    private const double Rad = Math.PI / 180;
    private static readonly Dictionary<string, (double Angle, bool Morning)> Events = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sunrise"] = (-0.833, true), ["sunset"] = (-0.833, false),
        ["sunriseEnd"] = (-0.3, true), ["sunsetStart"] = (-0.3, false),
        ["dawn"] = (-6, true), ["dusk"] = (-6, false),
        ["nauticalDawn"] = (-12, true), ["nauticalDusk"] = (-12, false),
        ["nightEnd"] = (-18, true), ["night"] = (-18, false),
        ["goldenHourEnd"] = (6, true), ["goldenHour"] = (6, false)
    };
    public static IReadOnlyList<string> EventNames { get; } = new[] { "dawn", "sunrise", "sunriseEnd", "goldenHourEnd", "solarNoon", "goldenHour", "sunsetStart", "sunset", "dusk", "nauticalDawn", "nauticalDusk", "nightEnd", "night", "nadir" };

    /// <returns>Local wall-clock event time, or null when the solar event does not occur (polar day/night).</returns>
    public static DateTime? GetEvent(DateTime date, double latitude, double longitude, string eventName, TimeZoneInfo? zone = null)
    {
        if (!double.IsFinite(latitude) || latitude < -90 || latitude > 90) throw new ArgumentOutOfRangeException(nameof(latitude));
        if (!double.IsFinite(longitude) || longitude < -180 || longitude > 180) throw new ArgumentOutOfRangeException(nameof(longitude));
        zone ??= TimeZoneInfo.Local;
        var noonLocal = DateTime.SpecifyKind(date.Date.AddHours(12), DateTimeKind.Unspecified);
        var noonUtc = TimeZoneInfo.ConvertTimeToUtc(noonLocal, zone);
        double days = (noonUtc - DateTime.UnixEpoch).TotalDays + 2440587.5 - 2451545;
        double westLongitude = -longitude * Rad, latitudeRadians = latitude * Rad;
        double cycle = Math.Floor(days - 0.0009 - westLongitude / (2 * Math.PI) + 0.5);
        double transit = 0.0009 + westLongitude / (2 * Math.PI) + cycle;
        double anomaly = Rad * (357.5291 + 0.98560028 * transit);
        double ecliptic = anomaly + Rad * (1.9148 * Math.Sin(anomaly) + 0.02 * Math.Sin(2 * anomaly) + 0.0003 * Math.Sin(3 * anomaly) + 102.9372) + Math.PI;
        double declination = Math.Asin(Math.Sin(ecliptic) * Math.Sin(Rad * 23.4397));
        double solarNoon = 2451545 + transit + 0.0053 * Math.Sin(anomaly) - 0.0069 * Math.Sin(2 * ecliptic);
        double julian;
        if (string.Equals(eventName, "solarNoon", StringComparison.OrdinalIgnoreCase)) julian = solarNoon;
        else if (string.Equals(eventName, "nadir", StringComparison.OrdinalIgnoreCase)) julian = solarNoon - 0.5;
        else
        {
            if (!Events.TryGetValue(eventName ?? "", out var solarEvent)) return null;
            double cosine = (Math.Sin(solarEvent.Angle * Rad) - Math.Sin(latitudeRadians) * Math.Sin(declination)) /
                (Math.Cos(latitudeRadians) * Math.Cos(declination));
            if (!double.IsFinite(cosine) || cosine < -1 || cosine > 1) return null;
            double hourAngle = Math.Acos(cosine) / (2 * Math.PI);
            julian = solarNoon + (solarEvent.Morning ? -hourAngle : hourAngle);
        }
        var utc = DateTime.UnixEpoch.AddDays(julian - 2440587.5);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, zone), DateTimeKind.Unspecified);
    }
}
