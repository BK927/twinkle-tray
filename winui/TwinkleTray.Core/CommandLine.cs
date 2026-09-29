using System.Globalization;

namespace TwinkleTray.Core;

public sealed record VcpCommand(byte Code, ushort Value);

public sealed record CommandLineOptions
{
    public bool List { get; init; }
    public bool All { get; init; }
    public int? MonitorNum { get; init; }
    public string? MonitorId { get; init; }
    public double? Set { get; init; }
    public double? Offset { get; init; }
    public VcpCommand? Vcp { get; init; }
    public bool Panel { get; init; }
    public bool Overlay { get; init; }
    public bool Settings { get; init; }
    public bool UseTime { get; init; }
    public bool Udp { get; init; }
    public bool Demo { get; init; }
    public bool Background { get; init; }
    public bool SmokeTest { get; init; }
    public bool Help { get; init; }
    public bool HasMonitorCommand => Set.HasValue || Offset.HasValue || Vcp is not null;
    public bool HasCommand => HasMonitorCommand || UseTime || List || Panel || Overlay || Settings || Help;
}

public static class CommandLine
{
    public const string HelpText = """
        Twinkle Tray for WinUI 3

        --List                           List connected monitors
        --All                            Select all monitors
        --MonitorNum=1                   Select a monitor by its 1-based number
        --MonitorID=UID2353               Select by full or partial monitor ID
        --Set=95                         Set brightness (0–100)
        --Offset=-20                     Change brightness (-100–100)
        --UseTime                        Apply the current time-adjustment schedule
        --VCP=0xD6:5                     Send a DDC/CI code (0–255) and value (0–65535)
        --UDP                            Send the command over local UDP using saved port/key
        --Panel                          Show the brightness panel
        --settings                       Open the settings window
        --Overlay                        Show the dedicated brightness OSD
        --background                     Start in the notification area
        --demo                           Use simulated monitors
        --smoke-test                     Verify application startup without hardware changes
        --help                           Show this help

        Brightness and VCP commands require exactly one monitor selector and one action.
        Example: TwinkleTray.WinUI.exe --MonitorNum=1 --Offset=-30 --Overlay
        """;

