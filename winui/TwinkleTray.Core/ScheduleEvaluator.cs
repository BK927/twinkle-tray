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
            if (entry is null || !entry.Enabled || entry.Brightness is < 0 or > 100 ||
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
}
