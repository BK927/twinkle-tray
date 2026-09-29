using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TwinkleTray.Core;
using TwinkleTray.WinUI.Services;

namespace TwinkleTray.WinUI;

internal sealed partial class AppController
{
    private async Task<IReadOnlyList<string>> VerifyRuntimeForSmokeTestAsync()
    {
        if (!IsSmokeTest || !IsDemo) throw new InvalidOperationException("Smoke checks require simulated displays.");
        var passed = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Smoke check failed: " + name); passed.Add(name); }
        const string id = "demo:external";
        MonitorSettings preferences = Settings.Monitors[id];
        _monitors = _monitors.Select(m => m.Id == id ? m with { HdrActive = false } : m).ToArray();
        preferences.MainControl = "gamma";
        await TrySetAsync(id, 25, throwOnError: true);
        Check(_monitors[0].GammaBrightness == 40 && Math.Abs(LogicalBrightness(_monitors[0]) - 25) < .01, "Gamma main control and inverse mapping");
        preferences.MainControl = "sdr";
        _monitors = _monitors.Select(m => m.Id == id ? m with { HdrActive = true } : m).ToArray();
        await TrySetAsync(id, 63, throwOnError: true);
        Check(_monitors[0].SdrBrightness == 63 && LogicalBrightness(_monitors[0]) == 63, "HDR SDR main control");
        preferences.MainControl = "brightness"; preferences.ExtendMinimum = true;
        _monitors = _monitors.Select(m => m.Id == id ? m with { HdrActive = false } : m).ToArray();
        await TrySetAsync(id, 10, throwOnError: true);
        Check(_monitors[0].Brightness == 0 && _monitors[0].GammaBrightness == 60 && LogicalBrightness(_monitors[0]) == 10, "Extended minimum routes hardware and gamma together");
        preferences.ExtendMinimum = false;
        await HandleHotkeyAsync(new HotkeyBinding { Actions = new() { new() { Type = "set", MonitorId = id, Value = 35 }, new() { Type = "offset", MonitorId = id, Value = 5 } } });
        Check(LogicalBrightness(_monitors[0]) == 40, "Ordered multi-action hotkeys");
        var cycle = new HotkeyBinding { Actions = new() { new() { Type = "cycle", MonitorId = id, Values = new() { 20, 80 } } } };
        await HandleHotkeyAsync(cycle);
        bool firstCycleMatchesUpstream = LogicalBrightness(_monitors[0]) == 80;
        await HandleHotkeyAsync(cycle);
        Check(firstCycleMatchesUpstream && LogicalBrightness(_monitors[0]) == 20, "Hotkey cycle preserves upstream initial advance and wraps once per invocation");
        await ApplyProfileAsync(new AppProfile { Brightness = new() { [id] = 55 }, OverlayType = "disabled" });
        Check(Math.Abs(LogicalBrightness(_monitors[0]) - 55) < .01, "Profile application");
        await QueryFeaturesAsync(id); await SetFeatureAsync(id, 0x62, 23);
        Check(Features(id).Single(f => f.Code == 0x62).Current == 23, "DDC feature routing");
        using (var request = JsonDocument.Parse("""{"type":"set","monitor":"demo:external","value":51}""")) await HandleUdpAsync(request.RootElement);
        Check(Math.Abs(LogicalBrightness(_monitors[0]) - 51) < .01, "UDP command compatibility");
        Settings.LinkedBrightness = true; Settings.HotkeysBreakLinkedLevels = false;
        await HandleHotkeyAsync(new HotkeyBinding { Actions = new() { new() { Type = "set", MonitorId = id, Value = 44 } } });
        Check(_monitors.All(m => Math.Abs(LogicalBrightness(m) - 44) < .01), "Hotkeys respect linked levels");
        Settings.LinkedBrightness = false;
        Settings.SmoothTransitions = true; Settings.TransitionSeconds = 2;
        await ApplyLevelsAsync(new Dictionary<string, double> { [id] = 90 });
        Check(_transitions.ContainsKey(id), "Smooth transition does not block the automation loop");
        await Task.Delay(300);
        await ApplyLevelsAsync(new Dictionary<string, double> { [id] = 58 }, false);
        await Task.Delay(350);
        Check(Math.Abs(LogicalBrightness(_monitors[0]) - 58) < .01 && !_transitions.ContainsKey(id), "Immediate profile restore cancels an earlier transition");
        Settings.SmoothTransitions = false;
        _dimmed = true; _beforeIdle[id] = 20; _idleRestoreDue = DateTime.Now.AddSeconds(-1);
        await TrySetAsync(id, 63, throwOnError: true);
        await UpdateIdleAsync(false);
        Check(!_dimmed && Math.Abs(LogicalBrightness(_monitors[0]) - 63) < .01, "Manual adjustment survives delayed idle restoration and pause");
        await VerifyAutomationForSmokeTestAsync(Check);

        using var reserve = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reserve.Client.LocalEndPoint!).Port; reserve.Close();
        int calls = 0;
        using var listener = new UdpControlService(port, false, "smoke-authentication-key", request =>
        {
            calls++;
            if (request.TryGetProperty("type", out var type) && type.GetString() == "fail") throw new InvalidOperationException("Simulated command failure");
            return Task.FromResult<object?>(new { ok = true });
        });
        using var client = new UdpClient(); client.Connect(IPAddress.Loopback, listener.Port);
        await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { key = "wrong", type = "list" }));
        // The invalid document must not kill the receiver or run a command.
        await client.SendAsync("[]"u8.ToArray());
        await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { key = "smoke-authentication-key", type = "list" }));
        var response = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check(calls == 1 && JsonDocument.Parse(response.Buffer).RootElement.GetProperty("ok").GetBoolean(), "UDP rejects unauthenticated requests and survives malformed JSON");
        await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { key = "smoke-authentication-key", type = "fail" }));
        var failedResponse = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        bool reportedFailure = JsonDocument.Parse(failedResponse.Buffer).RootElement.GetProperty("error").GetString() == "Simulated command failure";
        await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { key = "smoke-authentication-key", type = "list" }));
        var recoveredResponse = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check(reportedFailure && calls == 3 && JsonDocument.Parse(recoveredResponse.Buffer).RootElement.GetProperty("ok").GetBoolean(), "UDP reports command failures and accepts the next valid request");
        await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { key = "smoke-authentication-key", type = "list", padding = new string('x', 17000) }));
        await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { key = "smoke-authentication-key", type = "list" }));
        var afterOversize = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check(calls == 4 && JsonDocument.Parse(afterOversize.Buffer).RootElement.GetProperty("ok").GetBoolean(), "UDP drops oversized datagrams without stopping its receiver");
        VerifyUpdateRollback(Check);
        await VerifyUpdateDownloadAsync(Check);
        Settings.Monitors[id] = new MonitorSettings { ShowContrast = true, Features = new() { [0x62] = new FeatureSettings { Enabled = true, Name = "Volume" } }, Calibration = new() { new() { Input = 50, Output = 50 } } };
        Settings.Profiles.Add(new AppProfile { Name = "Smoke profile", Path = "demo-only-app.exe", Brightness = new() { [id] = 50 }, Enabled = false });
        Settings.Schedule.Add(new ScheduleEntry { Event = "sunset", OffsetMinutes = -30, IndividualBrightness = new() { [id] = 30 }, Enabled = false });
        Settings.Hotkeys.Add(new HotkeyBinding { Enabled = false, Actions = new() { new() { Type = "cycle", Values = new() { 25, 75 } }, new() { Type = "profile", ProfileId = Settings.Profiles[0].Id }, new() { Type = "vcp", Vcp = 0x62, Value = 50 } } });
        _monitors = CliRunner.DemoMonitors;
        ShowOverlay();
        return passed;
    }

    private async Task VerifyAutomationForSmokeTestAsync(Action<bool, string> check)
    {
        const string external = "demo:external", internalDisplay = "demo:internal";
        var originalSettings = Settings;
        var originalMonitors = _monitors;
        var failures = new List<string>();
        var observations = new List<object>();
        DateTime start = new(2026, 9, 29, 7, 59, 0, DateTimeKind.Local);
        double Level(string id) => LogicalBrightness(_monitors.Single(m => m.Id == id));
        void Observe(bool condition, string name, string details)
        {
            observations.Add(new { Name = name, Passed = condition, Details = details });
            if (condition) check(true, name); else failures.Add(name + ": " + details);
        }
        AutomationObservation Input(DateTime now, bool locked = false, string? foreground = null, int idleSeconds = 0, bool fullscreen = false, bool media = false)
            => new(now, locked, foreground, TimeSpan.FromSeconds(idleSeconds), fullscreen, media);
        async Task ResetAsync()
        {
            foreach (var transition in _transitions.Values.ToArray()) transition.Cancel();
            await Task.Delay(30);
            Settings = new AppSettings { ShowOverlay = false, DisableOnLockScreen = true };
            _monitors = CliRunner.DemoMonitors;
            _activeProfile = null; _beforeProfile.Clear(); _beforeIdle.Clear(); _interruptedLevels.Clear(); _dimmed = false;
            _automationPaused = false; _schedulePending = false; _idleRestoreDue = null;
            _lastSchedule = start; _lastSensor = start.AddHours(-1); _lastAutomationError = null;
            await TrySetAsync(external, 20, throwOnError: true);
            await TrySetAsync(internalDisplay, 60, throwOnError: true);
        }
        void Schedule(int brightness = 90) => Settings.Schedule.Add(new ScheduleEntry { Time = "08:00", Brightness = brightness, MonitorId = external });
        AppProfile Profile(int brightness = 80)
        {
            var profile = new AppProfile { Enabled = true, Path = "regression-fixture.exe", RestorePrevious = true, OverlayType = "disabled", Brightness = new() { [external] = brightness } };
            Settings.Profiles.Add(profile);
            return profile;
        }
        try
        {
            foreach (bool locked in new[] { false, true })
            {
                await ResetAsync(); Schedule(35);
                _automationPaused = !locked;
                await TickAsync(Input(start.AddMinutes(2), locked));
                bool held = Level(external) == 20 && _schedulePending;
                _automationPaused = false;
                await TickAsync(Input(start.AddMinutes(3)));
                Observe(held && Level(external) == 35 && !_schedulePending,
                    locked ? "Schedule due while locked catches up after unlock" : "Schedule due while paused catches up after resume",
                    $"Held={held}; restored={Level(external)}; pending={_schedulePending}");
            }

            foreach (bool locked in new[] { false, true })
            {
                await ResetAsync(); Schedule(); Settings.SmoothTransitions = true; Settings.TransitionSeconds = 1;
                await TickAsync(Input(start.AddMinutes(2)));
                await Task.Delay(280);
                _automationPaused = !locked;
                await TickAsync(Input(start.AddMinutes(2).AddSeconds(1), locked));
                await Task.Delay(40);
                double interruptedLevel = Level(external);
                _automationPaused = false;
                await TickAsync(Input(start.AddMinutes(3)));
                await Task.Delay(1150);
                Observe(Math.Abs(Level(external) - 90) < .01,
                    locked ? "A schedule transition completes after lock cancellation" : "A schedule transition completes after pause cancellation",
                    $"Interrupted={interruptedLevel}; resumed={Level(external)}");
            }

            foreach (bool locked in new[] { false, true })
            {
                await ResetAsync(); Profile(90); Settings.SmoothTransitions = true; Settings.TransitionSeconds = 1;
                await TickAsync(Input(start.AddMinutes(1), foreground: @"C:\Fixtures\regression-fixture.exe"));
                await Task.Delay(280);
                _automationPaused = !locked;
                await TickAsync(Input(start.AddMinutes(1).AddSeconds(1), locked, @"C:\Fixtures\regression-fixture.exe"));
                await Task.Delay(40);
                _automationPaused = false;
                await TickAsync(Input(start.AddMinutes(2), foreground: @"C:\Fixtures\regression-fixture.exe"));
                await Task.Delay(1150);
                Observe(Math.Abs(Level(external) - 90) < .01,
                    locked ? "An active profile transition resumes after unlock without a foreground change" : "An active profile transition resumes after pause without a foreground change",
                    $"Resumed={Level(external)}");
            }

            await ResetAsync(); Schedule(); Settings.SmoothTransitions = true; Settings.TransitionSeconds = 1;
            await TickAsync(Input(start.AddMinutes(2)));
            await Task.Delay(280);
            _automationPaused = true;
            await TickAsync(Input(start.AddMinutes(2).AddSeconds(1)));
            await Task.Delay(40);
            await TrySetAsync(external, 63, throwOnError: true);
            _automationPaused = false;
            await TickAsync(Input(start.AddMinutes(3)));
            await Task.Delay(1150);
            Observe(Level(external) == 63 && !_interruptedLevels.ContainsKey(external),
                "A manual edit while paused overrides the canceled automatic target", $"Manual level after resume={Level(external)}");

            await ResetAsync(); Schedule(); Settings.SmoothTransitions = true; Settings.TransitionSeconds = 1;
            await TickAsync(Input(start.AddMinutes(2)));
            _automationPaused = true;
            await TickAsync(Input(start.AddMinutes(2).AddSeconds(1)));
            await Task.Delay(40);
            bool profileHadInterruptedTarget = _interruptedLevels.GetValueOrDefault(external) == 90;
            double sameProfileLevel = Level(external);
            await ApplyProfileAsync(new AppProfile { OverlayType = "disabled", Brightness = new() { [external] = (int)sameProfileLevel } });
            _automationPaused = false;
            await TickAsync(Input(start.AddMinutes(3)));
            await Task.Delay(1150);
            Observe(profileHadInterruptedTarget && sameProfileLevel == 20 && Level(external) == sameProfileLevel && !_interruptedLevels.ContainsKey(external),
                "A manual profile matching the current level discards an interrupted automatic target",
                $"Interrupted target existed={profileHadInterruptedTarget}; chosen={sameProfileLevel}; after resume={Level(external)}");

            await ResetAsync(); Schedule(); Settings.SmoothTransitions = true; Settings.TransitionSeconds = 1;
            await TickAsync(Input(start.AddMinutes(2)));
            _automationPaused = true;
            await TickAsync(Input(start.AddMinutes(2).AddSeconds(1)));
            await Task.Delay(40);
            bool useTimeHadInterruptedTarget = _interruptedLevels.GetValueOrDefault(external) == 90;
            Settings.Schedule[0].Brightness = 35;
            string useTimeAcknowledgement = await HandleCommandAsync(CommandLine.Parse(["--demo", "--UseTime"]));
            double requestedScheduleLevel = Level(external);
            _automationPaused = false;
            await TickAsync(Input(start.AddMinutes(3)));
            await Task.Delay(1150);
            Observe(useTimeHadInterruptedTarget && useTimeAcknowledgement == "OK" && requestedScheduleLevel == 35 && Level(external) == 35 && !_interruptedLevels.ContainsKey(external),
                "An explicit UseTime command replaces an interrupted automatic target",
                $"Interrupted target existed={useTimeHadInterruptedTarget}; requested={requestedScheduleLevel}; after resume={Level(external)}");

            await ResetAsync(); Schedule(); Settings.SmoothTransitions = true; Settings.TransitionSeconds = 1;
            await TickAsync(Input(start.AddMinutes(2)));
            await Task.Delay(280);
            _automationPaused = true;
            await TickAsync(Input(start.AddMinutes(2).AddSeconds(1)));
            await Task.Delay(40);
            string rawAcknowledgement = await HandleCommandAsync(CommandLine.Parse(["--demo", "--MonitorID=demo:external", "--VCP=0x10:63"]));
            double rawManual = Level(external);
            _automationPaused = false;
            await TickAsync(Input(start.AddMinutes(3)));
            await Task.Delay(1150);
            Observe(rawAcknowledgement == "OK" && rawManual == 63 && Level(external) == 63 && !_interruptedLevels.ContainsKey(external),
                "Raw VCP brightness through CLI survives automatic-control resume", $"Raw manual={rawManual}; after resume={Level(external)}");

            await ResetAsync(); Settings.IdleEnabled = true; Settings.IdleMinutes = 0; Settings.IdleSeconds = 1; Settings.IdleBrightness = 5;
            await TickAsync(Input(start.AddMinutes(1), idleSeconds: 5));
            bool rawWasIdle = _dimmed && Level(external) == 5;
            await SetFeatureAsync(external, 0x10, 63);
            double rawIdleRestore = _beforeIdle.GetValueOrDefault(external);
            await TickAsync(Input(start.AddMinutes(2)));
            Observe(rawWasIdle && rawIdleRestore == 63 && !_dimmed && Level(external) == 63,
                "Raw VCP brightness while idle becomes the restored manual level", $"Was idle={rawWasIdle}; restoration target={rawIdleRestore}; restored={Level(external)}");

            await ResetAsync(); Settings.Monitors[external] = new MonitorSettings { MainControl = "gamma" };
            _monitors = _monitors.Select(m => m.Id == external ? m with { HdrActive = false } : m).ToArray();
            await TrySetAsync(external, 20, throwOnError: true);
            Schedule(); Settings.SmoothTransitions = true; Settings.TransitionSeconds = 1;
            await TickAsync(Input(start.AddMinutes(2)));
            await Task.Delay(280);
            _automationPaused = true;
            await TickAsync(Input(start.AddMinutes(2).AddSeconds(1)));
            await Task.Delay(40);
            await HandleHotkeyAsync(new HotkeyBinding { Actions = new() { new() { Type = "set", Target = "gamma", MonitorId = external, Value = 60 } } });
            double chosenGammaLogical = Level(external);
            _automationPaused = false;
            await TickAsync(Input(start.AddMinutes(3)));
            await Task.Delay(1150);
            Observe(Math.Abs(chosenGammaLogical - 50) < .01 && Math.Abs(Level(external) - 50) < .01 && !_interruptedLevels.ContainsKey(external),
                "A direct gamma hotkey while paused retains its chosen logical brightness", $"Chosen logical={chosenGammaLogical}; after resume={Level(external)}");

            await ResetAsync(); Schedule(); Settings.SmoothTransitions = true; Settings.TransitionSeconds = 1;
            await TickAsync(Input(start.AddMinutes(2)));
            await Task.Delay(280);
            _automationPaused = true;
            await TickAsync(Input(start.AddMinutes(2).AddSeconds(1)));
            // Do not yield to the canceled transition's cleanup before resuming.
            _automationPaused = false;
            await TickAsync(Input(start.AddMinutes(2).AddSeconds(2)));
            await Task.Delay(1150);
            Observe(Math.Abs(Level(external) - 90) < .01 && !_transitions.ContainsKey(external),
                "Immediate pause and resume restarts a transition before prior cleanup", $"Completed level={Level(external)}; transitioning={_transitions.ContainsKey(external)}");

            await ResetAsync(); Schedule(); Settings.SmoothTransitions = true; Settings.TransitionSeconds = 1;
            Settings.IdleEnabled = true; Settings.IdleMinutes = 0; Settings.IdleSeconds = 1; Settings.IdleBrightness = 5;
            await TickAsync(Input(start.AddMinutes(2), idleSeconds: 5));
            bool dimmed = _dimmed && Level(external) == 5;
            await Task.Delay(40);
            await TickAsync(Input(start.AddMinutes(3)));
            Observe(dimmed && !_dimmed && Math.Abs(Level(external) - 90) < .01,
                "Idle dimming preserves a schedule target started in the same tick", $"Dimmed={dimmed}; restored={Level(external)}");

            await ResetAsync(); Profile();
            await TickAsync(Input(start.AddMinutes(1), foreground: @"C:\Fixtures\regression-fixture.exe"));
            bool appliedProfile = Level(external) == 80 && _activeProfile is not null;
            await TickAsync(Input(start.AddMinutes(2), foreground: @"C:\Fixtures\other.exe"));
            Observe(appliedProfile && _activeProfile is null && Level(external) == 20 && Level(internalDisplay) == 60,
                "Leaving a foreground profile restores the previous per-display levels", $"Applied={appliedProfile}; restored={Level(external)}/{Level(internalDisplay)}");

            await ResetAsync(); Profile(); Schedule(25);
            await TickAsync(Input(start.AddMinutes(2), foreground: @"C:\Fixtures\regression-fixture.exe"));
            bool deferred = _schedulePending && Level(external) == 80;
            await TickAsync(Input(start.AddMinutes(3), foreground: @"C:\Fixtures\other.exe"));
            Observe(deferred && Level(external) == 25 && !_schedulePending,
                "A schedule deferred by an app profile catches up after profile exit", $"Deferred={deferred}; final={Level(external)}");

            await ResetAsync();
            Settings.Schedule.Add(new ScheduleEntry { Time = "08:00", Brightness = 25, MonitorId = "all" });
            Settings.Sensor = new SensorSettings { Enabled = true, Provider = "fake", FakeLux = 50, PollSeconds = 1,
                Monitors = new() { [external] = new SensorMonitorSettings { Enabled = true, MinLux = 0, MaxLux = 100, MinBrightness = 0, MaxBrightness = 100 } } };
            await TickAsync(Input(start.AddMinutes(2)));
            Observe(Level(external) == 50 && Level(internalDisplay) == 25,
                "The light sensor overrides schedules only for its enabled displays", $"Sensor/schedule={Level(external)}/{Level(internalDisplay)}");
            Profile(); Settings.Sensor.FakeLux = 75;
            await TickAsync(Input(start.AddMinutes(3), foreground: @"C:\Fixtures\regression-fixture.exe"));
            bool profileDominated = Level(external) == 80;
            await TickAsync(Input(start.AddMinutes(4), foreground: @"C:\Fixtures\other.exe"));
            Observe(profileDominated && Level(external) == 75,
                "Foreground profiles override the sensor and release it on exit", $"Profile={profileDominated}; sensor resumed={Level(external)}");
            Settings.Sensor.FakeLux = double.NaN;
            await TickAsync(Input(start.AddMinutes(5)));
            bool errorContained = !_tickBusy && _lastAutomationError is not null && Level(external) == 75;
            Settings.Sensor.FakeLux = 40;
            await TickAsync(Input(start.AddMinutes(6)));
            Observe(errorContained && !_tickBusy && _lastAutomationError is null && Level(external) == 40,
                "A sensor calculation error leaves the automation loop recoverable", $"Contained={errorContained}; recovered={Level(external)}");

            await ResetAsync(); Settings.IdleEnabled = true; Settings.IdleMinutes = 0; Settings.IdleSeconds = 1; Settings.IdleBrightness = 5;
            Settings.IdleCheckFullscreen = true; Settings.IdleCheckMedia = true;
            await TickAsync(Input(start.AddMinutes(1), idleSeconds: 10, fullscreen: true));
            bool fullscreenBlocked = !_dimmed && Level(external) == 20;
            await TickAsync(Input(start.AddMinutes(2), idleSeconds: 10, media: true));
            bool mediaBlocked = !_dimmed && Level(external) == 20;
            await TickAsync(Input(start.AddMinutes(3), idleSeconds: 10));
            Observe(fullscreenBlocked && mediaBlocked && _dimmed && Level(external) == 5,
                "Fullscreen and media suppress idle dimming until both blockers clear", $"Fullscreen={fullscreenBlocked}; media={mediaBlocked}; idle={_dimmed}");
        }
        finally
        {
            foreach (var transition in _transitions.Values.ToArray()) transition.Cancel();
            await Task.Delay(30);
            Settings = originalSettings; _monitors = originalMonitors;
            _activeProfile = null; _beforeProfile.Clear(); _beforeIdle.Clear(); _interruptedLevels.Clear(); _dimmed = false;
            _automationPaused = false; _schedulePending = false; _idleRestoreDue = null; _lastAutomationError = null;
            _lastSchedule = DateTime.Now;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "automation-regression.json"), JsonSerializer.Serialize(new { Passed = failures.Count == 0, Observations = observations, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failures.Count > 0) throw new InvalidOperationException("Automation regressions: " + string.Join("; ", failures));
    }

    private static void VerifyUpdateRollback(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "test-fixtures", Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source"), target = Path.Combine(root, "target"), backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(source, "a.txt"), "new-a"); File.WriteAllText(Path.Combine(source, "b.txt"), "new-b");
            File.WriteAllText(Path.Combine(target, "a.txt"), "old-a"); File.WriteAllText(Path.Combine(target, "b.txt"), "old-b");
            using (var locked = new FileStream(Path.Combine(target, "b.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                bool failed = false;
                try { UpdateService.InstallFiles(source, target, backup); } catch (IOException) { failed = true; }
                check(failed && File.ReadAllText(Path.Combine(target, "a.txt")) == "old-a", "Failed portable update rolls back previously replaced files");
            }
            UpdateService.InstallFiles(source, target, backup);
            check(File.ReadAllText(Path.Combine(target, "a.txt")) == "new-a" && File.ReadAllText(Path.Combine(target, "b.txt")) == "new-b", "Portable update replaces a fixture installation");
        }
        finally
        {
            string resolved = Path.GetFullPath(root), expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-fixtures")) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, true);
        }
    }
}