    public static CommandLineOptions Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new CommandLineOptions();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in args)
        {
            if (string.IsNullOrWhiteSpace(argument))
                throw new ArgumentException("An empty command-line argument is not valid.", nameof(args));

            var split = argument.IndexOf('=');
            var name = split < 0 ? argument : argument[..split];
            var value = split < 0 ? null : Unquote(argument[(split + 1)..]);
            var key = name.ToLowerInvariant();
            if (key is "-h" or "/?")
                key = "--help";
            if (!seen.Add(key))
                throw new ArgumentException($"'{name}' was supplied more than once.", nameof(args));

            switch (key)
            {
                case "--list": RequireFlag(name, value); options = options with { List = true }; break;
                case "--all": RequireFlag(name, value); options = options with { All = true }; break;
                case "--panel": RequireFlag(name, value); options = options with { Panel = true }; break;
                case "--overlay": RequireFlag(name, value); options = options with { Overlay = true }; break;
                case "--settings": RequireFlag(name, value); options = options with { Settings = true }; break;
                case "--usetime": RequireFlag(name, value); options = options with { UseTime = true }; break;
                case "--udp": RequireFlag(name, value); options = options with { Udp = true }; break;
                case "--demo": RequireFlag(name, value); options = options with { Demo = true }; break;
                case "--background": RequireFlag(name, value); options = options with { Background = true }; break;
                case "--smoke-test": RequireFlag(name, value); options = options with { SmokeTest = true }; break;
                case "--help": RequireFlag(name, value); options = options with { Help = true }; break;
                case "--monitornum":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
                        throw new ArgumentException("--MonitorNum requires a positive, 1-based monitor number.", nameof(args));
                    options = options with { MonitorNum = number };
                    break;
                case "--monitorid":
                    if (string.IsNullOrWhiteSpace(value))
                        throw new ArgumentException("--MonitorID requires a nonempty monitor ID.", nameof(args));
                    options = options with { MonitorId = value };
                    break;
                case "--set": options = options with { Set = ParsePercent(name, value, 0, 100) }; break;
                case "--offset": options = options with { Offset = ParsePercent(name, value, -100, 100) }; break;
                case "--vcp": options = options with { Vcp = ParseVcp(value) }; break;
                default: throw new ArgumentException($"Unknown argument '{name}'. Use --help for supported arguments.", nameof(args));
            }
        }

        var selectors = (options.All ? 1 : 0) + (options.MonitorNum.HasValue ? 1 : 0) + (options.MonitorId is not null ? 1 : 0);
        var actions = (options.Set.HasValue ? 1 : 0) + (options.Offset.HasValue ? 1 : 0) + (options.Vcp is not null ? 1 : 0);
        if (selectors > 1)
            throw new ArgumentException("Use only one of --All, --MonitorNum, or --MonitorID.", nameof(args));
        if (actions > 1)
            throw new ArgumentException("Use only one of --Set, --Offset, or --VCP.", nameof(args));
        if (options.UseTime && (selectors > 0 || actions > 0 || options.List))
            throw new ArgumentException("--UseTime cannot be combined with monitor selectors, brightness/VCP commands, or --List.", nameof(args));
        if (actions == 1 && selectors != 1)
            throw new ArgumentException("Brightness and VCP commands require --All, --MonitorNum, or --MonitorID.", nameof(args));
        if (selectors == 1 && actions != 1)
            throw new ArgumentException("A monitor selector requires --Set, --Offset, or --VCP.", nameof(args));
        if (options.List && (actions > 0 || options.Panel || options.Overlay))
            throw new ArgumentException("--List cannot be combined with a monitor command, --Panel, or --Overlay.", nameof(args));
        if (options.Panel && options.Overlay)
            throw new ArgumentException("Use only one of --Panel or --Overlay.", nameof(args));
        if (options.Settings && (options.Panel || options.Overlay || options.List || options.HasMonitorCommand || options.UseTime || options.Udp || options.Background))
            throw new ArgumentException("--settings cannot be combined with commands, other window modes, or --background.", nameof(args));
        if (options.Background && (options.Panel || options.Overlay))
            throw new ArgumentException("--background cannot be combined with --Panel or --Overlay.", nameof(args));
        if (options.SmokeTest && (actions > 0 || options.UseTime || options.Udp))
            throw new ArgumentException("--smoke-test cannot be combined with a monitor command, --UseTime, or --UDP.", nameof(args));
        if (options.Udp && !(options.List || options.HasMonitorCommand || options.UseTime))
            throw new ArgumentException("--UDP requires --List, --UseTime, or a monitor command.", nameof(args));
        if (options.Udp && options.Demo)
            throw new ArgumentException("--demo cannot use --UDP, which connects to the real application's saved server settings.", nameof(args));
        return options;
    }

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private static void RequireFlag(string name, string? value)
    {
        if (value is not null)
            throw new ArgumentException($"{name} is a flag and does not accept a value.");
    }

    private static double ParsePercent(string name, string? value, double min, double max)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            !double.IsFinite(number) || number < min || number > max)
            throw new ArgumentException($"{name} requires a number between {min} and {max}.");
        return number;
    }

    private static VcpCommand ParseVcp(string? value)
    {
        var parts = value?.Split(':');
        if (parts is not { Length: 2 } || !TryUnsigned(parts[0], out var code) || code > byte.MaxValue ||
            !TryUnsigned(parts[1], out var vcpValue) || vcpValue > ushort.MaxValue)
            throw new ArgumentException("--VCP requires code:value, with code from 0 to 255 and value from 0 to 65535 (decimal or 0x hexadecimal).");
        return new VcpCommand((byte)code, (ushort)vcpValue);
    }

    private static bool TryUnsigned(string value, out uint result) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result)
        : uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
}
