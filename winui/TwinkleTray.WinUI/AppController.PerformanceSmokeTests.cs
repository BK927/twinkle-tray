using TwinkleTray.Core;
using TwinkleTray.WinUI.Services;

namespace TwinkleTray.WinUI;

internal sealed partial class AppController
{
    private async Task VerifyBackgroundPerformanceForSmokeTestAsync(Action<bool, string> check)
    {
        if (!IsSmokeTest || !IsDemo) throw new InvalidOperationException("Performance checks require simulated displays.");
        const string external = "demo:external";
        var originalSettings = Settings;
        var originalMonitors = _monitors;
        var originalProfile = _activeProfile;
        var originalBeforeProfile = _beforeProfile.ToDictionary();
        bool originalDirty = _brightnessDirty, originalPending = _schedulePending;
        DateTime originalLastSchedule = _lastSchedule;
        string? foreground = @"C:\Fixtures\performance-fixture.exe";
        int queries = 0;
        string? GetForeground() { queries++; return foreground; }
        double Level() => LogicalBrightness(_monitors.Single(m => m.Id == external));
        try
        {
            Settings = new AppSettings { ShowOverlay = false, DisableOnLockScreen = false };
            _monitors = CliRunner.DemoMonitors;
            _activeProfile = null; _beforeProfile.Clear();
            await TrySetAsync(external, 20, throwOnError: true);
            var profile = new AppProfile
            {
                Enabled = false, Path = "performance-fixture.exe", RestorePrevious = true,
                OverlayType = "disabled", Brightness = new() { [external] = 80 }
            };
            Settings.Profiles.Add(profile);
            Settings.Profiles.Add(new AppProfile { Enabled = true, Path = "  " });
            for (int i = 0; i < 600; i++) await UpdateForegroundProfileAsync(GetForeground);
            check(queries == 0 && _activeProfile is null && Level() == 20,
                "600 inactive profile ticks perform no foreground-process queries or brightness changes");

            profile.Enabled = true;
            await UpdateForegroundProfileAsync(GetForeground);
            check(queries == 1 && _activeProfile == profile && Level() == 80,
                "Enabling a profile on existing settings takes effect on the next tick");

            profile.Enabled = false;
            foreground = @"C:\Fixtures\TwinkleTray.WinUI.exe";
            await UpdateForegroundProfileAsync(GetForeground);
            bool heldByOwnWindow = _activeProfile == profile && Level() == 80;
            foreground = @"C:\Fixtures\other.exe";
            await UpdateForegroundProfileAsync(GetForeground);
            int queriesAfterRestoration = queries;
            await UpdateForegroundProfileAsync(GetForeground);
            check(heldByOwnWindow && _activeProfile is null && Level() == 20 && queries == queriesAfterRestoration,
                "Disabling an active profile preserves own-window behavior and restores before stopping queries");

            profile.Enabled = true;
            foreground = @"C:\Fixtures\performance-fixture.exe";
            await UpdateForegroundProfileAsync(GetForeground);
            Settings.Profiles.Clear();
            foreground = null;
            await UpdateForegroundProfileAsync(GetForeground);
            queriesAfterRestoration = queries;
            await UpdateForegroundProfileAsync(GetForeground);
            check(_activeProfile is null && Level() == 20 && queries == queriesAfterRestoration,
                "Removing an active profile still restores its captured level when foreground is unavailable");

            DateTime start = new(2026, 9, 30, 7, 59, 0, DateTimeKind.Local);
            var schedule = new ScheduleEntry { Enabled = false, Time = "08:00", Brightness = 60, MonitorId = external };
            Settings.Schedule.Add(schedule);
            Settings.ScheduleInterpolation = true;
            _schedulePending = true;
            _lastSchedule = start;
            await TickAsync(new AutomationObservation(start.AddMinutes(2), false, null, TimeSpan.Zero, false, false));
            bool clearedWithoutApplying = !_schedulePending && _lastSchedule == start.AddMinutes(2) && Level() == 20;
            Settings.ScheduleInterpolation = false;
            schedule.Enabled = true;
            await TickAsync(new AutomationObservation(start.AddMinutes(3), false, null, TimeSpan.Zero, false, false));
            check(clearedWithoutApplying && !_schedulePending && Level() == 20,
                "Disabled schedules clear pending work and advance time without replaying past events when enabled");
        }
        finally
        {
            Settings = originalSettings;
            _monitors = originalMonitors;
            _activeProfile = originalProfile;
            _beforeProfile.Clear();
            foreach (var pair in originalBeforeProfile) _beforeProfile.Add(pair.Key, pair.Value);
            _brightnessDirty = originalDirty; _schedulePending = originalPending;
            _lastSchedule = originalLastSchedule;
        }
    }
}
