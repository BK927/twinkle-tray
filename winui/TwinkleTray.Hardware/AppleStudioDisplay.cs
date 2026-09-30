using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TwinkleTray.Hardware;

internal sealed record AppleDisplay(string Path, string Serial, string Name, double Brightness);

// Native HID feature-report transport for the same Apple brightness report used
// by upstream studio-display-control, without a Node/libusb runtime or driver changes.
internal static class AppleStudioDisplay
{
    internal static IReadOnlyList<AppleDisplay> Read(List<string> errors, CancellationToken cancellationToken)
    {
        var displays = new List<AppleDisplay>();
        HidD_GetHidGuid(out var guid);
        var devices = SetupDiGetClassDevs(ref guid, null, 0, 0x12);
        if (devices == -1) { errors.Add("Windows could not enumerate USB HID display controls."); return displays; }
        try
        {
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var device = new DeviceInterface { Size = (uint)Marshal.SizeOf<DeviceInterface>() };
                if (!SetupDiEnumDeviceInterfaces(devices, 0, ref guid, index, ref device)) break;
                SetupDiGetDeviceInterfaceDetail(devices, ref device, 0, 0, out var size, 0);
                if (size is < 8 or > 65536) continue;
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, nint.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(devices, ref device, detail, size, out _, 0)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (!IsBrightnessPath(path)) continue;
                    using var handle = Open(path, false);
                    if (handle.IsInvalid) { errors.Add($"Apple Studio Display brightness HID could not be opened (Windows error {Marshal.GetLastWin32Error()})."); continue; }
                    try
                    {
                        var brightness = ReadBrightness(handle);
                        var serial = ReadString(handle, false);
                        var name = ReadString(handle, true);
                        displays.Add(new AppleDisplay(path, serial, string.IsNullOrWhiteSpace(name) ? "Apple Studio Display" : name, brightness));
                    }
                    catch (InvalidOperationException ex) { errors.Add(ex.Message); }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
        return displays;
    }

    internal static void SetBrightness(AppleDisplay display, double percentage)
    {
        using var handle = Open(display.Path, true);
        if (handle.IsInvalid) throw new InvalidOperationException("Apple Studio Display brightness HID is no longer available.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (display.Serial.Length > 0 && !string.Equals(display.Serial, ReadString(handle, false), StringComparison.Ordinal))
            throw new InvalidOperationException("The Apple display identity changed. Refresh displays before adjusting brightness.");
        _ = ReadBrightness(handle); // Confirm the expected readable report before sending a write.
        var report = new byte[7]; report[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(1, 4), (uint)Math.Round(400 + percentage * 596));
        if (!HidD_SetFeature(handle, report, report.Length))
            throw new InvalidOperationException("Apple Studio Display rejected the brightness report.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private static double ReadBrightness(SafeFileHandle handle)
    {
        var report = new byte[7]; report[0] = 1;
        if (!HidD_GetFeature(handle, report, report.Length))
            throw new InvalidOperationException("Apple Studio Display brightness could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(1, 4));
        if (report[0] != 1 || value is < 400 or > 60000) throw new InvalidOperationException("Apple Studio Display returned an unsupported brightness report.");
        return Math.Round((value - 400) / 596d, 1);
    }

    private static bool IsBrightnessPath(string path) => path.Contains("vid_05ac", StringComparison.OrdinalIgnoreCase) &&
        (path.Contains("pid_1114", StringComparison.OrdinalIgnoreCase) || path.Contains("pid_1116", StringComparison.OrdinalIgnoreCase) || path.Contains("pid_1118", StringComparison.OrdinalIgnoreCase)) &&
        path.Contains("mi_07", StringComparison.OrdinalIgnoreCase);
    private static SafeFileHandle Open(string path, bool write) => CreateFile(path, write ? 0xc0000000u : 0, 3, 0, 3, 0, 0);
    private static string ReadString(SafeFileHandle handle, bool product)
    {
        var bytes = new byte[512];
        var ok = product ? HidD_GetProductString(handle, bytes, bytes.Length) : HidD_GetSerialNumberString(handle, bytes, bytes.Length);
        return ok ? Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim() : "";
    }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInterface { public uint Size; public Guid Class; public uint Flags; public nint Reserved; }
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] buffer, int length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, [Out] byte[] buffer, int length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetProductString(SafeFileHandle handle, [Out] byte[] buffer, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)] private static extern nint SetupDiGetClassDevs(ref Guid guid, string? enumerator, nint hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(nint devices, nint device, ref Guid guid, uint index, ref DeviceInterface data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetail(nint devices, ref DeviceInterface data, nint detail, uint size, out uint required, nint device);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint devices);
}
