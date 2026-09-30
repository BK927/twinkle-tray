using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TwinkleTray.Hardware;

internal sealed record ColorDisplay(string DeviceId, string DeviceName, string Name,
    bool HdrSupported, bool HdrActive, double? SdrBrightness, DisplayColor.Luid Adapter, uint Target)
{
    internal bool HdrModeKnown { get; init; }
}

internal static class DisplayColor
{
    internal static Dictionary<string, ColorDisplay> Read(List<string> errors, IReadOnlySet<string>? forceHdr = null)
    {
        var result = new Dictionary<string, ColorDisplay>(StringComparer.OrdinalIgnoreCase);
        var legacyHdr = DxgiColor.Read();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var status = GetDisplayConfigBufferSizes(2, out var pathCount, out var modeCount);
            if (status != 0) { errors.Add($"Display color configuration is unavailable (Windows error {status})."); return result; }
            if (pathCount > 256 || modeCount > 2048) throw new InvalidOperationException("Windows returned an invalid display configuration size.");
            var paths = new PathInfo[pathCount];
            var modes = new byte[modeCount * 64];
            status = QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, 0);
            if (status == 122) continue;
            if (status != 0) { errors.Add($"Display paths could not be read (Windows error {status})."); return result; }
            foreach (var path in paths.Take((int)pathCount))
            {
                var target = new TargetName { Header = Header.For<TargetName>(2, path.Target.Adapter, path.Target.Id) };
                if (GetTargetName(ref target) != 0) continue;
                var source = new SourceName { Header = Header.For<SourceName>(1, path.Source.Adapter, path.Source.Id) };
                if (GetSourceName(ref source) != 0) continue;
                var modern = new AdvancedColor2 { Header = Header.For<AdvancedColor2>(15, path.Target.Adapter, path.Target.Id) };
                bool supported = false, active = false, modeKnown = false;
                if (GetAdvancedColor2(ref modern) == 0)
                {
                    supported = (modern.Value & 0x10) != 0;
                    active = modern.ActiveColorMode == 2;
                    modeKnown = true;
                }
                else
                {
                    var legacy = new AdvancedColor { Header = Header.For<AdvancedColor>(9, path.Target.Adapter, path.Target.Id) };
                    if (GetAdvancedColor(ref legacy) == 0)
                    {
                        supported = (legacy.Value & 1) != 0;
                        // Legacy advancedColorEnabled also covers ACM. DXGI distinguishes HDR
                        // without changing SDR white level just to test the current mode.
                        modeKnown = legacyHdr.TryGetValue(source.Name, out active);
                    }
                }
                double? brightness = null;
                var id = Identity.Normalize(target.Path);
                var white = new WhiteLevel { Header = Header.For<WhiteLevel>(11, path.Target.Adapter, path.Target.Id) };
                if ((active || forceHdr?.Contains(id) == true) && GetWhiteLevel(ref white) == 0)
                    brightness = Math.Clamp((white.Level * 80d / 1000d - 80) / 4d, 0, 100);
                result[id] = new ColorDisplay(id, source.Name, target.Name, supported, active, brightness, path.Target.Adapter, path.Target.Id)
                    { HdrModeKnown = modeKnown };
            }
            return result;
        }
        errors.Add("The display configuration kept changing while color information was being read.");
        return result;
    }

    internal static void SetSdrBrightness(ColorDisplay display, double percentage, bool force = false)
    {
        if (!display.HdrActive && !force) throw new NotSupportedException("SDR content brightness is available only while HDR is active on this display.");
        // Windows' private SDR-white-level setter is the same one used upstream. The
        // range and units are kept identical: 0–100 -> 80–480 nits, rounded up to 4 nits.
        var nits = Math.Ceiling((80 + percentage * 4) / 4) * 4;
        var request = new SetWhiteLevel
        {
            Header = Header.For<SetWhiteLevel>(0xffffffee, display.Adapter, display.Target),
            Level = (uint)Math.Round(nits * 1000 / 80), FinalValue = 1
        };
        var status = SetWhiteLevelNative(ref request);
        if (status != 0) throw new InvalidOperationException($"Windows rejected the SDR content brightness change (error {status}).", new Win32Exception(status));
    }

    [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] internal struct Header
    {
        public uint Type, Size; public Luid Adapter; public uint Id;
        internal static Header For<T>(uint type, Luid adapter, uint id) => new() { Type = type, Size = (uint)Marshal.SizeOf<T>(), Adapter = adapter, Id = id };
    }
    [StructLayout(LayoutKind.Sequential)] private struct Source { public Luid Adapter; public uint Id, ModeIndex, Status; }
    [StructLayout(LayoutKind.Sequential)] private struct Rational { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)] private struct Target
    {
        public Luid Adapter; public uint Id, ModeIndex, Technology, Rotation, Scaling; public Rational Refresh;
        public uint ScanLine; public int Available; public uint Status;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public Source Source; public Target Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SourceName
    {
        public Header Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TargetName
    {
        public Header Header; public uint Flags, Technology; public ushort Manufacturer, Product; public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Path;
    }
    [StructLayout(LayoutKind.Sequential)] private struct AdvancedColor { public Header Header; public uint Value, Encoding, Bits; }
    [StructLayout(LayoutKind.Sequential)] private struct AdvancedColor2 { public Header Header; public uint Value, Encoding, Bits, ActiveColorMode; }
    [StructLayout(LayoutKind.Sequential)] private struct WhiteLevel { public Header Header; public uint Level; }
    [StructLayout(LayoutKind.Sequential)] private struct SetWhiteLevel { public Header Header; public uint Level; public byte FinalValue; }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] pathInfo, ref uint modes, [Out] byte[] modeInfo, nint topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetSourceName(ref SourceName info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetTargetName(ref TargetName info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetAdvancedColor(ref AdvancedColor info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetAdvancedColor2(ref AdvancedColor2 info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetWhiteLevel(ref WhiteLevel info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] private static extern int SetWhiteLevelNative(ref SetWhiteLevel info);
}

internal static class DxgiColor
{
    internal static Dictionary<string, bool> Read()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var iid = new Guid("770AAE78-F26F-4DBA-A829-253C83D1B387"); // IDXGIFactory1
        if (CreateDXGIFactory1(ref iid, out var factory) < 0) return result;
        try
        {
            var enumAdapter = Method<EnumChild>(factory, 12);
            for (uint i = 0; enumAdapter(factory, i, out var adapter) >= 0; i++)
            {
                try
                {
                    var enumOutput = Method<EnumChild>(adapter, 7);
                    for (uint j = 0; enumOutput(adapter, j, out var output) >= 0; j++)
                    {
                        try
                        {
                            var output6Iid = new Guid("068346E8-AAEC-4B84-ADD7-137F513F77A1");
                            if (Marshal.QueryInterface(output, in output6Iid, out var output6) < 0) continue;
                            try
                            {
                                if (Method<GetDesc>(output6, 27)(output6, out var desc) >= 0 && desc.Attached != 0)
                                    result[desc.DeviceName] = desc.ColorSpace == 12; // RGB_FULL_G2084_NONE_P2020
                            }
                            finally { Marshal.Release(output6); }
                        }
                        finally { Marshal.Release(output); }
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }
    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumChild(nint self, uint index, out nint child);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDesc(nint self, out OutputDesc desc);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public NativeMethods.Rect Desktop; public int Attached; public uint Rotation; public nint Monitor;
        public uint Bits, ColorSpace;
        public float RedX, RedY, GreenX, GreenY, BlueX, BlueY, WhiteX, WhiteY, MinimumLuminance, MaximumLuminance, FullFrameLuminance;
    }
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out nint factory);
}
