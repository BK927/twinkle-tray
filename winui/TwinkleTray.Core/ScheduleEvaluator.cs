using System.Globalization;

namespace TwinkleTray.Core;

public static class ScheduleEvaluator
{
    /// <summary>
    /// Returns enabled daily entries whose latest occurrence is in (previous, now], ordered by occurrence.
    /// A long pause executes each entry once; callers should set previous when starting or resuming.
    /// Times are local wall-clock times, so both arguments should use the same time zone.
    /// </summary>
    public static IReadOnlyList<ScheduleEntry> GetDue(IReadOnlyList<ScheduleEntry> entries, DateTime previous, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (now <= previous)
            return Array.Empty<ScheduleEntry>();

        var due = new List<(ScheduleEntry Entry, DateTime At)>();
        foreach (var entry in entries)
        {
            if (entry is null || !entry.Enabled || entry.Event != "time" || entry.Brightness is < 0 or > 100 ||
                !TimeOnly.TryParseExact(entry.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                continue;

            var occurrence = now.Date.Add(time.ToTimeSpan());
            if (occurrence > now)
            {
                if (occurrence.Date == DateTime.MinValue.Date)
                    continue;
                occurrence = occurrence.AddDays(-1);
            }

            if (occurrence > previous)
                due.Add((entry, occurrence));
        }

        return due.OrderBy(item => item.At).Select(item => item.Entry).ToArray();
    }

    /// <summary>Solar-aware daily schedule; applies each entry at most once after a long pause.</summary>
    public static IReadOnlyList<ScheduleEntry> GetDue(AppSettings settings, DateTime previous, DateTime now, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (now <= previous) return Array.Empty<ScheduleEntry>();
        return Occurrences(settings, now, zone).Where(item => item.At > previous && item.At <= now)
            .GroupBy(item => item.Entry).Select(group => group.MaxBy(item => item.At)!)
            .OrderBy(item => item.At).Select(item => item.Entry).ToArray();
    }

    public static DateTime? GetOccurrence(ScheduleEntry entry, DateTime date, double latitude = 0, double longitude = 0, TimeZoneInfo? zone = null)
    {
        if (entry is null || !entry.Enabled || entry.Brightness is < 0 or > 100) return null;
        DateTime? time;
        if (string.Equals(entry.Event, "time", StringComparison.OrdinalIgnoreCase))
        {
            if (!TimeOnly.TryParseExact(entry.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return null;
            time = date.Date.Add(parsed.ToTimeSpan());
            // A skipped wall-clock time during the spring DST jump has no occurrence.
            if ((zone ?? TimeZoneInfo.Local).IsInvalidTime(DateTime.SpecifyKind(time.Value, DateTimeKind.Unspecified))) return null;
        }
        else time = SolarCalculator.GetEvent(date, latitude, longitude, entry.Event, zone);
        if (time is null) return null;
        try
        {
            var shifted = time.Value.AddMinutes(Math.Clamp(entry.OffsetMinutes, -1440, 1440));
            return (zone ?? TimeZoneInfo.Local).IsInvalidTime(DateTime.SpecifyKind(shifted, DateTimeKind.Unspecified)) ? null : shifted;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Latest level for each requested monitor, optionally interpolated to its next applicable event.</summary>
    public static IReadOnlyDictionary<string, double> GetCurrentLevels(AppSettings settings, DateTime now, IEnumerable<string> monitorIds, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(monitorIds);
        var occurrences = Occurrences(settings, now, zone).OrderBy(item => item.At).ToArray();
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in monitorIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // The last applicable entry wins at an instant. Use that same target when approaching
            // the instant so duplicate times (including offset collisions) cannot create a jump.
            var applicable = occurrences.Select(item => (item.At, Level: GetLevel(item.Entry, id)))
                .Where(item => item.Level.HasValue).GroupBy(item => item.At).Select(group => group.Last()).ToArray();
            var previous = applicable.LastOrDefault(item => item.At <= now);
            if (!previous.Level.HasValue) continue;
            double level = previous.Level.Value;
            if (settings.ScheduleInterpolation)
            {
                var next = applicable.FirstOrDefault(item => item.At > now);
                if (next.Level.HasValue && next.At > previous.At)
                    level = Interpolate(level, next.Level.Value, (now - previous.At).TotalSeconds / (next.At - previous.At).TotalSeconds);
            }
            result[id] = level;
        }
        return result;
    }

    public static double? GetLevel(ScheduleEntry entry, string monitorId)
    {
        if (entry.IndividualBrightness?.Count > 0)
        {
            if (entry.IndividualBrightness.TryGetValue(monitorId, out var value))
                return value is >= 0 and <= 100 ? value : null;
            // JSON-created dictionaries use an ordinal comparer; monitor targeting is case-insensitive.
            foreach (var pair in entry.IndividualBrightness)
                if (string.Equals(pair.Key, monitorId, StringComparison.OrdinalIgnoreCase))
                    return pair.Value is >= 0 and <= 100 ? pair.Value : null;
            return null;
        }
        return (entry.MonitorId == "all" || string.Equals(entry.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase)) && entry.Brightness is >= 0 and <= 100
            ? entry.Brightness : null;
    }

    public static double Interpolate(double start, double end, double progress) =>
        start + (end - start) * (double.IsNaN(progress) ? 0 : Math.Clamp(progress, 0, 1));

    private static IEnumerable<(ScheduleEntry Entry, DateTime At)> Occurrences(AppSettings settings, DateTime now, TimeZoneInfo? zone)
    {
        // Adjacent source dates cover offsets that cross midnight, interpolation and the previous daily event.
        for (int delta = -2; delta <= 2; delta++)
        {
            DateTime date;
            try { date = now.Date.AddDays(delta); }
            catch (ArgumentOutOfRangeException) { continue; }
            foreach (var entry in settings.Schedule ?? [])
            {
                var at = GetOccurrence(entry, date, settings.Latitude, settings.Longitude, zone);
                if (at.HasValue) yield return (entry, at.Value);
            }
        }
    }
}
