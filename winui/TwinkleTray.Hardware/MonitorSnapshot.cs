namespace TwinkleTray.Hardware;

/// <summary>A read-only observation of a connected display. Levels are percentages.</summary>
/// <remarks>Brightness is meaningful only when SupportsBrightness is true.</remarks>
public sealed record MonitorSnapshot(
    string Id,
    string Name,
    string Connection,
    double Brightness,
    bool SupportsBrightness,
    bool SupportsContrast,
    double? Contrast)
{
    public string DeviceName { get; init; } = "";
    public string DeviceInstanceId { get; init; } = "";
    public byte BrightnessVcp { get; init; } = 0x10;
    public bool HdrSupported { get; init; }
    public bool HdrActive { get; init; }
    public double? SdrBrightness { get; init; }
    public double? GammaBrightness { get; init; }
    public bool SupportsGammaBrightness => GammaBrightness.HasValue && !HdrActive;
}

public sealed record VcpFeature(byte Code, string Name, uint Current, uint Maximum, IReadOnlyList<uint> AllowedValues);

public sealed class HardwareOptions
{
    public bool DisableWmi { get; set; }
    public bool DisableDdc { get; set; }
    public bool DisableAppleStudio { get; set; }
    public bool SoftwareFallback { get; set; }
    public bool EnableGamma { get; set; }
    public int VcpReadDelayMilliseconds { get; set; }
    public Dictionary<string, byte> BrightnessCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<byte>> CustomVcpCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> GammaDisplays { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ForceHdrDisplays { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal HardwareOptions Copy() => new()
    {
        DisableWmi = DisableWmi, DisableDdc = DisableDdc, DisableAppleStudio = DisableAppleStudio,
        SoftwareFallback = SoftwareFallback, EnableGamma = EnableGamma,
        VcpReadDelayMilliseconds = Math.Clamp(VcpReadDelayMilliseconds, 0, 2000),
        BrightnessCodes = new(BrightnessCodes ?? [], StringComparer.OrdinalIgnoreCase),
        CustomVcpCodes = (CustomVcpCodes ?? []).ToDictionary(pair => pair.Key, pair => new List<byte>(pair.Value), StringComparer.OrdinalIgnoreCase),
        GammaDisplays = new(GammaDisplays ?? [], StringComparer.OrdinalIgnoreCase),
        ForceHdrDisplays = new(ForceHdrDisplays ?? [], StringComparer.OrdinalIgnoreCase)
    };
}
