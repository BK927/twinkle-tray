using System.Globalization;
using TwinkleTray.Core;

internal static class AutomationEdgeTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Coincident schedules interpolate toward the level applied at the boundary", () =>
        {
            var settings = new AppSettings { ScheduleInterpolation = true, Schedule =
            [
                new() { Time = "06:00", Brightness = 20 },
                new() { Time = "12:00", IndividualBrightness = new() { ["a"] = 80, ["b"] = 40 } },
                new() { Time = "12:00", MonitorId = "a", Brightness = 60 }
            ] };
            var halfway = ScheduleEvaluator.GetCurrentLevels(settings, new(2026, 9, 29, 9, 0, 0), ["a", "b"], TimeZoneInfo.Utc);
            var boundary = ScheduleEvaluator.GetCurrentLevels(settings, new(2026, 9, 29, 12, 0, 0), ["a", "b"], TimeZoneInfo.Utc);
            Near(40, halfway["a"]);
            Near(30, halfway["b"]);
            Near(60, boundary["a"]);
            Near(40, boundary["b"]);
            // Disabled or unrelated entries at the same instant must not replace an applicable target.
            settings.Schedule.Add(new() { Time = "12:00", Brightness = 100, Enabled = false });
            settings.Schedule.Add(new() { Time = "12:00", MonitorId = "unrelated", Brightness = 0 });
            Near(40, ScheduleEvaluator.GetCurrentLevels(settings, new(2026, 9, 29, 9, 0, 0), ["a"], TimeZoneInfo.Utc)["a"]);
        }),
        ("Offset collisions use the same per-monitor precedence through midnight", () =>
        {
            var settings = new AppSettings { ScheduleInterpolation = true, Schedule =
            [
                new() { Time = "18:00", Brightness = 80 },
                new() { Time = "23:30", OffsetMinutes = 30, Brightness = 60 },
                new() { Time = "00:00", Brightness = 20 }
            ] };
            Near(50, ScheduleEvaluator.GetCurrentLevels(settings, new(2026, 9, 29, 21, 0, 0), ["a"], TimeZoneInfo.Utc)["a"]);
            Near(20, ScheduleEvaluator.GetCurrentLevels(settings, new(2026, 9, 30, 0, 0, 0), ["a"], TimeZoneInfo.Utc)["a"]);
        }),
        ("Individual schedule IDs match the same casing variants as single-monitor schedules", () =>
        {
            var settings = new AppSettings { Schedule = [new() { Time = "20:00", IndividualBrightness = new() { ["DISPLAY#AbC"] = 0 } }] };
            var levels = ScheduleEvaluator.GetCurrentLevels(settings, new(2026, 9, 29, 21, 0, 0), ["display#abc", "DISPLAY#ABC", "other"], TimeZoneInfo.Utc);
            Equal(1, levels.Count);
            Near(0, levels["display#abc"]);
            Equal<double?>(null, ScheduleEvaluator.GetLevel(settings.Schedule[0], "other"));
        }),
        ("Schedule catch-up uses the latest applicable event independently per monitor", () =>
        {
            var settings = new AppSettings { Schedule =
            [
                new() { Id = "a-evening", Time = "20:00", MonitorId = "a", Brightness = 25 },
                new() { Id = "b-late", Time = "23:00", MonitorId = "b", Brightness = 90 },
                new() { Id = "a-morning", Time = "08:00", MonitorId = "a", Brightness = 75 }
            ] };
            var now = new DateTime(2026, 9, 29, 6, 0, 0);
            var due = ScheduleEvaluator.GetDue(settings, now.AddDays(-30), now, TimeZoneInfo.Utc);
            Equal("a-morning,a-evening,b-late", string.Join(',', due.Select(entry => entry.Id)));
            var levels = ScheduleEvaluator.GetCurrentLevels(settings, now, ["a", "b", "unknown"], TimeZoneInfo.Utc);
            Equal(2, levels.Count);
            Near(25, levels["a"]);
            Near(90, levels["b"]);
            Equal(0, ScheduleEvaluator.GetDue(settings, now, now.AddMinutes(-1), TimeZoneInfo.Utc).Count);
        }),
        ("Maximum positive and negative schedule offsets remain discoverable", () =>
        {
            foreach (var offset in new[] { -1440, 1440 })
            {
                var entry = new ScheduleEntry { Time = "12:00", OffsetMinutes = offset, Brightness = 0 };
                var settings = new AppSettings { Schedule = [entry] };
                var now = new DateTime(2026, 9, 29, 12, 0, 0);
                Equal(entry, ScheduleEvaluator.GetDue(settings, now.AddSeconds(-1), now, TimeZoneInfo.Utc).Single());
                Near(0, ScheduleEvaluator.GetCurrentLevels(settings, now, ["a"], TimeZoneInfo.Utc)["a"]);
            }
        }),
        ("Spring-forward schedules skip nonexistent wall-clock times", () =>
        {
            var zone = CreateDstZone();
            var entry = new ScheduleEntry { Time = "02:30", Brightness = 20 };
            Equal<DateTime?>(null, ScheduleEvaluator.GetOccurrence(entry, new(2026, 3, 8), zone: zone));
            Equal(new DateTime(2026, 3, 9, 2, 30, 0), ScheduleEvaluator.GetOccurrence(entry, new(2026, 3, 9), zone: zone));
            Equal(0, ScheduleEvaluator.GetDue(new AppSettings { Schedule = [entry] }, new(2026, 3, 8, 1, 59, 0), new(2026, 3, 8, 3, 1, 0), zone).Count);
        }),
        ("Schedule offsets do not create occurrences inside a spring-forward gap", () =>
        {
            var zone = CreateDstZone();
            foreach (var entry in new[]
            {
                new ScheduleEntry { Time = "01:30", OffsetMinutes = 60 },
                new ScheduleEntry { Time = "03:30", OffsetMinutes = -60 }
            })
            {
                Equal<DateTime?>(null, ScheduleEvaluator.GetOccurrence(entry, new(2026, 3, 8), zone: zone));
                Equal(0, ScheduleEvaluator.GetDue(new AppSettings { Schedule = [entry] }, new(2026, 3, 8, 1, 59, 0), new(2026, 3, 8, 3, 1, 0), zone).Count);
                Equal(new DateTime(2026, 3, 9, 2, 30, 0), ScheduleEvaluator.GetOccurrence(entry, new(2026, 3, 9), zone: zone));
            }
        }),
        ("CLI keeps decimal parsing stable across locales and isolates settings/demo commands", () =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                Near(20.5, CommandLine.Parse(["--All", "--Set=20.5"]).Set!.Value);
                Throws<ArgumentException>(() => CommandLine.Parse(["--All", "--Set=20,5"]));
                Equal((ushort)65535, CommandLine.Parse(["--All", "--VCP=0xFF:0xFFFF"]).Vcp!.Value);
                Equal(true, CommandLine.Parse(["--demo", "--settings"]).Settings);
                foreach (string[] arguments in new string[][]
                {
                    ["--demo", "--UDP", "--UseTime"], ["--settings", "--UseTime"],
                    ["--settings", "--All", "--Set=50"], ["--settings", "--background"],
                    ["--smoke-test", "--UDP", "--List"], ["--UDP", "--List", "--Overlay"]
                })
                    Throws<ArgumentException>(() => CommandLine.Parse(arguments));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        })
    ];

    private static TimeZoneInfo CreateDstZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2020, 1, 1), new(2030, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Test Eastern", TimeSpan.FromHours(-5), "Test Eastern", "Test Standard", "Test Daylight", [rule]);
    }

    private static void Near(double expected, double actual)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 0.000001)
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
