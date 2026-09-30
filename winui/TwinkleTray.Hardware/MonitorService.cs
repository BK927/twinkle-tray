using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TwinkleTray.Hardware;

/// <summary>Reads and controls physical monitors through Windows DDC/CI and WMI.</summary>
/// <remarks>
/// Operations run on a worker thread and are serialized. Cancellation is honored before
/// native calls; Windows display drivers cannot be interrupted during a native call.
/// Refresh after a display connection, disconnection, resume, or driver configuration change.
/// A refresh only reads hardware. It never restores or changes brightness.
/// </remarks>
public sealed partial class MonitorService : IDisposable
{
    private const byte BrightnessCode = 0x10;
    private const byte ContrastCode = 0x12;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, DisplayEntry> _displays = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _lastRefreshErrors = Array.Empty<string>();
    private int _disposed;

    /// <summary>Nonfatal driver/provider failures from the last completed refresh.</summary>
    public IReadOnlyList<string> LastRefreshErrors => Volatile.Read(ref _lastRefreshErrors);

    public Task<IReadOnlyList<MonitorSnapshot>> RefreshAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => Refresh(cancellationToken), cancellationToken);

    public Task SetBrightnessAsync(string id, double percentage, CancellationToken cancellationToken = default)
    {
        ValidatePercentage(percentage);
        return RunAsync(() =>
        {
            var display = Find(id);
            if (!display.Snapshot.SupportsBrightness)
                throw new NotSupportedException($"Brightness control is not available for {display.Snapshot.Name}.");
            cancellationToken.ThrowIfCancellationRequested();
            if (display.Apple is not null)
            {
                if (Volatile.Read(ref _options).DisableAppleStudio) throw new NotSupportedException("Apple Studio Display control is disabled in settings.");
                AppleStudioDisplay.SetBrightness(display.Apple, percentage);
            }
            else if (display.Wmi is not null)
            {
                if (Volatile.Read(ref _options).DisableWmi) throw new NotSupportedException("WMI brightness is disabled in settings.");
                WmiProvider.SetBrightness(display.Wmi, percentage);
            }
            else if (display.HighLevelBrightness)
            {
                var value = display.BrightnessMinimum + ToNativeValue(percentage, display.BrightnessMaximum - display.BrightnessMinimum);
                if (!NativeMethods.SetMonitorBrightness(RequireHandle(display), value)) throw NativeFailure($"Brightness could not be changed for {display.Snapshot.Name}");
            }
            else
                WriteVcp(display, display.BrightnessVcp, ToNativeValue(percentage, display.BrightnessMaximum));
            return true;
        }, cancellationToken);
    }

    public Task SetContrastAsync(string id, double percentage, CancellationToken cancellationToken = default)
    {
        ValidatePercentage(percentage);
        return RunAsync(() =>
        {
            var display = Find(id);
            if (!display.Snapshot.SupportsContrast)
                throw new NotSupportedException($"Contrast control is not available for {display.Snapshot.Name}.");
            cancellationToken.ThrowIfCancellationRequested();
            if (display.HighLevelContrast)
            {
                var value = display.ContrastMinimum + ToNativeValue(percentage, display.ContrastMaximum - display.ContrastMinimum);
                if (!NativeMethods.SetMonitorContrast(RequireHandle(display), value)) throw NativeFailure($"Contrast could not be changed for {display.Snapshot.Name}");
            }
            else WriteVcp(display, ContrastCode, ToNativeValue(percentage, display.ContrastMaximum));
            return true;
        }, cancellationToken);
    }

    /// <summary>Sets a raw MCCS value after the monitor successfully answers a read for that VCP code.</summary>
    public Task SetVcpAsync(string id, byte code, uint value, CancellationToken cancellationToken = default)
    {
        if (value > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "MCCS VCP values must fit in 16 bits.");
        return RunAsync(() =>
        {
            var display = Find(id);
            var handle = RequireHandle(display);
            WaitBeforeVcpRead(Volatile.Read(ref _options), cancellationToken);
            if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, code, out _, out _, out var maximum))
                throw NativeFailure($"VCP 0x{code:X2} could not be read for {display.Snapshot.Name}; no value was written");
            if ((VcpCapabilities.IsContinuous(code) || code == display.BrightnessVcp) && (maximum == 0 || value > maximum))
                throw new ArgumentOutOfRangeException(nameof(value), $"The display reports a maximum of {maximum} for VCP 0x{code:X2}.");
            if (display.Capabilities?.TryGetValue(code, out var allowed) == true && allowed.Count > 0 && !allowed.Contains(value))
                throw new ArgumentOutOfRangeException(nameof(value), $"The monitor does not advertise value {value} for VCP 0x{code:X2}.");
            cancellationToken.ThrowIfCancellationRequested();
            WriteVcp(display, code, value);
            return true;
        }, cancellationToken);
    }

    /// <summary>Requests MCCS power-off (VCP D6, value 5) for one external monitor.</summary>
    /// <remarks>Availability and wake behavior depend on the monitor's firmware.</remarks>
    public Task PowerOffAsync(string id, CancellationToken cancellationToken = default) =>
        SetVcpAsync(id, 0xD6, 5, cancellationToken);

    private async Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await Task.Run(() =>
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<MonitorSnapshot> Refresh(CancellationToken cancellationToken)
    {
        var replacement = new Dictionary<string, DisplayEntry>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        try
        {
            var options = Volatile.Read(ref _options);
            var inventory = options.DisableWmi ? new WmiInventory([], [], []) : WmiProvider.Read(errors, cancellationToken);
            var wmi = inventory.BrightnessDisplays;
            var usedWmi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var logicalMonitors = new List<nint>();
            NativeMethods.MonitorEnumProc callback = (nint monitor, nint hdc, ref NativeMethods.Rect rect, nint data) =>
            {
                logicalMonitors.Add(monitor);
                return true;
            };
            if (!NativeMethods.EnumDisplayMonitors(0, 0, callback, 0))
                throw NativeFailure("Windows could not enumerate connected displays");

            foreach (var logicalMonitor in logicalMonitors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
                if (!NativeMethods.GetMonitorInfo(logicalMonitor, ref info))
                {
                    errors.Add(NativeFailure("A display disappeared while its details were being read").Message);
                    continue;
                }
                var devices = GetDeviceIdentities(info.DeviceName);
                if (devices.Count == 0)
                    devices.Add(new DeviceIdentity(info.DeviceName, info.DeviceName, info.DeviceName));

                var raw = Array.Empty<NativeMethods.PhysicalMonitor>();
                try
                {
                    if (!options.DisableDdc)
                        raw = ReadPhysicalMonitors(logicalMonitor, info.DeviceName, errors, cancellationToken);
                    if (raw.Length == 0)
                    {
                        for (var i = 0; i < devices.Count; i++)
                            AddDisplay(replacement, devices[i], i, null, devices[i].Name, inventory, usedWmi, options, errors, cancellationToken);
                    }
                    else
                    {
                        for (var i = 0; i < raw.Length; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var device = devices[Math.Min(i, devices.Count - 1)];
                            var handle = new PhysicalMonitorHandle(raw[i].Handle);
                            raw[i].Handle = 0; // Ownership has moved to SafeHandle, including error paths.
                            AddDisplay(replacement, device, i, handle, raw[i].Description, inventory, usedWmi, options, errors, cancellationToken);
                        }
                    }
                }
                finally
                {
                    CloseRawHandles(raw);
                }
            }

            // WMI can expose a built-in panel that has no DDC/CI physical handle.
            foreach (var (key, display) in wmi)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (usedWmi.Contains(key)) continue;
                AddDisplay(replacement, new DeviceIdentity(key, display.Name, ""), 0, null, display.Name,
                    inventory, usedWmi, options, errors, cancellationToken);
            }
            EnrichDisplays(replacement, inventory, options, errors, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshots = Array.AsReadOnly(replacement.Values.Select(display => display.Snapshot).ToArray());
            var previous = _displays;
            _displays = replacement;
            replacement = previous;
            Volatile.Write(ref _lastRefreshErrors, Array.AsReadOnly(errors.ToArray()));
            return snapshots;
        }
        finally
        {
            // Both a successful swap and an aborted refresh release every superseded handle.
            foreach (var display in replacement.Values) display.Dispose();
        }
    }

    private static void AddDisplay(Dictionary<string, DisplayEntry> target, DeviceIdentity device, int index,
        PhysicalMonitorHandle? handle, string description, WmiInventory inventory,
        HashSet<string> usedWmi, HardwareOptions options, List<string> errors, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Identity.Create(device.Id, index);
            var brightnessCode = BrightnessVcpFor(options, id, device.Id);
            if (target.ContainsKey(id)) return;
            var name = string.IsNullOrWhiteSpace(description) ? device.Name : description.Trim();
            if (inventory.Names.TryGetValue(device.Id, out var friendlyName) && !string.IsNullOrWhiteSpace(friendlyName))
                name = friendlyName;
            var brightnessMaximum = 0u;
            var contrastMaximum = 0u;
            var brightnessMinimum = 0u;
            var contrastMinimum = 0u;
            bool highLevelBrightness = false, highLevelContrast = false;
            var brightness = 0d;
            double? contrast = null;
            WmiDisplay? internalDisplay = null;
            var supportsBrightness = false;
            var supportsContrast = false;

            if (inventory.BrightnessDisplays.TryGetValue(device.Id, out var candidate) && usedWmi.Add(device.Id))
            {
                internalDisplay = candidate;
                name = candidate.Name;
                brightness = candidate.Brightness;
                supportsBrightness = candidate.MethodPath is not null;
                // WMI owns internal-panel brightness; do not probe that panel through DDC.
                handle?.Dispose();
                handle = null;
            }
            else if (handle is { IsInvalid: false })
            {
                WaitBeforeVcpRead(options, cancellationToken);
                supportsBrightness = ReadLevel(handle, brightnessCode, out brightness, out brightnessMaximum);
                cancellationToken.ThrowIfCancellationRequested();
                WaitBeforeVcpRead(options, cancellationToken);
                supportsContrast = ReadLevel(handle, ContrastCode, out var contrastValue, out contrastMaximum);
                if ((!supportsBrightness || !supportsContrast) && NativeMethods.GetMonitorCapabilities(handle, out var caps, out _))
                {
                    if (!supportsBrightness && brightnessCode == BrightnessCode && (caps & 2) != 0 &&
                        NativeMethods.GetMonitorBrightness(handle, out var minimum, out var current, out var maximum) &&
                        maximum > minimum && current >= minimum && current <= maximum)
                    {
                        supportsBrightness = highLevelBrightness = true;
                        brightnessMinimum = minimum; brightnessMaximum = maximum;
                        brightness = Math.Round(100d * (current - minimum) / (maximum - minimum), 1);
                    }
                    if (!supportsContrast && (caps & 4) != 0 &&
                        NativeMethods.GetMonitorContrast(handle, out var minContrast, out var currentContrast, out var maxContrast) &&
                        maxContrast > minContrast && currentContrast >= minContrast && currentContrast <= maxContrast)
                    {
                        supportsContrast = highLevelContrast = true;
                        contrastMinimum = minContrast; contrastMaximum = maxContrast;
                        contrastValue = Math.Round(100d * (currentContrast - minContrast) / (maxContrast - minContrast), 1);
                    }
                }
                if (supportsContrast) contrast = contrastValue;
                if (!supportsBrightness)
                    errors.Add($"{name} did not return a valid DDC/CI brightness value. Enable DDC/CI in the monitor menu if supported.");
            }

            var connection = internalDisplay is not null ? "Internal (WMI)" :
                handle is { IsInvalid: false } ? "DDC/CI" : "Display driver";
            target.Add(id, new DisplayEntry(new MonitorSnapshot(id, name, connection, brightness,
                supportsBrightness, supportsContrast, contrast) { DeviceName = device.DeviceName, DeviceInstanceId = device.Id, BrightnessVcp = brightnessCode }, handle, internalDisplay, brightnessMaximum, contrastMaximum)
                { BrightnessVcp = brightnessCode, HighLevelBrightness = highLevelBrightness, HighLevelContrast = highLevelContrast,
                    BrightnessMinimum = brightnessMinimum, ContrastMinimum = contrastMinimum });
            handle = null; // DisplayEntry owns it from here.
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static List<DeviceIdentity> GetDeviceIdentities(string adapter)
    {
        var devices = new List<DeviceIdentity>();
        for (uint index = 0; ; index++)
        {
            var device = new NativeMethods.DisplayDevice { Size = (uint)Marshal.SizeOf<NativeMethods.DisplayDevice>() };
            if (!NativeMethods.EnumDisplayDevices(adapter, index, ref device, NativeMethods.GetDeviceInterfaceName)) break;
            if ((device.StateFlags & NativeMethods.DisplayDeviceActive) == 0 ||
                (device.StateFlags & NativeMethods.DisplayDeviceMirroringDriver) != 0) continue;
            var id = Identity.Normalize(device.DeviceId);
            if (id.Length == 0) id = device.DeviceName;
            if (devices.All(existing => !StringComparer.OrdinalIgnoreCase.Equals(existing.Id, id)))
                devices.Add(new DeviceIdentity(id, string.IsNullOrWhiteSpace(device.DeviceString) ? adapter : device.DeviceString.Trim(), adapter));
        }
        return devices;
    }

    private static NativeMethods.PhysicalMonitor[] ReadPhysicalMonitors(nint logicalMonitor, string deviceName,
        List<string> errors, CancellationToken cancellationToken)
    {
        string? failure = null;
        // Some drivers initially expose the logical display before its DDC handle.
        // Retry only this read-only enumeration; monitor writes are never retried here.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0 && cancellationToken.WaitHandle.WaitOne(150))
                cancellationToken.ThrowIfCancellationRequested();
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(logicalMonitor, out var count))
            {
                failure = NativeFailure($"Physical monitor access is unavailable for {deviceName}").Message;
                continue;
            }
            if (count == 0)
            {
                // Internal panels and unsupported outputs legitimately have no DDC handles.
                failure = null;
                continue;
            }
            if (count > 256)
            {
                failure = $"Windows returned an invalid physical monitor count for {deviceName}.";
                continue;
            }
            var candidate = new NativeMethods.PhysicalMonitor[count];
            bool transferred = false;
            try
            {
                if (NativeMethods.GetPhysicalMonitorsFromHMONITOR(logicalMonitor, count, candidate))
                {
                    if (candidate.All(monitor => monitor.Handle != 0 && monitor.Handle != -1))
                    {
                        transferred = true;
                        return candidate;
                    }
                    failure = $"Windows returned an incomplete physical monitor handle list for {deviceName}.";
                }
                else failure = NativeFailure($"Physical monitor access is unavailable for {deviceName}").Message;
            }
            finally
            {
                // A failed native call may still have returned some handles.
                if (!transferred) CloseRawHandles(candidate);
            }
        }
        if (failure is not null) errors.Add(failure);
        return [];
    }

    private static bool ReadLevel(PhysicalMonitorHandle handle, byte code, out double percentage, out uint maximum)
    {
        percentage = 0;
        maximum = 0;
        if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, code, out _, out var current, out var max) ||
            max == 0 || max > ushort.MaxValue || current > max) return false;
        maximum = max;
        percentage = Math.Round(100d * current / max, 1);
        return true;
    }

    private DisplayEntry Find(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _displays.TryGetValue(id, out var display) ? display :
            throw new InvalidOperationException("The selected display is no longer available. Refresh displays and try again.");
    }

    private PhysicalMonitorHandle RequireHandle(DisplayEntry display)
    {
        if (Volatile.Read(ref _options).DisableDdc) throw new NotSupportedException("DDC/CI control is disabled in settings.");
        return display.Handle is { IsInvalid: false, IsClosed: false } handle ? handle :
            throw new NotSupportedException($"DDC/CI controls are not available for {display.Snapshot.Name}.");
    }

    private void WriteVcp(DisplayEntry display, byte code, uint value)
    {
        if (!NativeMethods.SetVCPFeature(RequireHandle(display), code, value))
            throw NativeFailure($"The display did not accept VCP 0x{code:X2} for {display.Snapshot.Name}. Refresh displays and try again");
    }

    private static InvalidOperationException NativeFailure(string message)
    {
        var error = Marshal.GetLastWin32Error();
        if (error == 0)
            return new InvalidOperationException($"{message}. The driver did not provide an extended error code.");
        return new InvalidOperationException($"{message} (Windows error {error}: {new Win32Exception(error).Message}).",
            new Win32Exception(error));
    }

    private static uint ToNativeValue(double percentage, uint maximum) =>
        (uint)Math.Round(percentage * maximum / 100d, MidpointRounding.AwayFromZero);

    private static void ValidatePercentage(double percentage)
    {
        if (!double.IsFinite(percentage) || percentage < 0 || percentage > 100)
            throw new ArgumentOutOfRangeException(nameof(percentage), "The level must be a finite percentage from 0 to 100.");
    }

    private static void CloseRawHandles(NativeMethods.PhysicalMonitor[] monitors)
    {
        for (var i = 0; i < monitors.Length; i++)
        {
            if (monitors[i].Handle == 0 || monitors[i].Handle == -1) continue;
            NativeMethods.DestroyPhysicalMonitor(monitors[i].Handle);
            monitors[i].Handle = 0;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _gate.Wait();
        try
        {
            var gammaErrors = _gamma.Restore();
            if (gammaErrors.Count > 0) Volatile.Write(ref _lastRefreshErrors, gammaErrors);
            foreach (var display in _displays.Values) display.Dispose();
            _displays.Clear();
        }
        finally
        {
            // Leave the gate usable so already-queued callers can observe ObjectDisposedException.
            _gate.Release();
        }
        GC.SuppressFinalize(this);
    }

    private sealed record DeviceIdentity(string Id, string Name, string DeviceName);

    private sealed record DisplayEntry(MonitorSnapshot Snapshot, PhysicalMonitorHandle? Handle,
        WmiDisplay? Wmi, uint BrightnessMaximum, uint ContrastMaximum) : IDisposable
    {
        internal byte BrightnessVcp { get; init; } = BrightnessCode;
        internal ColorDisplay? Color { get; init; }
        internal AppleDisplay? Apple { get; init; }
        internal bool HighLevelBrightness { get; init; }
        internal bool HighLevelContrast { get; init; }
        internal uint BrightnessMinimum { get; init; }
        internal uint ContrastMinimum { get; init; }
        internal Dictionary<byte, IReadOnlyList<uint>>? Capabilities { get; set; }
        public void Dispose() => Handle?.Dispose();
    }
}
