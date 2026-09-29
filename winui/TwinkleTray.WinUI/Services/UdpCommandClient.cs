using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TwinkleTray.Core;

namespace TwinkleTray.WinUI.Services;

internal static class UdpCommandClient
{
    public static async Task<string> RunAsync(CommandLineOptions options)
    {
        var settings = new SettingsStore().Load();
        if (!settings.UdpEnabled) throw new InvalidOperationException("Enable UDP in Advanced settings and start Twinkle Tray before using --UDP.");
        foreach (int port in new[] { settings.UdpPort, settings.UdpPort + 13137, settings.UdpPort + 1603 }.Where(p => p <= 65535))
        {
            using var socket = new UdpClient(); socket.Connect(IPAddress.Loopback, port);
            async Task<JsonElement> SendAsync(object command)
            {
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(command));
                var response = await socket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
                using var document = JsonDocument.Parse(response.Buffer);
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.ToString());
                return document.RootElement.Clone();
            }
            try
            {
                var listing = await SendAsync(new { type = "list", key = settings.UdpKey });
                if (options.List) return JsonSerializer.Serialize(listing, new JsonSerializerOptions { WriteIndented = true });
                if (options.UseTime) return (await SendAsync(new { type = "checktime", key = settings.UdpKey })).ToString();
                string monitor = options.All ? "all" : options.MonitorId ?? "";
                if (options.MonitorNum is { } index)
                {
                    var monitors = listing.EnumerateObject().ToArray();
                    if (index > monitors.Length) throw new ArgumentException($"Monitor {index} was not found.");
                    monitor = monitors[index - 1].Name;
                }
                await SendAsync(new { type = options.Vcp is null ? "set" : "setvcp", key = settings.UdpKey, monitor,
                    value = options.Vcp is { } vcp ? vcp.Value : options.Set ?? options.Offset ?? 0,
                    vcp = options.Vcp is { } code ? $"0x{code.Code:X2}" : "brightness", mode = options.Offset.HasValue ? "offset" : "set", overlay = options.Overlay, panel = options.Panel });
                return "OK";
            }
            catch (TimeoutException) { }
            catch (SocketException) { }
        }
        throw new TimeoutException("No authenticated Twinkle Tray UDP server answered on the configured ports.");
    }
}
