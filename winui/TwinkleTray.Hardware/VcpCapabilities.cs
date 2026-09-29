using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TwinkleTray.Hardware;

internal static class VcpCapabilities
{
    internal static readonly IReadOnlyDictionary<byte, string> Names = new Dictionary<byte, string>
    {
        [0x10] = "Brightness", [0x12] = "Contrast", [0x13] = "Backlight",
        [0x14] = "Color preset", [0x16] = "Red gain", [0x18] = "Green gain", [0x1A] = "Blue gain",
        [0x60] = "Input source", [0x62] = "Speaker volume", [0x8D] = "Audio mute",
        [0xD6] = "Power mode", [0x0B] = "Color temperature increment", [0x0C] = "Color temperature"
    };
    internal static bool IsContinuous(byte code) => code is 0x10 or 0x12 or 0x13 or 0x16 or 0x18 or 0x1A or 0x62 or 0x0B or 0x0C;

    internal static Dictionary<byte, IReadOnlyList<uint>>? Read(PhysicalMonitorHandle handle)
    {
        if (!GetCapabilitiesStringLength(handle, out var length) || length is 0 or > 65536) return null;
        var text = new StringBuilder((int)length);
        return CapabilitiesRequestAndCapabilitiesReply(handle, text, length) ? Parse(text.ToString()) : null;
    }

    internal static Dictionary<byte, IReadOnlyList<uint>> Parse(string value)
    {
        var result = new Dictionary<byte, IReadOnlyList<uint>>();
        var match = Regex.Match(value, @"\bvcp\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return result;
        var position = match.Index + match.Length;
        while (position < value.Length)
        {
            SkipWhitespace(value, ref position);
            if (position >= value.Length || value[position] == ')') break;
            var code = Token(value, ref position);
            if (!byte.TryParse(code, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed))
                throw new FormatException("The monitor returned a malformed VCP capabilities list.");
            SkipWhitespace(value, ref position);
            var allowed = new List<uint>();
            if (position < value.Length && value[position] == '(')
            {
                position++;
                while (position < value.Length)
                {
                    SkipWhitespace(value, ref position);
                    if (position >= value.Length || value[position] == ')') break;
                    var item = Token(value, ref position);
                    if (!uint.TryParse(item, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number) || number > ushort.MaxValue)
                        throw new FormatException("The monitor returned a malformed VCP value list.");
                    allowed.Add(number);
                }
                if (position >= value.Length) throw new FormatException("The monitor returned an incomplete VCP value list.");
                position++;
            }
            result[parsed] = allowed.Distinct().ToArray();
        }
        if (position >= value.Length) throw new FormatException("The monitor returned an incomplete VCP capabilities list.");
        return result;
    }

    private static void SkipWhitespace(string text, ref int index) { while (index < text.Length && char.IsWhiteSpace(text[index])) index++; }
    private static string Token(string text, ref int index)
    {
        var start = index;
        while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not '(' and not ')') index++;
        if (start == index) throw new FormatException("Unexpected delimiter in the monitor's VCP capabilities.");
        return text[start..index];
    }
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCapabilitiesStringLength(PhysicalMonitorHandle monitor, out uint length);
    [DllImport("dxva2.dll", CharSet = CharSet.Ansi, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CapabilitiesRequestAndCapabilitiesReply(PhysicalMonitorHandle monitor, StringBuilder reply, uint length);
}
