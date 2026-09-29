using System.Net;
using System.Net.Sockets;
using System.Text;
using TwinkleTray.Hardware;

internal static class EnvironmentProbe
{
    internal static async Task RunAsync()
    {
        // A local fixture verifies hub discovery, explicit hardware selection, endpoint
        // construction, and Yoctopuce's 16.16 raw lux decoding without contacting a sensor.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = ServeAsync(listener, timeout.Token);
            using var yocto = new AmbientLightService(new AmbientLightOptions
            {
                Provider = "yocto", HubUrl = $"http://127.0.0.1:{port}", SensorSerial = "LIGHTMK5-123456.lightSensor1"
            });
            var reading = await yocto.ReadLuxAsync(timeout.Token);
            if (reading != 50) throw new InvalidOperationException("Yoctopuce fixture read failed: " + yocto.LastError);
            await server;
        }
        finally { listener.Stop(); }

        var locked = DesktopEnvironment.IsSessionLocked();
        var idle = DesktopEnvironment.IdleTime;
        var media = await DesktopEnvironment.IsMediaPlayingAsync();
        _ = DesktopEnvironment.GetForegroundProcessPath(); // Do not print the user's foreground application path.
        _ = DesktopEnvironment.IsFullscreen();
        if (idle < TimeSpan.Zero) throw new InvalidOperationException("Idle duration must not be negative.");
        using var windows = new AmbientLightService();
        var devices = await windows.GetDevicesAsync();
        Console.WriteLine($"Environment reads: lock state known={locked.HasValue}; media query succeeded; Windows light sensors={devices.Count}. Yoctopuce fixture=50 lux.");
    }

    private static async Task ServeAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 2; i++)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            var request = await reader.ReadLineAsync(cancellationToken);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken))) { }
            var expected = i == 0 ? "GET /api.json HTTP/1.1" : "GET /bySerial/LIGHTMK5-123456/api/lightSensor1.json HTTP/1.1";
            if (request != expected) throw new InvalidOperationException("Unexpected Yoctopuce read endpoint: " + request);
            var json = i == 0
                ? "{\"services\":{\"yellowPages\":{\"LightSensor\":[{\"hardwareId\":\"LIGHTMK5-123456.lightSensor1\",\"logicalName\":\"Desk\"}]}}}"
                : "{\"unit\":\"lx\",\"sensorState\":0,\"currentRawValue\":3276800}";
            var payload = Encoding.UTF8.GetBytes(json);
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
        }
    }
}
