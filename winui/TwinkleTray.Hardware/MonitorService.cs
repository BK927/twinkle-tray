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
public sealed class MonitorService : IDisposable
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
            if (display.Wmi is not null)
                WmiProvider.SetBrightness(display.Wmi, percentage);
            else
                WriteVcp(display, BrightnessCode, ToNativeValue(percentage, display.BrightnessMaximum));
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
            WriteVcp(display, ContrastCode, ToNativeValue(percentage, display.ContrastMaximum));
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
            if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, code, out _, out _, out var maximum))
                throw NativeFailure($"VCP 0x{code:X2} could not be read for {display.Snapshot.Name}; no value was written");
            if ((code == BrightnessCode || code == ContrastCode) && (maximum == 0 || value > maximum))
                throw new ArgumentOutOfRangeException(nameof(value), $"The display reports a maximum of {maximum} for VCP 0x{code:X2}.");
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
            var inventory = WmiProvider.Read(errors, cancellationToken);
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
                    devices.Add(new DeviceIdentity(info.DeviceName, info.DeviceName));

                var raw = Array.Empty<NativeMethods.PhysicalMonitor>();
                try
                {
                    if (NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(logicalMonitor, out var count) && count is > 0 and <= 256)
                    {
                        raw = new NativeMethods.PhysicalMonitor[count];
                        if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(logicalMonitor, count, raw))
                        {
                            errors.Add(NativeFailure($"Physical monitor access is unavailable for {info.DeviceName}").Message);
                            CloseRawHandles(raw);
                            raw = [];
                        }
                    }
                    if (raw.Length == 0)
                    {
                        for (var i = 0; i < devices.Count; i++)
                            AddDisplay(replacement, devices[i], i, null, devices[i].Name, inventory, usedWmi, errors, cancellationToken);
                    }
                    else
                    {
                        for (var i = 0; i < raw.Length; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var device = devices[Math.Min(i, devices.Count - 1)];
                            var handle = new PhysicalMonitorHandle(raw[i].Handle);
                            raw[i].Handle = 0; // Ownership has moved to SafeHandle, including error paths.
                            AddDisplay(replacement, device, i, handle, raw[i].Description, inventory, usedWmi, errors, cancellationToken);
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
                AddDisplay(replacement, new DeviceIdentity(key, display.Name), 0, null, display.Name,
                    inventory, usedWmi, errors, cancellationToken);
            }
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
        HashSet<string> usedWmi, List<string> errors, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Identity.Create(device.Id, index);
            if (target.ContainsKey(id)) return;
            var name = string.IsNullOrWhiteSpace(description) ? device.Name : description.Trim();
            if (inventory.Names.TryGetValue(device.Id, out var friendlyName) && !string.IsNullOrWhiteSpace(friendlyName))
                name = friendlyName;
            var brightnessMaximum = 0u;
            var contrastMaximum = 0u;
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
                supportsBrightness = ReadLevel(handle, BrightnessCode, out brightness, out brightnessMaximum);
                cancellationToken.ThrowIfCancellationRequested();
                supportsContrast = ReadLevel(handle, ContrastCode, out var contrastValue, out contrastMaximum);
                if (supportsContrast) contrast = contrastValue;
                if (!supportsBrightness)
                    errors.Add($"{name} did not return a valid DDC/CI brightness value. Enable DDC/CI in the monitor menu if supported.");
            }

            var connection = internalDisplay is not null ? "Internal (WMI)" :
                handle is { IsInvalid: false } ? "DDC/CI" : "Display driver";
            target.Add(id, new DisplayEntry(new MonitorSnapshot(id, name, connection, brightness,
                supportsBrightness, supportsContrast, contrast), handle, internalDisplay, brightnessMaximum, contrastMaximum));
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
                devices.Add(new DeviceIdentity(id, string.IsNullOrWhiteSpace(device.DeviceString) ? adapter : device.DeviceString.Trim()));
        }
        return devices;
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

    private static PhysicalMonitorHandle RequireHandle(DisplayEntry display) =>
        display.Handle is { IsInvalid: false, IsClosed: false } handle ? handle :
            throw new NotSupportedException($"DDC/CI controls are not available for {display.Snapshot.Name}.");

    private static void WriteVcp(DisplayEntry display, byte code, uint value)
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

    private sealed record DeviceIdentity(string Id, string Name);

    private sealed record DisplayEntry(MonitorSnapshot Snapshot, PhysicalMonitorHandle? Handle,
        WmiDisplay? Wmi, uint BrightnessMaximum, uint ContrastMaximum) : IDisposable
    {
        public void Dispose() => Handle?.Dispose();
    }
}
