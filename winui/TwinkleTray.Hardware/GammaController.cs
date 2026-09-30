using System.Runtime.InteropServices;

namespace TwinkleTray.Hardware;

// All access is protected by MonitorService's operation gate. A cloned output shares
// one GDI ramp, so ownership is per GDI device, never per physical monitor handle.
internal sealed class GammaController
{
    private readonly Dictionary<string, RampState> _states = new(StringComparer.OrdinalIgnoreCase);

    internal double? Read(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return null;
        using var dc = DeviceContext.Open(deviceName);
        if (dc.Handle == 0) return null;
        var ramp = ReadRamp(dc.Handle);
        if (ramp is null) return null;
        if (!_states.TryGetValue(deviceName, out var state))
            _states[deviceName] = state = new RampState(ramp);
        return Measure(state.Original, ramp);
    }

    internal void Set(string deviceName, string deviceId, double percentage)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) throw new NotSupportedException("This display has no GDI output for software brightness.");
        var topology = DisplayColor.Read([]);
        if (!topology.TryGetValue(deviceId, out var target) || !string.Equals(target.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The display connection changed. Refresh displays before adjusting software brightness.");
        if (target.HdrActive) throw new NotSupportedException("GDI software brightness is unavailable while HDR is active.");
        using var dc = DeviceContext.Open(deviceName);
        if (dc.Handle == 0) throw new InvalidOperationException("Windows could not open this display's gamma ramp.");
        var current = ReadRamp(dc.Handle) ?? throw new NotSupportedException("The display driver does not provide a gamma ramp.");
        if (!_states.TryGetValue(deviceName, out var state)) _states[deviceName] = state = new RampState(current);
        if (state.Written && state.OwnerDeviceId != deviceId &&
            (!topology.TryGetValue(state.OwnerDeviceId, out var owner) || !string.Equals(owner.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)))
            _states[deviceName] = state = new RampState(current); // The GDI name was reused for another physical output.
        if (!state.Written)
        {
            state.Original = current; // Preserve the exact calibration present at the first write.
            state.OwnerDeviceId = deviceId;
        }
        var requested = Build(state.Original, Math.Clamp(percentage, 20, 100));
        state.Written = true;
        state.Applied = requested;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (SetDeviceGammaRamp(dc.Handle, requested) && ReadRamp(dc.Handle) is { } actual && Match(requested, actual)) return;
            if (attempt < 2) Thread.Sleep(20 * (attempt + 1));
        }
        // Roll back a partially applied/rejected ramp, retaining ownership if rollback fails.
        if (SetDeviceGammaRamp(dc.Handle, state.Original) && ReadRamp(dc.Handle) is { } restored && Match(state.Original, restored))
            state.Written = false;
        throw new InvalidOperationException("The display driver did not apply the requested software brightness. The original ramp was restored where the driver allowed it.");
    }

    internal IReadOnlyList<string> Restore(Func<string, bool>? predicate = null)
    {
        var errors = new List<string>();
        var owned = _states.Where(pair => pair.Value.Written && (predicate is null || predicate(pair.Key))).ToArray();
        if (owned.Length == 0) return errors;
        var topology = DisplayColor.Read(errors);
        foreach (var (name, state) in owned)
        {
            if (!topology.TryGetValue(state.OwnerDeviceId, out var target) || !string.Equals(target.DeviceName, name, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"The original gamma ramp for {name} was not applied because its physical display connection changed.");
                continue;
            }
            using var dc = DeviceContext.Open(name);
            var current = dc.Handle == 0 ? null : ReadRamp(dc.Handle);
            if (current is null) { errors.Add($"The original gamma ramp for {name} could not be restored because the display is unavailable."); continue; }
            if (state.Applied is not null && !Match(current, state.Applied) && !Match(current, state.Original))
            {
                // Do not undo a later color-calibration change made outside this app.
                state.Written = false;
                state.Original = current;
                errors.Add($"Gamma restoration for {name} was skipped because another application changed its ramp.");
                continue;
            }
            if (!SetDeviceGammaRamp(dc.Handle, state.Original) || ReadRamp(dc.Handle) is not { } restored || !Match(state.Original, restored))
            {
                errors.Add($"Windows did not restore the original gamma ramp for {name}.");
                continue;
            }
            state.Written = false;
        }
        return errors;
    }

    internal static ushort[] Build(ushort[] baseline, double brightness)
    {
        var requestedScale = (60 + (brightness - 20) * .5) / 100d;
        var peakScale = Math.Max(.7, requestedScale);
        var exponent = requestedScale < .7 ? Math.Log(requestedScale * .5 / peakScale) / Math.Log(.5) : 1;
        return baseline.Select(value => (ushort)Math.Round(Math.Min(1, peakScale * Math.Pow(value / 65535d, exponent)) * 65535)).ToArray();
    }

    internal static double Measure(ushort[] baseline, ushort[] current)
    {
        var index = Enumerable.Range(256, 256).MinBy(i => Math.Abs(baseline[i] - 32768));
        if (baseline[index] == 0) return 100;
        return Math.Clamp(Math.Round(20 + ((double)current[index] / baseline[index] * 100 - 60) * 2), 20, 100);
    }

    private static bool Match(ushort[] left, ushort[] right) => left.Length == right.Length && left.Zip(right).All(pair => Math.Abs(pair.First - pair.Second) <= 256);
    private static ushort[]? ReadRamp(nint dc) { var ramp = new ushort[768]; return GetDeviceGammaRamp(dc, ramp) ? ramp : null; }
    private sealed class RampState(ushort[] original) { internal ushort[] Original = original; internal ushort[]? Applied; internal bool Written; internal string OwnerDeviceId = ""; }
    private sealed class DeviceContext(nint handle) : IDisposable
    {
        internal nint Handle { get; } = handle;
        internal static DeviceContext Open(string name) => new(CreateDC("DISPLAY", name, null, 0));
        public void Dispose() { if (Handle != 0) DeleteDC(Handle); }
    }
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateDCW")] private static extern nint CreateDC(string driver, string device, string? output, nint initialization);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDeviceGammaRamp(nint dc, [Out] ushort[] ramp);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetDeviceGammaRamp(nint dc, ushort[] ramp);
}
