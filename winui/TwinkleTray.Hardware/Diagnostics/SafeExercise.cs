using System.Runtime.InteropServices;
using System.Text.Json;
using TwinkleTray.Hardware;

// This executable's only real write path is explicitly opted into. The native
// adapter admits brightness/contrast only; it has no power, input, HDR or gamma setter.
internal static class SafeExercise
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            bool fake = arguments.Contains("--exercise-self-test", StringComparer.Ordinal);
            bool real = arguments.Contains("--exercise-safe", StringComparer.Ordinal);
            if (fake == real || arguments.Contains("--read-only", StringComparer.Ordinal))
                throw new ArgumentException("Choose exactly one of --read-only, --exercise-safe, or --exercise-self-test.");
            int reportIndex = Array.IndexOf(arguments, "--report");
            if (reportIndex < 0 || reportIndex + 1 >= arguments.Length || arguments.Count(item => item == "--report") != 1 ||
                !Path.IsPathFullyQualified(arguments[reportIndex + 1]))
                throw new ArgumentException("The exercise requires --report followed by an absolute recovery JSON path.");
            var reportPath = Path.GetFullPath(arguments[reportIndex + 1]);
            var allowed = new HashSet<string>(["--exercise-safe", "--exercise-self-test", "--exercise-gamma", "--report"], StringComparer.Ordinal);
            for (int index = 0; index < arguments.Length; index++)
                if (index != reportIndex + 1 && !allowed.Contains(arguments[index])) throw new ArgumentException("Unknown exercise argument: " + arguments[index]);
            if (File.Exists(reportPath)) throw new IOException("The report already exists. Preserve recovery evidence and choose a new report path.");
            if (fake) return await SelfTestAsync(reportPath);
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || RuntimeInformation.OSArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("Real-device exercise acceptance is limited to native Windows x64.");

            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                using var backend = new NativeBackend();
                var journal = NewJournal(false, arguments.Contains("--exercise-gamma", StringComparer.Ordinal));
                var runner = new Runner(backend, reportPath, journal, TimeSpan.FromMilliseconds(250));
                int exitCode = await runner.RunAsync(cancellation.Token);
                Console.WriteLine($"{journal.Status}: {reportPath}");
                return exitCode;
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static Journal NewJournal(bool simulated, bool gammaRequested) => new()
    {
        Simulated = simulated,
        Gamma = gammaRequested
            ? "Skipped: exact original-ramp export, ownership transfer and exact readback are not exposed by the public monitor service. No gamma write was attempted; external calibration is preserved."
            : "Disabled: no gamma exercise was requested.",
    };

    private sealed class Runner(IBackend backend, string reportPath, Journal journal, TimeSpan retryDelay)
    {
        private bool _durabilityFailed;

        internal async Task<int> RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                var connected = await StableInventoryAsync(cancellationToken);
                foreach (var display in connected)
                {
                    if (!IsOrdinaryDdc(display))
                    {
                        journal.Skipped.Add(display.Name + ": excluded: " + ExclusionReason(display));
                        continue;
                    }
                    // Capture all ordinary peers, including unsupported ones. If any
                    // peer cannot be observed, stop before any test write is allowed.
                    var brightness = await backend.ReadAsync(display.Id, display.BrightnessVcp, cancellationToken);
                    var contrast = await backend.ReadAsync(display.Id, 0x12, cancellationToken);
                    ValidateRaw(brightness); ValidateRaw(contrast);
                    if (brightness.Code == contrast.Code) throw new InvalidOperationException("Brightness and contrast cannot share one code in this exercise.");
                    journal.Displays.Add(new Baseline(display.Id, display.Name, display.DeviceInstanceId, display.DeviceName,
                        new RawControl(brightness.Code, brightness.Current, brightness.Maximum), new RawControl(contrast.Code, contrast.Current, contrast.Maximum)));
                }
                if (journal.Displays.Count == 0)
                {
                    journal.Status = "Skipped";
                    journal.Errors.Add("No eligible ordinary DDC/CI displays were found.");
                    Save(); return 2;
                }
                journal.Status = "BaselineCaptured";
                Save(); // Durably record exact original raw values before any write.
                await VerifyAllAsync(null, cancellationToken);
                foreach (var display in journal.Displays)
                {
                    foreach (var control in new[] { display.Brightness, display.Contrast })
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var requested = TinyChange(control.Original, control.Maximum);
                        if (requested is null)
                        {
                            journal.Skipped.Add($"{display.Name} VCP 0x{control.Code:X2}: no different, nonzero value within two raw points.");
                            continue;
                        }
                        bool proceed = await ExerciseAsync(display, control, requested.Value, cancellationToken);
                        if (!proceed) { journal.FinishedUtc = DateTimeOffset.UtcNow; TrySave(); return 1; }
                    }
                }
                await VerifyAllAsync(null, cancellationToken);
                journal.Status = journal.Steps.Count > 0 ? "Passed" : "Skipped";
                journal.FinishedUtc = DateTimeOffset.UtcNow;
                Save();
                return journal.Steps.Count > 0 ? 0 : 2;
            }
            catch (Exception exception)
            {
                journal.Errors.Add(exception.Message);
                if (journal.Status != "RestoreUncertain") journal.Status = journal.Steps.Any(step => step.WriteAttempted) ? "StoppedRestored" : "FailedNoWrites";
                journal.FinishedUtc = DateTimeOffset.UtcNow;
                TrySave();
                return 1;
            }
        }

        private async Task<bool> ExerciseAsync(Baseline display, RawControl control, uint requested, CancellationToken cancellationToken)
        {
            await VerifyAllAsync(null, cancellationToken);
            var step = new Step { DisplayId = display.Id, Code = control.Code, Original = control.Original, Maximum = control.Maximum, Requested = requested };
            journal.Steps.Add(step);
            journal.Status = "WritePending";
            step.Status = "WritePending";
            step.WriteAttempted = true; // Conservative recovery state even if the process exits inside the native call.
            Save(); // Failure here prevents the test write altogether.
            try
            {
                await backend.WriteAsync(display, control.Code, requested, restoring: false, cancellationToken);
                step.Status = "ReadbackPending";
                Save();
                step.Observed = await ReadExpectedAsync(display.Id, control, requested, cancellationToken, step.Readbacks);
                await VerifyAllAsync(step, cancellationToken);
                step.OtherControlsUnchanged = true;
                step.Status = "AppliedAndIsolated";
            }
            catch (Exception exception)
            {
                step.Error = exception.Message;
                step.Status = "ExerciseFailed";
                journal.Errors.Add($"{display.Name} VCP 0x{control.Code:X2}: {exception.Message}");
            }
            finally
            {
                // Cancellation never skips restoration. The original intent and raw
                // value are already durable even if later journal updates fail.
                step.Restored = await RestoreAsync(display, control, step);
                if (step.Restored)
                {
                    try { await VerifyAllAsync(null, CancellationToken.None); step.FinalAllControlsUnchanged = true; }
                    catch (Exception exception) { step.Error ??= exception.Message; journal.Errors.Add(exception.Message); }
                }
                step.Status = step.Restored ? "Restored" : "RestoreUncertain";
                journal.Status = !step.Restored ? "RestoreUncertain" : step.Error is null && !_durabilityFailed && step.FinalAllControlsUnchanged ? "Running" : "StoppedRestored";
                if (!TrySave() && step.Restored) journal.Status = "StoppedRestored";
            }
            return step.Restored && step.Error is null && step.OtherControlsUnchanged && step.FinalAllControlsUnchanged && !_durabilityFailed;
        }

        private async Task<IReadOnlyList<MonitorSnapshot>> StableInventoryAsync(CancellationToken cancellationToken)
        {
            string? previous = null;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                var connected = await ObserveInventoryAsync("Preflight " + attempt, cancellationToken);
                var signature = JsonSerializer.Serialize(connected.OrderBy(display => display.Id, StringComparer.OrdinalIgnoreCase).Select(display =>
                    new { display.Id, display.DeviceInstanceId, display.DeviceName, display.Name, display.Connection, display.HdrModeKnown, display.HdrActive, display.BrightnessVcp }));
                Save(); // Persist excluded snapshots as well as successful observations.
                if (signature == previous) return connected;
                previous = signature;
                if (attempt < 4) await Task.Delay(retryDelay + retryDelay, cancellationToken);
            }
            throw new InvalidOperationException("No two consecutive identity/connection/HDR inventories were stable across four read-only refreshes. No test write was attempted.");
        }

        private async Task<IReadOnlyList<MonitorSnapshot>> ObserveInventoryAsync(string phase, CancellationToken cancellationToken)
        {
            var connected = await backend.RefreshAsync(cancellationToken);
            journal.Inventories.Add(new Inventory(DateTimeOffset.UtcNow, phase, connected.ToArray(), backend.LastRefreshErrors.ToArray()));
            return connected;
        }

        private async Task<bool> RestoreAsync(Baseline display, RawControl control, Step step)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var restore = new RestoreAttempt { Attempt = attempt };
                step.Restoration.Add(restore);
                try
                {
                    // A fresh handle set and physical identity are mandatory for
                    // every restoration attempt, including after a failed write.
                    var connected = await ObserveInventoryAsync("Restore identity " + attempt, CancellationToken.None);
                    ValidateIdentity(display, connected);
                    var current = await backend.ReadAsync(display.Id, control.Code, CancellationToken.None);
                    ValidateRaw(current);
                    if (current.Maximum != control.Maximum) throw new InvalidOperationException("Control maximum changed; restoration refused.");
                    restore.Before = current.Current;
                    if (current.Current != control.Original)
                    {
                        restore.WritePending = true;
                        TrySave(); // Recovery still takes precedence if the disk fails after the test write.
                        await backend.WriteAsync(display, control.Code, control.Original, restoring: true, CancellationToken.None);
                    }
                    restore.Observed = await ReadExpectedAsync(display.Id, control, control.Original, CancellationToken.None, restore.Readbacks);
                    restore.Succeeded = true;
                    TrySave();
                    return true;
                }
                catch (Exception exception)
                {
                    restore.Error = exception.Message;
                    TrySave();
                    if (attempt < 3) await Task.Delay(retryDelay);
                }
            }
            journal.Errors.Add($"RESTORE UNCERTAIN: {display.Name}, VCP 0x{control.Code:X2}, original raw value {control.Original}. No further test writes are permitted.");
            Console.Error.WriteLine(journal.Errors[^1]);
            return false;
        }

        private async Task<uint> ReadExpectedAsync(string id, RawControl control, uint expected, CancellationToken cancellationToken, List<Reading> observations)
        {
            string failure = "The monitor did not return the expected raw value.";
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var actual = await backend.ReadAsync(id, control.Code, cancellationToken);
                    observations.Add(new Reading(attempt, actual.Current, actual.Maximum, null));
                    if (actual.Maximum != control.Maximum) throw new InvalidOperationException("The monitor's control maximum changed during the exercise.");
                    if (actual.Current == expected) return actual.Current;
                    failure = $"Expected raw {expected}, observed {actual.Current}.";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { failure = exception.Message; observations.Add(new Reading(attempt, null, null, failure)); }
                if (attempt < 5) await Task.Delay(retryDelay, cancellationToken);
            }
            throw new InvalidOperationException("Readback failed after five attempts: " + failure);
        }

        private async Task VerifyAllAsync(Step? changed, CancellationToken cancellationToken)
        {
            var connected = await ObserveInventoryAsync(changed is null ? "Baseline verification" : "Peer isolation verification", cancellationToken);
            var ordinary = connected.Where(IsOrdinaryDdc).Select(display => display.Id).Order(StringComparer.OrdinalIgnoreCase);
            if (!ordinary.SequenceEqual(journal.Displays.Select(display => display.Id).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Connected DDC display topology changed; test writes stopped.");
            foreach (var display in journal.Displays)
            {
                ValidateIdentity(display, connected);
                foreach (var control in new[] { display.Brightness, display.Contrast })
                {
                    if (changed?.DisplayId == display.Id && changed.Code == control.Code) continue;
                    var current = await backend.ReadAsync(display.Id, control.Code, cancellationToken);
                    if (current.Maximum != control.Maximum || current.Current != control.Original)
                        throw new InvalidOperationException($"Non-target control changed: {display.Name} VCP 0x{control.Code:X2}, original {control.Original}/{control.Maximum}, observed {current.Current}/{current.Maximum}. It was not overwritten.");
                }
            }
        }

        private void Save() => Persist(reportPath, journal);
        private bool TrySave()
        {
            try { Save(); return true; }
            catch (Exception exception)
            {
                _durabilityFailed = true;
                Console.Error.WriteLine("Recovery journal update failed; restoring the current control and stopping: " + exception.Message);
                return false;
            }
        }
    }

    private static bool IsOrdinaryDdc(MonitorSnapshot display) => display.Connection == "DDC/CI" && display.HdrModeKnown && !display.HdrActive &&
        !display.DeviceInstanceId.Contains("\\APP", StringComparison.OrdinalIgnoreCase) &&
        !display.Name.Replace(" ", "").Contains("StudioDisplay", StringComparison.OrdinalIgnoreCase);

    private static string ExclusionReason(MonitorSnapshot display) => string.Join("; ", new[]
    {
        display.Connection != "DDC/CI" ? "Connection=" + display.Connection : null,
        !display.HdrModeKnown ? "HDR/SDR mode is unknown" : null,
        display.HdrActive ? "HdrActive=true" : null,
        display.DeviceInstanceId.Contains("\\APP", StringComparison.OrdinalIgnoreCase) ? "Apple device identity" : null,
        display.Name.Replace(" ", "").Contains("StudioDisplay", StringComparison.OrdinalIgnoreCase) ? "Apple Studio Display name" : null,
    }.Where(reason => reason is not null));

    private static void ValidateIdentity(Baseline original, IReadOnlyList<MonitorSnapshot> connected)
    {
        var current = connected.SingleOrDefault(display => string.Equals(display.Id, original.Id, StringComparison.OrdinalIgnoreCase));
        if (current is null || !IsOrdinaryDdc(current) || !string.Equals(current.DeviceInstanceId, original.DeviceInstanceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.DeviceName, original.DeviceName, StringComparison.OrdinalIgnoreCase) || current.BrightnessVcp != original.Brightness.Code)
            throw new InvalidOperationException("The original physical display identity or output mapping is no longer present; no write was sent.");
    }

    private static void ValidateRaw(VcpFeature feature)
    {
        if (feature.Maximum == 0 || feature.Maximum > ushort.MaxValue || feature.Current > feature.Maximum)
            throw new InvalidOperationException($"VCP 0x{feature.Code:X2} has no valid observable raw range. No exercise is possible.");
    }

    internal static uint? TinyChange(uint original, uint maximum)
    {
        if (maximum == 0 || maximum > ushort.MaxValue || original > maximum) return null;
        uint candidate = original <= maximum - Math.Min(2u, maximum) ? Math.Min(maximum, original + 2) : original > 2 ? original - 2 : 1;
        candidate = Math.Clamp(candidate, 1u, maximum);
        return candidate != original && Math.Abs((long)candidate - original) <= 2 ? candidate : null;
    }

    private static void Persist(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private interface IBackend : IDisposable
    {
        IReadOnlyList<string> LastRefreshErrors { get; }
        Task<IReadOnlyList<MonitorSnapshot>> RefreshAsync(CancellationToken cancellationToken);
        Task<VcpFeature> ReadAsync(string id, byte code, CancellationToken cancellationToken);
        Task WriteAsync(Baseline original, byte code, uint value, bool restoring, CancellationToken cancellationToken);
    }

    private sealed class NativeBackend : IBackend
    {
        private readonly MonitorService _service = new();
        public IReadOnlyList<string> LastRefreshErrors => _service.LastRefreshErrors;
        internal NativeBackend() => _service.Configure(new HardwareOptions { DisableAppleStudio = true });
        public Task<IReadOnlyList<MonitorSnapshot>> RefreshAsync(CancellationToken cancellationToken) => _service.RefreshAsync(cancellationToken);
        public Task<VcpFeature> ReadAsync(string id, byte code, CancellationToken cancellationToken) => _service.GetVcpAsync(id, code, cancellationToken);
        public async Task WriteAsync(Baseline original, byte code, uint value, bool restoring, CancellationToken cancellationToken)
        {
            if (code != original.Brightness.Code && code != 0x12) throw new InvalidOperationException("Only brightness and contrast are allowed.");
            var control = code == original.Brightness.Code ? original.Brightness : original.Contrast;
            if (restoring ? value != control.Original : value == 0 || value > control.Maximum || Math.Abs((long)value - control.Original) > 2)
                throw new InvalidOperationException("The write is outside the tiny-change or exact-restoration limits.");
            // Identity validation and the fresh native handle are kept adjacent to
            // the actual write, rather than relying on an earlier enumeration.
            ValidateIdentity(original, await _service.RefreshAsync(cancellationToken));
            if (restoring) await _service.SetVcpAsync(original.Id, code, value, cancellationToken);
            else
            {
                double percentage = 100d * value / control.Maximum;
                if (code == original.Brightness.Code) await _service.SetBrightnessAsync(original.Id, percentage, cancellationToken);
                else await _service.SetContrastAsync(original.Id, percentage, cancellationToken);
            }
        }
        public void Dispose() => _service.Dispose(); // No gamma writes occurred, so disposal cannot restore/change gamma.
    }

    private sealed class Journal
    {
        public int FormatVersion { get; init; } = 1;
        public Guid RunId { get; init; } = Guid.NewGuid();
        public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedUtc { get; set; }
        public string Status { get; set; } = "Starting";
        public bool Simulated { get; init; }
        public string Scope { get; init; } = "Windows x64; connected ordinary SDR DDC monitors; brightness/contrast only; at most two raw points; exact restoration.";
        public string Gamma { get; init; } = "Disabled";
        public List<Baseline> Displays { get; } = [];
        public List<Inventory> Inventories { get; } = [];
        public List<Step> Steps { get; } = [];
        public List<string> Skipped { get; } = [];
        public List<string> Errors { get; } = [];
    }
    private sealed record Baseline(string Id, string Name, string DeviceInstanceId, string DeviceName, RawControl Brightness, RawControl Contrast);
    private sealed record Inventory(DateTimeOffset CapturedUtc, string Phase, IReadOnlyList<MonitorSnapshot> Displays, IReadOnlyList<string> LastRefreshErrors);
    private sealed record RawControl(byte Code, uint Original, uint Maximum);
    private sealed record Reading(int Attempt, uint? Current, uint? Maximum, string? Error);
    private sealed class Step
    {
        public required string DisplayId { get; init; }
        public byte Code { get; init; }
        public uint Original { get; init; }
        public uint Maximum { get; init; }
        public uint Requested { get; init; }
        public string TestApi { get; init; } = "Public percentage brightness/contrast setter; exact raw VCP restoration.";
        public string Status { get; set; } = "Pending";
        public bool WriteAttempted { get; set; }
        public uint? Observed { get; set; }
        public bool OtherControlsUnchanged { get; set; }
        public bool Restored { get; set; }
        public bool FinalAllControlsUnchanged { get; set; }
        public string? Error { get; set; }
        public List<Reading> Readbacks { get; } = [];
        public List<RestoreAttempt> Restoration { get; } = [];
    }
    private sealed class RestoreAttempt
    {
        public int Attempt { get; init; }
        public uint? Before { get; set; }
        public bool WritePending { get; set; }
        public uint? Observed { get; set; }
        public bool Succeeded { get; set; }
        public string? Error { get; set; }
        public List<Reading> Readbacks { get; } = [];
    }

    private static async Task<int> SelfTestAsync(string reportPath)
    {
        var results = new List<object>();
        var directory = Path.Combine(Path.GetDirectoryName(reportPath)!, "exercise-fixtures-" + Guid.NewGuid().ToString("N"));
        foreach (var scenario in new[] { "success", "write-throws-after-change", "restore-fails", "identity-changes", "peer-changes", "stale-readback", "cancel-after-write", "initial-inventory-transient", "unknown-color-mode" })
        {
            var path = Path.Combine(directory, scenario + ".json");
            using var cancellation = new CancellationTokenSource();
            using var fake = new FakeBackend(scenario, path, cancellation);
            var journal = NewJournal(true, false);
            int exitCode = await new Runner(fake, path, journal, TimeSpan.Zero).RunAsync(cancellation.Token);
            bool passed = scenario switch
            {
                "success" => exitCode == 0 && fake.AllOriginal && journal.Steps.Count == 4 && journal.Steps.All(step => step.Restored),
                "write-throws-after-change" => exitCode == 1 && fake.AllOriginal && journal.Steps.Count == 1 && journal.Steps[0].Restored,
                "restore-fails" => exitCode == 1 && journal.Status == "RestoreUncertain" && journal.Steps.Count == 1 && journal.Steps[0].Restoration.Count == 3,
                "identity-changes" => exitCode == 1 && journal.Status == "RestoreUncertain" && journal.Steps.Count == 1 && fake.RestoreWrites == 0,
                "peer-changes" => exitCode == 1 && journal.Steps.Count == 1 && journal.Steps[0].Restored && !journal.Steps[0].FinalAllControlsUnchanged,
                "stale-readback" => exitCode == 0 && fake.AllOriginal && journal.Steps[0].Readbacks.Count >= 2,
                "cancel-after-write" => exitCode == 1 && fake.AllOriginal && journal.Steps.Count == 1 && journal.Steps[0].Restored,
                "initial-inventory-transient" => exitCode == 0 && fake.AllOriginal && journal.Steps.Count == 4 && journal.Inventories.Count(item => item.Phase.StartsWith("Preflight")) == 3,
                "unknown-color-mode" => exitCode == 2 && fake.AllOriginal && journal.Steps.Count == 0,
                _ => false,
            };
            if (!passed) throw new InvalidOperationException("Fake exercise self-test failed: " + scenario);
            results.Add(new { Scenario = scenario, Passed = true, Report = path });
        }
        foreach (uint maximum in new uint[] { 1, 2, 3, 100, ushort.MaxValue })
            foreach (uint original in new uint[] { 0, 1, maximum })
                if (original <= maximum && TinyChange(original, maximum) is { } requested && (requested == original || requested == 0 || requested > maximum || Math.Abs((long)original - requested) > 2))
                    throw new InvalidOperationException("Tiny-change range invariant failed.");
        Persist(reportPath, new { Status = "Passed", Simulated = true, HardwareCalls = 0, Results = results });
        Console.WriteLine("All nine fake exercise scenarios, durable pre-write journals, and tiny-change range checks passed. No hardware backend was created.");
        return 0;
    }

    private sealed class FakeBackend(string scenario, string reportPath, CancellationTokenSource cancellation) : IBackend
    {
        public IReadOnlyList<string> LastRefreshErrors => [];
        private readonly Dictionary<(string, byte), uint> _values = new() { [("one", 0x10)] = 100, [("one", 0x12)] = 70, [("two", 0x10)] = 50, [("two", 0x12)] = 50 };
        private bool _testWriteOccurred, _staleRead;
        private int _refreshes;
        public int RestoreWrites { get; private set; }
        internal bool AllOriginal => _values[("one", 0x10)] == 100 && _values[("one", 0x12)] == 70 && _values[("two", 0x10)] == 50 && _values[("two", 0x12)] == 50;
        public Task<IReadOnlyList<MonitorSnapshot>> RefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _refreshes++;
            IReadOnlyList<MonitorSnapshot> displays = new[] { "one", "two" }.Select(id => new MonitorSnapshot(id, "Fake " + id,
                scenario == "initial-inventory-transient" && _refreshes == 1 && id == "one" ? "Display driver" : "DDC/CI", 50, true, true, 50)
            { DeviceInstanceId = "DISPLAY\\FAKE\\" + id + (scenario == "identity-changes" && _testWriteOccurred && id == "one" ? "-changed" : ""), DeviceName = id, HdrModeKnown = scenario != "unknown-color-mode" }).ToArray();
            return Task.FromResult(displays);
        }
        public Task<VcpFeature> ReadAsync(string id, byte code, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint value = _values[(id, code)];
            if (scenario == "stale-readback" && _testWriteOccurred && !_staleRead && id == "one" && code == 0x10) { _staleRead = true; value = 100; }
            return Task.FromResult(new VcpFeature(code, "Fake", value, 100, []));
        }
        public Task WriteAsync(Baseline original, byte code, uint value, bool restoring, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var persisted = JsonDocument.Parse(File.ReadAllText(reportPath)))
            {
                var saved = persisted.RootElement.GetProperty("Displays").EnumerateArray().Single(display => display.GetProperty("Id").GetString() == original.Id);
                string controlName = code == original.Brightness.Code ? "Brightness" : "Contrast";
                uint expectedOriginal = code == original.Brightness.Code ? original.Brightness.Original : original.Contrast.Original;
                if (saved.GetProperty(controlName).GetProperty("Original").GetUInt32() != expectedOriginal ||
                    !persisted.RootElement.GetProperty("Steps").EnumerateArray().Last().GetProperty("WriteAttempted").GetBoolean())
                    throw new InvalidOperationException("A simulated write was attempted without a durable recovery journal.");
            }
            if (restoring)
            {
                RestoreWrites++;
                if (scenario == "restore-fails") throw new IOException("Simulated failed restoration.");
            }
            _values[(original.Id, code)] = value;
            if (!restoring)
            {
                _testWriteOccurred = true;
                if (scenario == "peer-changes") _values[("two", 0x12)] = 51;
                if (scenario == "write-throws-after-change") throw new IOException("Simulated write changed hardware then threw.");
                if (scenario == "cancel-after-write") cancellation.Cancel();
            }
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }
}
