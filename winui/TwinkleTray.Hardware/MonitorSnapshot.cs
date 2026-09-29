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
    double? Contrast);
