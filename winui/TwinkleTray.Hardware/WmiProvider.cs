using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

namespace TwinkleTray.Hardware;

internal sealed record WmiDisplay(string InstanceName, string Name, byte Brightness, byte[] Levels, string? MethodPath);
internal sealed record WmiInventory(Dictionary<string, WmiDisplay> BrightnessDisplays, Dictionary<string, string> Names);

internal static class WmiProvider
{
    private const string Scope = @"root\wmi";

    internal static WmiInventory Read(List<string> errors, CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var methods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var displays = new Dictionary<string, WmiDisplay>(StringComparer.OrdinalIgnoreCase);
        Query("WmiMonitorID", item =>
        {
            var name = item["UserFriendlyName"] is ushort[] chars
                ? new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim()
                : "";
            names[Identity.Normalize(item["InstanceName"]?.ToString())] = name;
        }, errors, cancellationToken);
        Query("WmiMonitorBrightnessMethods", item =>
        {
            methods[Identity.Normalize(item["InstanceName"]?.ToString())] = item.Path.Path;
        }, errors, cancellationToken);
        Query("WmiMonitorBrightness", item =>
        {
            var instance = item["InstanceName"]?.ToString();
            if (string.IsNullOrWhiteSpace(instance)) return;
            var key = Identity.Normalize(instance);
            var brightness = Convert.ToByte(item["CurrentBrightness"], CultureInfo.InvariantCulture);
            if (brightness > 100) return;
            var levels = item["Level"] is byte[] supported
                ? supported.Where(level => level <= 100).Distinct().Order().ToArray()
                : [];
            displays[key] = new WmiDisplay(instance,
                names.GetValueOrDefault(key) is { Length: > 0 } name ? name : "Built-in display",
                brightness, levels, methods.GetValueOrDefault(key));
        }, errors, cancellationToken);
        return new WmiInventory(displays, names);
    }

    private static void Query(string className, Action<ManagementObject> read, List<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var searcher = new ManagementObjectSearcher(Scope, $"SELECT * FROM {className} WHERE Active = TRUE");
            searcher.Options.Timeout = TimeSpan.FromSeconds(5);
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    read(item);
                }
            }
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidClass ||
                                              ex.ErrorCode == ManagementStatus.NotSupported)
        {
            // Desktop display drivers commonly do not implement these optional classes.
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            errors.Add($"{className} could not be read: {ex.Message}");
        }
    }

    internal static void SetBrightness(WmiDisplay display, double percentage)
    {
        if (display.MethodPath is null)
            throw new NotSupportedException("The display driver does not expose a writable WMI brightness method.");

        var requested = (byte)Math.Round(percentage, MidpointRounding.AwayFromZero);
        var value = display.Levels.Length == 0 ? requested : display.Levels.MinBy(level => Math.Abs(level - requested));
        try
        {
            using var method = new ManagementObject(display.MethodPath);
            using var parameters = method.GetMethodParameters("WmiSetBrightness");
            parameters["Timeout"] = 0u;
            parameters["Brightness"] = value;
            using var result = method.InvokeMethod("WmiSetBrightness", parameters,
                new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(5) });
            if (result?["ReturnValue"] is not { } status)
                throw new InvalidOperationException($"The display driver did not confirm the brightness change for {display.Name}.");
            var code = Convert.ToUInt32(status, CultureInfo.InvariantCulture);
            if (code != 0)
                throw new InvalidOperationException($"The display driver rejected the brightness change for {display.Name} (WMI status 0x{code:X8}).");
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Brightness could not be changed for {display.Name}. Refresh displays and try again. {ex.Message}", ex);
        }
    }
}

internal static class Identity
{
    // EnumDisplayDevices returns \\?\DISPLAY#vendor#instance#{class-guid} when
    // EDD_GET_DEVICE_INTERFACE_NAME is used; WMI uses DISPLAY\vendor\instance_0.
    internal static string Normalize(string? value)
    {
        var result = (value ?? "").Trim().ToUpperInvariant();
        if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
            result = result[4..].Replace('#', '\\');
        var guid = result.IndexOf(@"\{", StringComparison.Ordinal);
        if (guid >= 0) result = result[..guid];
        var suffix = result.LastIndexOf('_');
        if (suffix > result.LastIndexOf('\\') && suffix >= 0 &&
            uint.TryParse(result.AsSpan(suffix + 1), out _))
            result = result[..suffix];
        return result;
    }

    internal static string Create(string deviceId, int physicalIndex) => $"display:{deviceId}:{physicalIndex}";
}
