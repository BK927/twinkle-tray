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

        using var reserve = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reserve.Client.LocalEndPoint!).Port; reserve.Close();
        int calls = 0;
        using var listener = new UdpControlService(port, false, "smoke-authentication-key", request => { calls++; return Task.FromResult<object?>(new { ok = true }); });
        using var client = new UdpClient(); client.Connect(IPAddress.Loopback, listener.Port);
        await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { key = "wrong", type = "list" }));
        // The invalid document must not kill the receiver or run a command.
        await client.SendAsync("[]"u8.ToArray());
        await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { key = "smoke-authentication-key", type = "list" }));
        var response = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check(calls == 1 && JsonDocument.Parse(response.Buffer).RootElement.GetProperty("ok").GetBoolean(), "UDP rejects unauthenticated requests and survives malformed JSON");
        VerifyUpdateRollback(Check);
        Settings.Monitors[id] = new MonitorSettings { ShowContrast = true, Features = new() { [0x62] = new FeatureSettings { Enabled = true, Name = "Volume" } }, Calibration = new() { new() { Input = 50, Output = 50 } } };
        Settings.Profiles.Add(new AppProfile { Name = "Smoke profile", Path = "demo-only-app.exe", Brightness = new() { [id] = 50 }, Enabled = false });
        Settings.Schedule.Add(new ScheduleEntry { Event = "sunset", OffsetMinutes = -30, IndividualBrightness = new() { [id] = 30 }, Enabled = false });
        Settings.Hotkeys.Add(new HotkeyBinding { Enabled = false, Actions = new() { new() { Type = "cycle", Values = new() { 25, 75 } }, new() { Type = "profile", ProfileId = Settings.Profiles[0].Id }, new() { Type = "vcp", Vcp = 0x62, Value = 50 } } });
        _monitors = CliRunner.DemoMonitors;
        ShowOverlay();
        return passed;
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
