using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using TwinkleTray.Hardware;

// Opt-in diagnostic only. Its native imports can READ ramps but cannot set them.
// Every write/restore goes through the production MonitorService public API.
internal static class GammaExercise
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            bool simulated = args.Contains("--exercise-gamma-self-test", StringComparer.Ordinal);
            string flag = simulated ? "--exercise-gamma-self-test" : "--exercise-gamma-only";
            if (args.Length != 3 || args[0] != flag || args[1] != "--report" || !Path.IsPathFullyQualified(args[2]))
                throw new ArgumentException("Use " + flag + " --report <new-absolute-json-path>; gamma is a separate exercise.");
            string reportPath = Path.GetFullPath(args[2]);
            if (File.Exists(reportPath)) throw new IOException("The recovery report already exists. Choose a new path.");
            if (simulated) return await SelfTestAsync(reportPath);
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || RuntimeInformation.OSArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("Gamma exercise acceptance is limited to native Windows x64.");
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancel;
            var backend = new NativeBackend();
            var report = new Report();
            try
            {
                int result = await new Runner(backend, reportPath, report, TimeSpan.FromMilliseconds(200)).RunAsync(cancellation.Token);
                Console.WriteLine(report.Status + ": " + reportPath);
                return result;
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
                // Dispose would retry production-owned ramps. After uncertainty or
                // an external calibration change, that would be an unjournaled
                // write. Let process exit release native handles instead.
                if (report.Steps.All(step => !step.WriteAttempted || step.RestoredExactly)) backend.Dispose();
                else Console.Error.WriteLine("Automatic disposal restoration was suppressed after uncertainty; preserve the recovery report. No further gamma writes will be attempted.");
            }
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
    }

    private sealed class Runner(IBackend backend, string reportPath, Report report, TimeSpan delay)
    {
        private bool _journalFailed;

        internal async Task<int> RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                IReadOnlyList<MonitorSnapshot>? stable = null;
                string? previous = null;
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    var snapshot = await backend.RefreshAsync(cancellationToken);
                    report.Inventories.Add(new Inventory("Preflight " + attempt, snapshot.ToArray(), backend.LastRefreshErrors.ToArray()));
                    Save();
                    string signature = Fingerprint(snapshot);
                    if (signature == previous) { stable = snapshot; break; }
                    previous = signature;
                    if (attempt < 4) await Task.Delay(delay + delay, cancellationToken);
                }
                if (stable is null) throw new InvalidOperationException("The display inventory did not stabilize in four read-only refreshes.");
                report.InitialTopology = Fingerprint(stable);
                foreach (var group in stable.Where(display => !string.IsNullOrWhiteSpace(display.DeviceName)).GroupBy(display => display.DeviceName, StringComparer.OrdinalIgnoreCase))
                {
                    // Capture every visible GDI output for peer-isolation checks,
                    // even outputs that will not themselves be written.
                    var ramp = backend.ReadRamp(group.Key);
                    if (ramp is null || ramp.Length != 768)
                    {
                        report.Status = "Skipped";
                        report.Errors.Add("Cannot observe all peer ramps: " + group.Key + ". No gamma write was attempted.");
                        Save(); return 2;
                    }
                    var displays = group.ToArray();
                    var display = displays[0];
                    string? excluded = displays.Length != 1 ? "Cloned/shared GDI output" :
                        !display.HdrModeKnown ? "HDR/SDR mode is unknown" : display.HdrActive ? "HDR is active" :
                        !display.SupportsGammaBrightness ? "Gamma support is unavailable" : display.Connection != "DDC/CI" ? "Not an ordinary DDC/CI display" :
                        display.DeviceInstanceId.Contains("\\APP", StringComparison.OrdinalIgnoreCase) || display.Name.Replace(" ", "").Contains("StudioDisplay", StringComparison.OrdinalIgnoreCase) ? "Apple displays are excluded" : null;
                    report.Outputs.Add(new Output(display.Id, display.Name, display.DeviceInstanceId, group.Key, ramp, excluded));
                }
                if (report.Outputs.All(output => output.Excluded is not null)) { report.Status = "Skipped"; Save(); return 2; }
                report.Status = "BaselinesCaptured";
                Save(); // Contains all 768 original ushort entries before any setter.
                await VerifyAsync(null, cancellationToken);
                foreach (var output in report.Outputs.Where(output => output.Excluded is null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await ExerciseAsync(output, cancellationToken)) { report.FinishedUtc = DateTimeOffset.UtcNow; TrySave(); return 1; }
                }
                await VerifyAsync(null, cancellationToken);
                report.Status = "Passed";
                report.FinishedUtc = DateTimeOffset.UtcNow;
                Save(); return 0;
            }
            catch (Exception exception)
            {
                report.Errors.Add(exception.Message);
                report.Status = report.Steps.Any(step => step.WriteAttempted && !step.RestoredExactly) ? "RestoreUncertain" :
                    report.Steps.Any(step => step.WriteAttempted) ? "StoppedRestored" : "FailedNoWrites";
                report.FinishedUtc = DateTimeOffset.UtcNow;
                TrySave(); return 1;
            }
        }

        private async Task<bool> ExerciseAsync(Output output, CancellationToken cancellationToken)
        {
            await VerifyAsync(null, cancellationToken);
            var step = new Step { DisplayId = output.Id, Expected = BuildExpected(output.Original, 98) };
            report.Steps.Add(step);
            report.Status = "WritePending";
            step.WriteAttempted = true;
            Save(); // Persist conservative write intent before entering any setter.
            try
            {
                await backend.SetGammaAsync(output.Id, 98, cancellationToken);
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var actual = backend.ReadRamp(output.DeviceName) ?? throw new InvalidOperationException("Gamma readback is unavailable.");
                    step.ReadbackAttempts = attempt;
                    if (!actual.SequenceEqual(output.Original) && Near(actual, step.Expected)) { step.Applied = actual; break; }
                    if (attempt < 5) await Task.Delay(delay, cancellationToken);
                }
                if (step.Applied is null) throw new InvalidOperationException("The 98% software-brightness request did not produce a readable, expected ramp change.");
                Save();
                await VerifyAsync(output.Id, cancellationToken);
                step.PeersUnchanged = true;
            }
            catch (Exception exception) { step.Error = exception.Message; report.Errors.Add(output.Name + ": " + exception.Message); }
            finally
            {
                step.RestoredExactly = await RestoreAsync(output, step);
                if (step.RestoredExactly)
                {
                    try { await VerifyAsync(null, CancellationToken.None); step.FinalAllRampsUnchanged = true; }
                    catch (Exception exception) { step.Error ??= exception.Message; report.Errors.Add(exception.Message); }
                }
                report.Status = !step.RestoredExactly ? "RestoreUncertain" : step.Error is null && !_journalFailed ? "Running" : "StoppedRestored";
                TrySave();
            }
            return step.RestoredExactly && step.PeersUnchanged && step.FinalAllRampsUnchanged && step.Error is null && !_journalFailed;
        }

        private async Task<bool> RestoreAsync(Output output, Step step)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var restoration = new Restoration { Attempt = attempt };
                step.Restoration.Add(restoration);
                try
                {
                    var connected = await backend.RefreshAsync(CancellationToken.None);
                    report.Inventories.Add(new Inventory("Restore " + attempt, connected.ToArray(), backend.LastRefreshErrors.ToArray()));
                    ValidateTarget(output, connected);
                    var current = backend.ReadRamp(output.DeviceName) ?? throw new InvalidOperationException("Current ramp cannot be read for restoration.");
                    bool original = current.SequenceEqual(output.Original);
                    bool ours = step.Applied is not null ? current.SequenceEqual(step.Applied) : Near(current, step.Expected);
                    if (!original && !ours)
                    {
                        restoration.Error = "An external or unrecognized ramp change was detected. It was not overwritten.";
                        step.Error ??= restoration.Error;
                        report.Errors.Add(restoration.Error);
                        TrySave(); return false;
                    }
                    restoration.WritePending = true;
                    TrySave(); // Previously persisted full original remains recovery evidence if storage fails.
                    await backend.RestoreGammaAsync(CancellationToken.None);
                    var restored = backend.ReadRamp(output.DeviceName) ?? throw new InvalidOperationException("Restored ramp cannot be read.");
                    restoration.ExactMatch = restored.SequenceEqual(output.Original);
                    restoration.MaximumDifference = restored.Zip(output.Original).Max(pair => Math.Abs(pair.First - pair.Second));
                    if (!restoration.ExactMatch) throw new InvalidOperationException("Restoration did not exactly reproduce all 768 original ramp entries.");
                    TrySave(); return true;
                }
                catch (Exception exception) { restoration.Error = exception.Message; TrySave(); if (attempt < 3) await Task.Delay(delay); }
            }
            report.Errors.Add("RESTORE UNCERTAIN: " + output.Name + "; no subsequent gamma exercise is permitted.");
            return false;
        }

        private async Task VerifyAsync(string? changedId, CancellationToken cancellationToken)
        {
            var connected = await backend.RefreshAsync(cancellationToken);
            report.Inventories.Add(new Inventory("Verify", connected.ToArray(), backend.LastRefreshErrors.ToArray()));
            if (Fingerprint(connected) != report.InitialTopology) throw new InvalidOperationException("Display identity, mapping or color mode changed; no new exercise is allowed.");
            foreach (var output in report.Outputs)
            {
                if (output.Id == changedId) continue;
                var actual = backend.ReadRamp(output.DeviceName);
                if (actual is null || !actual.SequenceEqual(output.Original))
                    throw new InvalidOperationException("Non-target/original gamma ramp changed: " + output.Name + ". It was not overwritten.");
            }
        }

        private void Save() => Persist(reportPath, report);
        private bool TrySave()
        {
            try { Save(); return true; }
            catch (Exception exception) { _journalFailed = true; Console.Error.WriteLine("Gamma journal update failed; restore and stop: " + exception.Message); return false; }
        }
    }

    private static string Fingerprint(IReadOnlyList<MonitorSnapshot> snapshots) => JsonSerializer.Serialize(snapshots.OrderBy(display => display.Id, StringComparer.OrdinalIgnoreCase)
        .Select(display => new { display.Id, display.DeviceName, display.DeviceInstanceId, display.Connection, display.HdrModeKnown, display.HdrActive }));

    private static void ValidateTarget(Output output, IReadOnlyList<MonitorSnapshot> snapshots)
    {
        var target = snapshots.SingleOrDefault(display => display.Id == output.Id);
        if (target is null || target.DeviceInstanceId != output.DeviceInstanceId || target.DeviceName != output.DeviceName || !target.HdrModeKnown || target.HdrActive ||
            snapshots.Count(display => string.Equals(display.DeviceName, output.DeviceName, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidOperationException("Original gamma output identity is no longer safely available; no restoration write was sent.");
    }

    private static ushort[] BuildExpected(ushort[] original, double level) =>
        (ushort[])typeof(MonitorService).Assembly.GetType("TwinkleTray.Hardware.GammaController")!.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [original, level])!;
    private static bool Near(ushort[] actual, ushort[] expected) => actual.Length == 768 && expected.Length == 768 && actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) <= 256);

    private static void Persist(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = path + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
            File.Move(pending, path, true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    private interface IBackend : IDisposable
    {
        IReadOnlyList<string> LastRefreshErrors { get; }
        Task<IReadOnlyList<MonitorSnapshot>> RefreshAsync(CancellationToken token);
        ushort[]? ReadRamp(string deviceName);
        Task SetGammaAsync(string id, double percentage, CancellationToken token);
        Task RestoreGammaAsync(CancellationToken token);
    }

    private sealed class NativeBackend : IBackend
    {
        private readonly MonitorService _service = new();
        internal NativeBackend() => _service.Configure(new HardwareOptions { EnableGamma = true, DisableAppleStudio = true });
        public IReadOnlyList<string> LastRefreshErrors => _service.LastRefreshErrors;
        public Task<IReadOnlyList<MonitorSnapshot>> RefreshAsync(CancellationToken token) => _service.RefreshAsync(token);
        public Task SetGammaAsync(string id, double percentage, CancellationToken token) => _service.SetGammaBrightnessAsync(id, percentage, token);
        public Task RestoreGammaAsync(CancellationToken token) => _service.RestoreGammaAsync(token);
        public ushort[]? ReadRamp(string deviceName)
        {
            var dc = CreateDC("DISPLAY", deviceName, null, 0);
            if (dc == 0) return null;
            try { var values = new ushort[768]; return GetDeviceGammaRamp(dc, values) ? values : null; }
            finally { DeleteDC(dc); }
        }
        public void Dispose() => _service.Dispose();
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateDCW")] private static extern nint CreateDC(string driver, string device, string? output, nint initialization);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDeviceGammaRamp(nint dc, [Out] ushort[] ramp);
    }

    private sealed class Report
    {
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedUtc { get; set; }
        public bool Simulated { get; init; }
        public string Status { get; set; } = "Starting";
        public string Scope { get; } = "Windows x64, ordinary DDC/CI SDR displays, 98% software brightness, exact original-ramp restoration; no raw gamma setters in diagnostics.";
        public string InitialTopology { get; set; } = "";
        public List<Output> Outputs { get; } = [];
        public List<Step> Steps { get; } = [];
        public List<Inventory> Inventories { get; } = [];
        public List<string> Errors { get; } = [];
    }
    private sealed record Output(string Id, string Name, string DeviceInstanceId, string DeviceName, ushort[] Original, string? Excluded);
    private sealed record Inventory(string Phase, IReadOnlyList<MonitorSnapshot> Displays, IReadOnlyList<string> LastRefreshErrors);
    private sealed class Step
    {
        public required string DisplayId { get; init; }
        public double RequestedPercentage { get; } = 98;
        public required ushort[] Expected { get; init; }
        public ushort[]? Applied { get; set; }
        public bool WriteAttempted { get; set; }
        public int ReadbackAttempts { get; set; }
        public bool PeersUnchanged { get; set; }
        public bool RestoredExactly { get; set; }
        public bool FinalAllRampsUnchanged { get; set; }
        public string? Error { get; set; }
        public List<Restoration> Restoration { get; } = [];
    }
    private sealed class Restoration
    {
        public int Attempt { get; init; }
        public bool WritePending { get; set; }
        public bool ExactMatch { get; set; }
        public int? MaximumDifference { get; set; }
        public string? Error { get; set; }
    }

    private static async Task<int> SelfTestAsync(string reportPath)
    {
        var results = new List<object>();
        var folder = Path.Combine(Path.GetDirectoryName(reportPath)!, "gamma-fixtures-" + Guid.NewGuid().ToString("N"));
        foreach (string scenario in new[] { "success", "external-ramp", "restore-fails", "identity-changes", "hdr-unknown" })
        {
            var path = Path.Combine(folder, scenario + ".json");
            var fake = new FakeBackend(scenario, path);
            var report = new Report { Simulated = true };
            int result = await new Runner(fake, path, report, TimeSpan.Zero).RunAsync(CancellationToken.None);
            bool okay = scenario switch
            {
                "success" => result == 0 && report.Steps.Count == 2 && report.Steps.All(step => step.RestoredExactly) && fake.AllOriginal,
                "external-ramp" or "identity-changes" => result == 1 && report.Steps.Count == 1 && !report.Steps[0].RestoredExactly && fake.RestoreCalls == 0,
                "restore-fails" => result == 1 && report.Steps.Count == 1 && report.Steps[0].Restoration.Count == 3,
                "hdr-unknown" => result == 2 && report.Steps.Count == 0 && fake.AllOriginal,
                _ => false,
            };
            if (!okay) throw new InvalidOperationException("Gamma fake self-test failed: " + scenario);
            results.Add(new { Scenario = scenario, Passed = true, Report = path });
        }
        Persist(reportPath, new { Status = "Passed", Simulated = true, HardwareCalls = 0, Results = results });
        Console.WriteLine("All five gamma fake scenarios passed. No hardware backend was created.");
        return 0;
    }

    private sealed class FakeBackend(string scenario, string reportPath) : IBackend
    {
        private readonly ushort[] _original = Enumerable.Range(0, 768).Select(index => (ushort)((index % 256) * 257)).ToArray();
        private readonly Dictionary<string, ushort[]> _ramps = new();
        private string? _written;
        private int _postWriteRefreshes;
        public int RestoreCalls { get; private set; }
        public bool AllOriginal => _ramps.Values.All(ramp => ramp.SequenceEqual(_original));
        public IReadOnlyList<string> LastRefreshErrors => [];
        public Task<IReadOnlyList<MonitorSnapshot>> RefreshAsync(CancellationToken token)
        {
            if (_written is not null)
            {
                _postWriteRefreshes++;
                if (scenario == "external-ramp" && _postWriteRefreshes == 1) _ramps[_written][128] += 1;
            }
            IReadOnlyList<MonitorSnapshot> result = new[] { "one", "two" }.Select(id => new MonitorSnapshot(id, "Fake " + id, "DDC/CI", 100, true, true, 70)
            { DeviceName = id, DeviceInstanceId = id + (scenario == "identity-changes" && _written is not null && id == _written ? "-changed" : ""), HdrModeKnown = scenario != "hdr-unknown", GammaBrightness = 100 }).ToArray();
            return Task.FromResult(result);
        }
        public ushort[] ReadRamp(string deviceName) => (_ramps.TryGetValue(deviceName, out var value) ? value : _original).ToArray();
        public Task SetGammaAsync(string id, double percentage, CancellationToken token)
        {
            using var persisted = JsonDocument.Parse(File.ReadAllText(reportPath));
            if (persisted.RootElement.GetProperty("Outputs").EnumerateArray().Single(output => output.GetProperty("Id").GetString() == id).GetProperty("Original").GetArrayLength() != 768 ||
                !persisted.RootElement.GetProperty("Steps").EnumerateArray().Last().GetProperty("WriteAttempted").GetBoolean())
                throw new InvalidOperationException("Gamma write had no durable original-ramp journal.");
            _ramps[id] = BuildExpected(_original, percentage); _written = id; _postWriteRefreshes = 0;
            return Task.CompletedTask;
        }
        public Task RestoreGammaAsync(CancellationToken token)
        {
            RestoreCalls++;
            if (scenario == "restore-fails") throw new IOException("Simulated restore failure.");
            if (_written is not null) _ramps[_written] = _original.ToArray();
            _written = null; return Task.CompletedTask;
        }
        public void Dispose() { }
    }
}
