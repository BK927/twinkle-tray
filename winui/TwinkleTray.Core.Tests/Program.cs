using TwinkleTray.Core;

// Package-free tests: dotnet run --project winui/TwinkleTray.Core.Tests
var tests = new (string Name, Action Run)[]
{
    ("Brightness calibration maps endpoints and midpoint", () =>
    {
        Equal(20, BrightnessMath.ToHardware(0, 20, 80));
        Equal(50, BrightnessMath.ToHardware(50, 20, 80));
        Equal(80, BrightnessMath.ToHardware(100, 20, 80));
        Equal(50d, BrightnessMath.ToLogical(50, 20, 80));
    }),
    ("Brightness clamps out-of-range input and normalizes reversed limits", () =>
    {
        Equal(20, BrightnessMath.ToHardware(-20, 80, 20));
        Equal(80, BrightnessMath.ToHardware(200, 80, 20));
        Equal(0, BrightnessMath.ToHardware(double.NaN, -5, 105));
        Equal(100, BrightnessMath.ToHardware(double.PositiveInfinity, 0, 100));
        Equal(0d, BrightnessMath.ToLogical(10, 20, 80));
        Equal(100d, BrightnessMath.ToLogical(90, 20, 80));
        Equal(0d, BrightnessMath.ToLogical(50, 50, 50));
    }),
    ("Brightness conversion round-trips calibrated hardware percentages", () =>
    {
        for (var hardware = 17; hardware <= 83; hardware++)
            Equal(hardware, BrightnessMath.ToHardware(BrightnessMath.ToLogical(hardware, 17, 83), 17, 83));
    }),
    ("Schedule crosses midnight in chronological order", () =>
    {
        var late = new ScheduleEntry { Id = "late", Time = "23:59" };
        var early = new ScheduleEntry { Id = "early", Time = "00:01" };
        var due = ScheduleEvaluator.GetDue([early, late], new DateTime(2026, 9, 28, 23, 58, 0), new DateTime(2026, 9, 29, 0, 2, 0));
        Equal("late,early", string.Join(',', due.Select(entry => entry.Id)));
    }),
    ("Schedule excludes previous boundary and includes current boundary", () =>
    {
        var entries = new[] { new ScheduleEntry { Time = "20:00" }, new ScheduleEntry { Time = "20:01" } };
        var due = ScheduleEvaluator.GetDue(entries, new DateTime(2026, 9, 29, 20, 0, 0), new DateTime(2026, 9, 29, 20, 1, 0));
        Equal(1, due.Count);
        Equal("20:01", due[0].Time);
        Equal(0, ScheduleEvaluator.GetDue(entries, new DateTime(2026, 9, 29, 20, 1, 0), new DateTime(2026, 9, 29, 20, 1, 1)).Count);
    }),
    ("Schedule ignores disabled, malformed, future and invalid entries", () =>
    {
        ScheduleEntry[] entries = [new() { Time = "19:30", Enabled = false }, new() { Time = "24:00" },
            new() { Time = "20:01" }, new() { Time = "19:30", Brightness = 101 }, new() { Time = "7:30" }];
        Equal(0, ScheduleEvaluator.GetDue(entries, new DateTime(2026, 9, 29, 19, 0, 0), new DateTime(2026, 9, 29, 20, 0, 0)).Count);
    }),
    ("Schedule emits once per entry after long pause and handles backward clock", () =>
    {
        ScheduleEntry[] entries = [new() { Time = "20:00" }];
        Equal(1, ScheduleEvaluator.GetDue(entries, new DateTime(2026, 9, 1), new DateTime(2026, 9, 29, 21, 0, 0)).Count);
        Equal(0, ScheduleEvaluator.GetDue(entries, new DateTime(2026, 9, 29), new DateTime(2026, 9, 28)).Count);
    }),
    ("CLI preserves monitor IDs and accepts decimal percentages", () =>
    {
        var command = CommandLine.Parse(["--MonitorID=UID_AbC", "--Offset=-20.5", "--Overlay"]);
        Equal("UID_AbC", command.MonitorId);
        Equal(-20.5d, command.Offset!.Value);
        Equal(true, command.Overlay && command.HasMonitorCommand && command.HasCommand);
    }),
    ("CLI accepts hexadecimal VCP and case-insensitive flags", () =>
    {
        var command = CommandLine.Parse(["--ALL", "--vcp=\"0xD6:0x05\""]);
        Equal((byte)0xD6, command.Vcp!.Code);
        Equal((ushort)5, command.Vcp.Value);
        Equal(true, command.All);
        Equal(true, CommandLine.Parse(["--demo", "--background"]).Demo);
        Equal(true, CommandLine.Parse(["--smoke-test"]).SmokeTest);
        Equal(true, CommandLine.Parse(["--List"]).List);
        Equal(true, CommandLine.Parse(["--Panel"]).Panel);
        Equal(true, CommandLine.Parse(["--MonitorNum=1", "--Set=95"]).MonitorNum == 1);
    }),
    ("CLI rejects ambiguous actions, invalid numbers, and unsupported flags", () =>
    {
        string[][] invalid = [
            ["--All", "--Set=50", "--Offset=5"], ["--All", "--MonitorNum=1", "--Set=50"],
            ["--Set=50"], ["--All"], ["--MonitorNum=0", "--Set=50"], ["--MonitorID=", "--Set=50"],
            ["--All", "--Set=NaN"], ["--All", "--Set=Infinity"], ["--All", "--Set=101"],
            ["--All", "--Offset=-101"], ["--All", "--VCP=0x100:5"], ["--All", "--VCP=0xD6:65536"],
            ["--All", "--VCP=0xD6:5:1"], ["--All", "--VCP=-1:5"], ["--All", "--VCP=foo"],
            ["--List", "--All", "--Set=50"], ["--Panel", "--Overlay"], ["--background", "--Panel"],
            ["--All=true"], ["--List", "--list"], ["--Unknown"], ["--smoke-test", "--All", "--Set=50"]
        ];
        foreach (var args in invalid)
            Throws<ArgumentException>(() => CommandLine.Parse(args), string.Join(' ', args));
    }),
    ("Settings normalization validates values without mutating the input", () =>
    {
        var input = new AppSettings
        {
            Theme = "DARK", ScrollStep = 0, IdleMinutes = 0, IdleBrightness = 120,
            Monitors = new() { ["display"] = new() { MinBrightness = 90, MaxBrightness = 10 } },
            Hotkeys = [new() { Action = "unsupported" }]
        };
        var normalized = SettingsNormalizer.Normalize(input);
        Equal("dark", normalized.Theme);
        Equal(1, normalized.ScrollStep);
        Equal(1, normalized.IdleMinutes);
        Equal(100, normalized.IdleBrightness);
        Equal(10, normalized.Monitors["display"].MinBrightness);
        Equal(90, normalized.Monitors["display"].MaxBrightness);
        Equal(false, normalized.Hotkeys[0].Enabled);
        Equal(0, input.ScrollStep);
        Equal(90, input.Monitors["display"].MinBrightness);
    }),
    ("Settings round-trip and replace atomically in an isolated folder", () => InTemporaryDirectory(directory =>
    {
        var path = Path.Combine(directory, "nested", "settings.json");
        var store = new SettingsStore(path);
        Equal("system", store.Load().Theme);
        Equal(false, File.Exists(path));
        store.Save(new AppSettings { Theme = "dark", Monitors = new() { ["display"] = new() { Name = "업무용 모니터" } } });
        Equal("업무용 모니터", store.Load().Monitors["display"].Name);
        store.Save(new AppSettings { Theme = "light" });
        Equal("light", store.Load().Theme);
        Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length);
    })),
    ("Corrupt settings preserve original and recoverable copy", () => InTemporaryDirectory(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        const string broken = "{\"theme\":\"dark\", broken";
        File.WriteAllText(path, broken);
        var error = Throws<SettingsLoadException>(() => new SettingsStore(path).Load());
        Equal(broken, File.ReadAllText(path));
        Equal(true, error.BackupPath is not null);
        Equal(broken, File.ReadAllText(error.BackupPath!));
    })),
    ("Settings handle explicit null collections and older missing fields", () => InTemporaryDirectory(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "{\"monitors\":null,\"schedule\":null,\"hotkeys\":null,\"theme\":null}");
        var settings = new SettingsStore(path).Load();
        Equal("system", settings.Theme);
        Equal(5, settings.ScrollStep);
        Equal(0, settings.Monitors.Count + settings.Schedule.Count + settings.Hotkeys.Count);
    }))
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {error}");
    }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static TException Throws<TException>(Action action, string context = "") where TException : Exception
{
    try { action(); }
    catch (TException error) { return error; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}. {context}");
}

static void InTemporaryDirectory(Action<string> action)
{
    // Test data stays under the test output directory; no user preferences or hardware are accessed.
    var root = Path.GetFullPath(AppContext.BaseDirectory);
    var directory = Path.Combine(root, "test-data-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try { action(directory); }
    finally
    {
        var resolved = Path.GetFullPath(directory);
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup path escaped its output directory.");
        Directory.Delete(resolved, recursive: true);
    }
}
