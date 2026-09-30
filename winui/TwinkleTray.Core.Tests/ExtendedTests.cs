using System.Text.Json;
using TwinkleTray.Core;

internal static class ExtendedTests
{
    public static (string Name, Action Run)[] All =>
    [
        ("Calibration preserves piecewise points and calibrated endpoints", () =>
        {
            var settings = new MonitorSettings { MinBrightness = 20, MaxBrightness = 80, Calibration = [new() { Input = 50, Output = 70 }] };
            Near(20, BrightnessCalibration.ToHardware(0, settings));
            Near(45, BrightnessCalibration.ToHardware(25, settings));
            Near(75, BrightnessCalibration.ToHardware(75, settings));
            Near(80, BrightnessCalibration.ToHardware(100, settings));
            for (int logical = 0; logical <= 100; logical++) Near(logical, BrightnessCalibration.ToLogical(BrightnessCalibration.ToHardware(logical, settings), settings));
        }),
        ("Calibration handles duplicate points, plateaus and out-of-range reads", () =>
        {
            var settings = new MonitorSettings { MinBrightness = 20, MaxBrightness = 80, Calibration = [new() { Input = 50, Output = 30 }, new() { Input = 50, Output = 20 }] };
            Near(20, BrightnessCalibration.ToHardware(50, settings));
            Near(25, BrightnessCalibration.ToLogical(20, settings));
            Near(0, BrightnessCalibration.ToLogical(5, settings));
            Near(100, BrightnessCalibration.ToLogical(99, settings));
            Equal(30d, settings.Calibration[0].Output);
        }),
        ("Extended minimum hands off continuously between gamma and backlight", () =>
        {
            var settings = new MonitorSettings { MinBrightness = 10, MaxBrightness = 90, ExtendMinimum = true, ExtendMinimumBreakpoint = 20 };
            var dark = BrightnessCalibration.GetExtendedLevels(0, settings);
            Near(10, dark.Hardware); Near(20, dark.Gamma);
            var threshold = BrightnessCalibration.GetExtendedLevels(20, settings);
            Near(10, threshold.Hardware); Near(100, threshold.Gamma);
            foreach (double logical in new double[] { 0, 5, 19, 20, 60, 100 })
            {
                var levels = BrightnessCalibration.GetExtendedLevels(logical, settings);
                Near(logical, BrightnessCalibration.FromExtendedLevels(levels.Hardware, levels.Gamma, settings));
            }
            Equal(60, BrightnessCalibration.LinkedFeatureValue(25, new() { Min = 20, Max = 100, MaxVisual = 50 }));
        }),
        ("Solar calculator matches upstream SunCalc 1.9 reference fixtures", () =>
        {
            // Public astronomical reference values: https://github.com/mourner/suncalc/blob/v1.9.0/test.js
            var expected = new Dictionary<string, string>
            {
                ["solarNoon"] = "2013-03-05T10:10:57", ["nadir"] = "2013-03-04T22:10:57",
                ["sunrise"] = "2013-03-05T04:34:56", ["sunset"] = "2013-03-05T15:46:57",
                ["sunriseEnd"] = "2013-03-05T04:38:19", ["sunsetStart"] = "2013-03-05T15:43:34",
                ["dawn"] = "2013-03-05T04:02:17", ["dusk"] = "2013-03-05T16:19:36",
                ["nauticalDawn"] = "2013-03-05T03:24:31", ["nauticalDusk"] = "2013-03-05T16:57:22",
                ["nightEnd"] = "2013-03-05T02:46:17", ["night"] = "2013-03-05T17:35:36",
                ["goldenHourEnd"] = "2013-03-05T05:19:01", ["goldenHour"] = "2013-03-05T15:02:52"
            };
            foreach (var (name, time) in expected)
            {
                var actual = SolarCalculator.GetEvent(new(2013, 3, 5), 50.5, 30.5, name, TimeZoneInfo.Utc);
                Equal(true, actual.HasValue);
                Equal(true, Math.Abs((actual!.Value - DateTime.Parse(time, System.Globalization.CultureInfo.InvariantCulture)).TotalSeconds) < 1);
            }
        }),
        ("Solar calculator skips polar events and applies caller time zone", () =>
        {
            Equal<DateTime?>(null, SolarCalculator.GetEvent(new(2026, 6, 21), 89, 0, "sunset", TimeZoneInfo.Utc));
            Equal<DateTime?>(null, SolarCalculator.GetEvent(new(2026, 12, 21), 89, 0, "sunrise", TimeZoneInfo.Utc));
            var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
            var utc = SolarCalculator.GetEvent(new(2013, 3, 5), 50.5, 30.5, "sunrise", TimeZoneInfo.Utc);
            var local = SolarCalculator.GetEvent(new(2013, 3, 5), 50.5, 30.5, "sunrise", zone);
            Near(2, (local!.Value - utc!.Value).TotalHours);
            Throws<ArgumentOutOfRangeException>(() => SolarCalculator.GetEvent(DateTime.Today, 91, 0, "sunrise"));
        }),
        ("Solar schedules and positive offsets cross midnight correctly", () =>
        {
            var settings = new AppSettings { Latitude = 50.5, Longitude = 30.5, Schedule = [new() { Event = "sunrise", OffsetMinutes = 10 }, new() { Time = "23:50", OffsetMinutes = 20 }] };
            var sunriseDue = ScheduleEvaluator.GetDue(settings, new(2013, 3, 5, 4, 40, 0), new(2013, 3, 5, 4, 50, 0), TimeZoneInfo.Utc);
            Equal(1, sunriseDue.Count); Equal("sunrise", sunriseDue[0].Event);
            var midnightDue = ScheduleEvaluator.GetDue(settings, new(2013, 3, 5, 23, 59, 0), new(2013, 3, 6, 0, 11, 0), TimeZoneInfo.Utc);
            Equal(1, midnightDue.Count); Equal("23:50", midnightDue[0].Time);
        }),
        ("Continuous schedules interpolate through midnight independently per monitor", () =>
        {
            var settings = new AppSettings { ScheduleInterpolation = true, Schedule =
            [
                new() { Time = "20:00", IndividualBrightness = new() { ["a"] = 20, ["b"] = 80 } },
                new() { Time = "08:00", IndividualBrightness = new() { ["a"] = 80, ["b"] = 20 } }
            ] };
            var levels = ScheduleEvaluator.GetCurrentLevels(settings, new(2026, 9, 29, 2, 0, 0), ["a", "b", "absent"], TimeZoneInfo.Utc);
            Equal(2, levels.Count); Near(50, levels["a"]); Near(50, levels["b"]);
            settings.ScheduleInterpolation = false;
            Near(20, ScheduleEvaluator.GetCurrentLevels(settings, new(2026, 9, 29, 2, 0, 0), ["a"], TimeZoneInfo.Utc)["a"]);
        }),
        ("Per-monitor schedules do not interpolate toward unrelated targets", () =>
        {
            var settings = new AppSettings { ScheduleInterpolation = true, Schedule =
            [new() { Time = "06:00", MonitorId = "a", Brightness = 20 }, new() { Time = "08:00", MonitorId = "b", Brightness = 90 }, new() { Time = "18:00", MonitorId = "a", Brightness = 80 }] };
            Near(50, ScheduleEvaluator.GetCurrentLevels(settings, new(2026, 9, 29, 12, 0, 0), ["a"], TimeZoneInfo.Utc)["a"]);
        }),
        ("Sensor curve clamps thresholds and rejects invalid sensor readings", () =>
        {
            var settings = new SensorMonitorSettings { MinLux = 5, MaxLux = 255, MinBrightness = 10, MaxBrightness = 90 };
            Equal(10, SensorCurve.ToBrightness(0, settings)); Equal(50, SensorCurve.ToBrightness(130, settings)); Equal(90, SensorCurve.ToBrightness(1000, settings));
            Throws<ArgumentOutOfRangeException>(() => SensorCurve.ToBrightness(double.NaN, settings));
            settings.MaxLux = 5; Throws<ArgumentException>(() => SensorCurve.ToBrightness(5, settings));
        }),
        ("Profiles preserve case-insensitive substring and last-match behavior", () =>
        {
            AppProfile[] profiles = [new() { Id = "broad", Enabled = true, Path = "games, editor.exe" }, new() { Id = "specific", Enabled = true, Path = "games/special, ,  " }, new() { Id = "disabled", Enabled = false, Path = "special" }];
            Equal("specific", ProfileResolver.Match(@"C:\GAMES\SPECIAL\app.exe", profiles)?.Id);
            Equal("broad", ProfileResolver.Match(@"D:\Tools\EDITOR.EXE", profiles)?.Id);
            Equal<AppProfile?>(null, ProfileResolver.Match(@"C:\Windows\explorer.exe", profiles));
            Equal<AppProfile?>(null, ProfileResolver.Match("", profiles));
        }),
        ("Importer maps identities and preserves calibration, schedules and action lists", () =>
        {
            const string json = """
            {
              "theme":"dark","linkedLevelsActive":true,"adjustmentTimeSpeed":"instant",
              "names":{"OLD":"Office"},"remaps":{"OLD":{"min":10,"max":90,"calibration":[{"input":50,"output":70}]}},
              "order":["OLD"],"monitorFeatures":{"OLD":{"0x12":true}},"monitorFeaturesSettings":{"OLD":{"0x12":{"min":5,"max":80,"linked":true,"maxVisual":75}}},
              "adjustmentTimeIndividualDisplays":true,"adjustmentTimes":[{"time":"22:00","brightness":0,"monitors":{"OLD":25}}],
              "hotkeys":[{"accelerator":"Control+Shift+Up","actions":[{"type":"offset","allMonitors":true,"target":"brightness","value":5},{"type":"cycle","monitors":{"OLD":true},"target":"volume","values":[0,25,50]}]}],
              "profiles":[{"id":"p","name":"Game","path":"game.exe","setBrightness":true,"monitors":{"OLD":60},"showInMenu":true}]
            }
            """;
            var result = UpstreamSettingsImporter.Import(json, new Dictionary<string, string> { ["OLD"] = "native" });
            Equal(0, result.UnmappedMonitorIds.Count); Equal(json, result.Settings.ImportedUpstreamJson);
            Equal("Office", result.Settings.Monitors["native"].Name); Equal(70d, result.Settings.Monitors["native"].Calibration[0].Output);
            Equal(true, result.Settings.Monitors["native"].Features[0x12].LinkedToBrightness);
            Equal(25, result.Settings.Schedule[0].IndividualBrightness["native"]);
            Equal(0, result.Settings.Schedule[0].Brightness);
            Equal((uint)6, result.Settings.Hotkeys[0].Modifiers); Equal((uint)0x26, result.Settings.Hotkeys[0].VirtualKey);
            Equal(2, result.Settings.Hotkeys[0].Actions.Count); Equal((byte)0x62, result.Settings.Hotkeys[0].Actions[1].Vcp);
            Equal("vcp", result.Settings.Hotkeys[0].Actions[1].Target); Equal(60, result.Settings.Profiles[0].Brightness["native"]);
        }),
        ("Importer leaves unresolved references inert and reports unsupported settings", () =>
        {
            const string json = """{"names":{"OLD":"Unknown"},"adjustmentTimeSpeed":"instant","overrideTaskbarGap":42,"adjustmentTimeIndividualDisplays":true,"adjustmentTimes":[{"time":"22:00","monitors":{"OLD":25}}]}""";
            var result = UpstreamSettingsImporter.Import(json);
            Equal("OLD", result.UnmappedMonitorIds.Single());
            Equal(true, result.Settings.Monitors.ContainsKey("unresolved:OLD"));
            Equal(true, result.Settings.Schedule[0].IndividualBrightness.ContainsKey("unresolved:OLD"));
            Equal(true, result.Warnings.Any(message => message.Contains("overrideTaskbarGap")));
            Equal(json, result.Settings.ImportedUpstreamJson);
            Equal(0, ScheduleEvaluator.GetCurrentLevels(result.Settings, new(2026, 9, 29, 23, 0, 0), ["native"], TimeZoneInfo.Utc).Count);
        }),
        ("Importer migrates legacy midnight and zero-brightness values faithfully", () =>
        {
            var result = UpstreamSettingsImporter.Import("""{"adjustmentTimeSpeed":"instant","detectIdleTime":35,"adjustmentTimes":[{"hour":12,"minute":0,"am":"AM","brightness":0}],"hotkeys":{"legacy":{"accelerator":"Alt+F3","monitor":"all","direction":-1}},"hotkeyPercent":7}""");
            Equal("00:00", result.Settings.Schedule[0].Time); Equal(0, result.Settings.Schedule[0].Brightness);
            Equal(0, result.Settings.IdleMinutes); Equal(35, result.Settings.IdleSeconds);
            Equal(-7d, result.Settings.Hotkeys[0].Actions[0].Value);
            Equal((uint)0x72, result.Settings.Hotkeys[0].VirtualKey);
        }),
        ("Importer preserves sensor source and hardware disabling switches", () =>
        {
            var result = UpstreamSettingsImporter.Import("""{"adjustmentTimeSpeed":"instant","disableWMI":true,"disableWin32":true,"disableAppleStudio":true,"logging":false,"lightSensor":{"enabled":true,"active":"yocto","sensorPollingInterval":3,"sensors":{"yocto":{"hubUrl":"localhost"},"fake":{"overriddenLux":12}},"monitorSettings":{"old":{"enabled":true,"minLux":7,"maxLux":300}}}}""", new Dictionary<string, string> { ["old"] = "native" });
            Equal(true, result.Settings.DisableWmi && result.Settings.DisableDdc && result.Settings.DisableAppleStudio);
            Equal(false, result.Settings.Logging); Equal("yocto", result.Settings.Sensor.Provider);
            Equal("http://localhost:4444/", result.Settings.Sensor.Endpoint); Equal(3, result.Settings.Sensor.PollSeconds);
            Equal(7d, result.Settings.Sensor.Monitors["native"].MinLux);
        }),
        ("Importer disables unsupported accelerators and empty monitor actions", () =>
        {
            var result = UpstreamSettingsImporter.Import("""{"adjustmentTimeSpeed":"instant","hotkeys":[{"accelerator":"Ctrl+A+B","actions":[{"type":"set","monitors":{},"value":50}]}]}""");
            Equal(false, result.Settings.Hotkeys[0].Enabled); Equal(true, result.Warnings.Count >= 2);
            Throws<JsonException>(() => UpstreamSettingsImporter.Import("[]"));
            Equal(false, UpstreamSettingsImporter.TryAccelerator("Ctrl+A+B", out _, out _));
        }),
        ("Extended schema survives normalization and JSON without losing new fields", () =>
        {
            var settings = new AppSettings
            {
                ScheduleInterpolation = true, RestoreBrightnessAtStartup = true, DisableWmi = true, Logging = false,
                Sensor = new() { Provider = "fake", FakeLux = 42, Monitors = new() { ["a"] = new() { Enabled = true } } },
                Monitors = new() { ["a"] = new() { MainControl = "gamma", ExtendMinimum = true, Features = new() { [0x62] = new() { Enabled = true, Max = 255 } } } },
                Hotkeys = [new() { Action = "custom", Actions = [new() { Type = "cycle", Values = [10, 50] }] }],
                Profiles = [new() { Enabled = true, Name = "Office", Path = "editor.exe", Brightness = new() { ["a"] = 42 } }],
                LastBrightness = new() { ["a"] = 31 }, ImportedUpstreamJson = "{\"original\":true}", ImportWarnings = ["pending"]
            };
            var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(SettingsNormalizer.Normalize(settings)))!;
            Equal(true, loaded.ScheduleInterpolation && loaded.RestoreBrightnessAtStartup && loaded.DisableWmi);
            Equal(false, loaded.Logging); Equal(42d, loaded.Sensor.FakeLux); Equal(255, loaded.Monitors["a"].Features[0x62].Max);
            Equal(true, loaded.Hotkeys[0].Enabled); Equal(2, loaded.Hotkeys[0].Actions[0].Values.Count);
            Equal(42, loaded.Profiles[0].Brightness["a"]); Equal(31d, loaded.LastBrightness["a"]); Equal("pending", loaded.ImportWarnings[0]);
        }),
        ("Importer preserves power modes, overlay policy and driver timing", () =>
        {
            var result = UpstreamSettingsImporter.Import("""{"adjustmentTimeSpeed":"instant","sleepAction":"none","defaultOverlayType":"aggressive","updateInterval":500,"hardwareRestoreSeconds":8,"checkVCPWaitMS":40,"disableAutoApply":true,"hideClosedLid":true,"disableWMI":true,"disableWMIC":true}""");
            Equal("none", result.Settings.PowerOffMode); Equal("aggressive", result.Settings.OverlayPolicy);
            Equal(500, result.Settings.UpdateIntervalMilliseconds); Equal(8, result.Settings.HardwareRestoreSeconds);
            Equal(40, result.Settings.VcpReadDelayMilliseconds); Equal(true, result.Settings.DisableAutoApply && result.Settings.HideClosedLid);
            Equal(0, result.Warnings.Count);
            Equal("windows", UpstreamSettingsImporter.Import("""{"sleepAction":"ps","adjustmentTimeSpeed":"instant"}""").Settings.PowerOffMode);
            Equal("both", UpstreamSettingsImporter.Import("""{"sleepAction":"ps_ddcci","adjustmentTimeSpeed":"instant"}""").Settings.PowerOffMode);
        }),
        ("CLI schedule and UDP transport flags preserve command validation", () =>
        {
            var command = CommandLine.Parse(["--UDP", "--UseTime", "--Overlay"]);
            Equal(true, command.Udp && command.UseTime && command.HasCommand && command.Overlay);
            Equal(false, command.HasMonitorCommand);
            Equal(true, CommandLine.Parse(["--udp", "--list"]).List);
            Equal(55d, CommandLine.Parse(["--UDP", "--All", "--Set=55"]).Set);
            foreach (string[] arguments in new string[][] { ["--UseTime", "--All", "--Set=55"], ["--UseTime", "--List"], ["--UDP"], ["--UDP=localhost", "--List"], ["--smoke-test", "--UseTime"], ["--demo", "--UDP", "--All", "--Set=50"] })
                Throws<ArgumentException>(() => CommandLine.Parse(arguments));
        }),
        ("Native appearance and timing preferences survive validated persistence", () =>
        {
            var settings = SettingsNormalizer.Normalize(new AppSettings
            {
                OverlayPolicy = "aggressive", UpdateIntervalMilliseconds = 1, VcpReadDelayMilliseconds = 2000,
                HardwareRestoreSeconds = -1, ThemeNotifications = false, PowerNotifications = false,
                Profiles = [new() { OverlayType = "force" }, new() { OverlayType = "safe" }],
                Monitors = new() { ["a"] = new() { ShowName = false, ShowValue = false, IconGlyph = "\uE706" } }
            });
            Equal(16, settings.UpdateIntervalMilliseconds); Equal(1000, settings.VcpReadDelayMilliseconds); Equal(0, settings.HardwareRestoreSeconds);
            Equal(false, settings.ThemeNotifications || settings.PowerNotifications);
            Equal("force", settings.Profiles[0].OverlayType); Equal("safe", settings.Profiles[1].OverlayType);
            Equal(false, settings.Monitors["a"].ShowName || settings.Monitors["a"].ShowValue); Equal("\uE706", settings.Monitors["a"].IconGlyph);
        }),
        ("Importer preserves native brightness keys and custom feature indicators", () =>
        {
            var result = UpstreamSettingsImporter.Import("""{"adjustmentTimeSpeed":"instant","disableThemeChanges":true,"disablePowerNotifications":true,"hotkeys":[{"accelerator":"BrightnessUp","actions":[{"type":"offset","target":"brightness","allMonitors":true,"value":5}]}],"monitorFeaturesSettings":{"model":{"0x16":{"iconType":"windows","icon":"E706","iconPath":"C:\\icons\\sun.png"},"0x18":{"iconType":"text","iconText":"Green"}}}}""", new Dictionary<string, string> { ["model"] = "native" });
            Equal(true, result.Settings.Hotkeys[0].Enabled); Equal("BrightnessUp", result.Settings.Hotkeys[0].NativeKey);
            Equal(0u, result.Settings.Hotkeys[0].VirtualKey); Equal(false, result.Settings.ThemeNotifications || result.Settings.PowerNotifications);
            Equal("\uE706", result.Settings.Monitors["native"].Features[0x16].IconGlyph);
            Equal(@"C:\icons\sun.png", result.Settings.Monitors["native"].Features[0x16].IconPath);
            Equal("Green", result.Settings.Monitors["native"].Features[0x18].IconText); Equal("Green", result.Settings.Monitors["native"].Features[0x18].Name);
            Equal(0, result.Warnings.Count);
            Equal(true, CommandLine.Parse(["--demo", "--settings"]).Settings);
            Throws<ArgumentException>(() => CommandLine.Parse(["--settings", "--List"]));
        }),
        ("Known-display import preserves source, mapped zero brightness and SDR levels", () =>
        {
            const string known = """{"legacy-key":{"id":"legacy-id","brightness":0},"sdr-key":{"brightness":12,"sdrLevel":65},"unmapped":{"brightness":40}}""";
            var original = new AppSettings { Monitors = new() { ["sdr"] = new() { MainControl = "sdr" } } };
            var result = UpstreamSettingsImporter.ImportKnownDisplays(original, known, new Dictionary<string, string> { ["legacy-key"] = "native", ["legacy-id"] = "native", ["sdr-key"] = "sdr" });
            Equal(0d, result.Settings.LastBrightness["native"]); Equal(65d, result.Settings.LastBrightness["sdr"]);
            Equal(40d, result.Settings.LastBrightness["unresolved:unmapped"]); Equal("unmapped", result.UnmappedMonitorIds.Single());
            Equal(known, result.Settings.ImportedKnownDisplaysJson); Equal(0, original.LastBrightness.Count);
            var combined = UpstreamSettingsImporter.Import("""{"adjustmentTimeSpeed":"instant"}""", new Dictionary<string, string> { ["a"] = "native" }, """{"a":{"brightness":33}}""");
            Equal(33d, combined.Settings.LastBrightness["native"]);
        }),
        ("Known-display import rejects ambiguous records without losing prior levels", () =>
        {
            var original = new AppSettings { LastBrightness = new() { ["native"] = 73 } };
            var result = UpstreamSettingsImporter.ImportKnownDisplays(original, """{"a":{"brightness":20},"b":{"brightness":80},"c":{"brightness":101}}""", new Dictionary<string, string> { ["a"] = "native", ["b"] = "native", ["c"] = "other" });
            Equal(73d, result.Settings.LastBrightness["native"]); Equal(false, result.Settings.LastBrightness.ContainsKey("other"));
            Equal(2, result.Warnings.Count);
            var conflict = UpstreamSettingsImporter.ImportKnownDisplays(new(), """{"a":{"id":"b","brightness":50}}""", new Dictionary<string, string> { ["a"] = "one", ["b"] = "two" });
            Equal(0, conflict.Settings.LastBrightness.Count); Equal(1, conflict.Warnings.Count);
            Throws<JsonException>(() => UpstreamSettingsImporter.ImportKnownDisplays(new(), "[]"));
        }),
        ("Semantic version precedence handles prerelease promotion and numeric identifiers", () =>
        {
            string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.10.0", "2.0.0"];
            for (int i = 0; i < ordered.Length - 1; i++)
            {
                Equal(-1, SemanticVersion.Compare(ordered[i], ordered[i + 1]));
                Equal(1, SemanticVersion.Compare(ordered[i + 1], ordered[i]));
            }
            Equal(0, SemanticVersion.Compare("1.0.0-beta.2+one", "1.0.0-beta.2+002"));
            Equal(1, SemanticVersion.Compare("1.0.0-beta.999999999999999999999", "1.0.0-beta.20"));
            foreach (string invalid in new[] { "1.0", "1.0.0.0", "v1.0.0", "01.0.0", "1.0.0-01", "1.0.0-alpha..1", "1.0.0+", "1.0.0+one+two", "1.0.0-β", "1.0.0\n" })
                Equal(false, SemanticVersion.TryCompare(invalid, "1.0.0", out _));
            Throws<FormatException>(() => SemanticVersion.Compare("invalid", "1.0.0"));
        }),
        ("Malformed native hotkeys are retained disabled instead of changing brightness", () =>
        {
            var settings = SettingsNormalizer.Normalize(new AppSettings { Hotkeys =
            [
                new() { Actions = [new() { Type = "unknown" }] },
                new() { Actions = [new() { Type = "cycle", Values = [] }] },
                new() { Actions = [new() { Target = "invalid" }] },
                new() { NativeKey = "invalid" },
                new() { Actions = [new() { Type = "set", Value = double.NaN }] },
                new() { NativeKey = "BrightnessDown", VirtualKey = 0, Actions = [new() { Type = "offset", Value = -5 }] }
            ] });
            Equal(true, settings.Hotkeys.Take(5).All(binding => !binding.Enabled));
            Equal("unknown", settings.Hotkeys[0].Actions[0].Type); Equal(true, settings.Hotkeys[5].Enabled);
        })
    ];

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
    private static void Near(double expected, double actual)
    {
        if (Math.Abs(expected - actual) > 0.0001) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
