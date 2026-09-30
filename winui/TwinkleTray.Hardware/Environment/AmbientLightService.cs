using System.Net.Http;
using System.Text.Json;
using Windows.Devices.Sensors;
using Windows.Devices.Enumeration;

namespace TwinkleTray.Hardware;

public sealed class AmbientLightOptions
{
    public string Provider { get; set; } = "windows";
    public string HubUrl { get; set; } = "http://127.0.0.1:4444";
    public string? SensorSerial { get; set; }
    public string? WindowsDeviceId { get; set; }
}

public sealed record AmbientLightDevice(string Id, string Name, string Provider);

/// <summary>Reads ambient light without changing brightness or sensor calibration.</summary>
public sealed class AmbientLightService : IDisposable
{
    private readonly object _gate = new();
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(4),
        MaxResponseContentBufferSize = 1024 * 1024
    };
    private readonly CancellationTokenSource _lifetime = new();
    private Configuration _configuration = new("windows", new Uri("http://127.0.0.1:4444/"), null);
    private LightSensor? _windowsSensor;
    private string? _lastError;
    private bool _disposed;

    public AmbientLightService(AmbientLightOptions? options = null)
    {
        try { Configure(options ?? new AmbientLightOptions()); }
        catch { _http.Dispose(); _lifetime.Dispose(); throw; }
    }

    /// <summary>Reason for the latest unavailable reading; null after a successful reading.</summary>
    public string? LastError { get { lock (_gate) return _lastError; } }

    public void Configure(AmbientLightOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var provider = options.Provider?.Trim().ToLowerInvariant();
        if (provider is not ("windows" or "yocto" or "yoctopuce"))
            throw new ArgumentException("Ambient light provider must be windows or yocto.", nameof(options));
        if (!Uri.TryCreate(options.HubUrl, UriKind.Absolute, out var hub)
            || (hub.Scheme != Uri.UriSchemeHttp && hub.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(hub.Host) || hub.UserInfo.Length != 0
            || hub.Query.Length != 0 || hub.Fragment.Length != 0)
            throw new ArgumentException("The hub URL must be an HTTP(S) base URL without credentials, a query, or a fragment.", nameof(options));
        var selection = provider == "windows" ? options.WindowsDeviceId ?? options.SensorSerial : options.SensorSerial;
        var serial = string.IsNullOrWhiteSpace(selection) ? null : selection.Trim();
        if (serial is not null && (provider == "windows" ? serial.Length > 2048 || serial.Any(char.IsControl) :
            serial.Split('.').Length > 2 || !serial.Split('.').All(IsIdentifier)))
            throw new ArgumentException("Use a Windows sensor device ID, or a Yoctopuce serial/function ID containing ASCII letters, digits, hyphens, and underscores.", nameof(options));
        var configuration = new Configuration(provider, new Uri(hub.AbsoluteUri.TrimEnd('/') + "/"), serial);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReleaseWindowsSensor();
            _configuration = configuration;
            _lastError = null;
        }
    }

    /// <summary>Returns null when a sensor or a valid reading is unavailable. Cancellation propagates.</summary>
    public async Task<double?> ReadLuxAsync(CancellationToken cancellationToken = default)
    {
        Configuration configuration;
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            configuration = _configuration;
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        }
        using (linked)
        {
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                double? lux;
                if (configuration.Provider == "windows")
                {
                    LightSensor? sensor;
                    lock (_gate) sensor = _windowsSensor;
                    sensor ??= configuration.Serial is null ? LightSensor.GetDefault() :
                        await LightSensor.FromIdAsync(configuration.Serial).AsTask(linked.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        if (!ReferenceEquals(configuration, _configuration)) return null;
                        _windowsSensor ??= sensor;
                        if (_windowsSensor is null)
                            throw new InvalidOperationException("No Windows ambient light sensor was found.");
                        // Polling requires a report interval, including for the first reading.
                        _windowsSensor.ReportInterval = Math.Max(1000u, _windowsSensor.MinimumReportInterval);
                        lux = _windowsSensor.GetCurrentReading()?.IlluminanceInLux;
                    }
                }
                else
                {
                    lux = await ReadYoctoAsync(configuration, linked.Token).ConfigureAwait(false);
                }
                linked.Token.ThrowIfCancellationRequested();
                if (lux is null || !double.IsFinite(lux.Value) || lux < 0)
                    throw new InvalidOperationException("The ambient light sensor has no valid lux reading.");
                lock (_gate)
                {
                    if (!ReferenceEquals(configuration, _configuration)) return null;
                    _lastError = null;
                }
                return lux;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or OperationCanceledException)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(configuration, _configuration))
                    {
                        _lastError = ex is OperationCanceledException ? "The ambient light hub did not respond in time." : ex.Message;
                        if (configuration.Provider == "windows") ReleaseWindowsSensor();
                    }
                }
                return null;
            }
        }
    }

    private async Task<double?> ReadYoctoAsync(Configuration configuration, CancellationToken cancellationToken)
    {
        var sensors = await GetYoctoDevicesAsync(configuration, cancellationToken).ConfigureAwait(false);
        var selected = sensors.FirstOrDefault(sensor => configuration.Serial is null ||
            string.Equals(sensor.Id, configuration.Serial, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sensor.Id.Split('.')[0], configuration.Serial, StringComparison.OrdinalIgnoreCase));
        if (selected is null) throw new InvalidOperationException("The selected Yoctopuce light sensor is not reported by the hub.");
        var parts = selected.Id.Split('.');
        var serial = parts[0]; var function = parts[1];

        // Only fixed read endpoints are requested. Hub-returned URLs are never followed.
        var uri = new Uri(configuration.Hub, $"bySerial/{serial}/api/{function}.json");
        using var reading = await ReadJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        var root = reading.RootElement;
        if (root.TryGetProperty("sensorState", out var state) && state.TryGetInt32(out var status) && status != 0)
            throw new InvalidOperationException($"The Yoctopuce light sensor is not ready (state {status}).");
        if (root.TryGetProperty("unit", out var unit) && unit.GetString() is string unitName
            && !unitName.Equals("lx", StringComparison.OrdinalIgnoreCase) && !unitName.Equals("lux", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Yoctopuce light sensor is not reporting lux.");
        // Like upstream get_currentRawValue(), use the uncalibrated reading. Yoctopuce JSON uses 16.16 fixed point.
        if (!root.TryGetProperty("currentRawValue", out var value) || !value.TryGetDouble(out var raw)) return null;
        return raw / 65536.0;
    }

    public async Task<IReadOnlyList<AmbientLightDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        Configuration configuration;
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            configuration = _configuration;
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        }
        using (linked)
        {
            if (configuration.Provider != "windows") return await GetYoctoDevicesAsync(configuration, linked.Token).ConfigureAwait(false);
            var devices = await DeviceInformation.FindAllAsync(LightSensor.GetDeviceSelector()).AsTask(linked.Token).ConfigureAwait(false);
            return devices.Select(device => new AmbientLightDevice(device.Id, device.Name, "windows")).ToArray();
        }
    }

    private async Task<IReadOnlyList<AmbientLightDevice>> GetYoctoDevicesAsync(Configuration configuration, CancellationToken cancellationToken)
    {
        var result = new List<AmbientLightDevice>();
        using var discovery = await ReadJsonAsync(new Uri(configuration.Hub, "api.json"), cancellationToken).ConfigureAwait(false);
        if (discovery.RootElement.TryGetProperty("services", out var services) && services.TryGetProperty("yellowPages", out var pages) &&
            pages.TryGetProperty("LightSensor", out var sensors) && sensors.ValueKind == JsonValueKind.Array)
        {
            foreach (var sensor in sensors.EnumerateArray())
            {
                if (!sensor.TryGetProperty("hardwareId", out var id) || id.ValueKind != JsonValueKind.String) continue;
                var hardwareId = id.GetString()!;
                var parts = hardwareId.Split('.');
                if (parts.Length != 2 || !parts.All(IsIdentifier)) continue;
                var name = sensor.TryGetProperty("logicalName", out var logical) && logical.ValueKind == JsonValueKind.String ? logical.GetString() : null;
                result.Add(new AmbientLightDevice(hardwareId, string.IsNullOrWhiteSpace(name) ? hardwareId : name, "yocto"));
            }
        }
        return result.AsReadOnly();
    }

    private async Task<JsonDocument> ReadJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static bool IsIdentifier(string value) => value.Length is > 0 and <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private void ReleaseWindowsSensor()
    {
        if (_windowsSensor is null) return;
        try { _windowsSensor.ReportInterval = 0; }
        catch (System.Runtime.InteropServices.COMException) { }
        _windowsSensor = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            ReleaseWindowsSensor();
        }
        _http.Dispose();
        _lifetime.Dispose();
    }

    private sealed record Configuration(string Provider, Uri Hub, string? Serial);
}
