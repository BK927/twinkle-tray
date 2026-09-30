using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using TwinkleTray.Core;
using TwinkleTray.Hardware;
using TwinkleTray.WinUI.Services;
using Windows.Storage.Pickers;

namespace TwinkleTray.WinUI;

internal sealed partial class AppController
{
    private readonly UpdateService _updates = new();
    private UdpControlService? _udp;
    private string _udpConfiguration = "", _sensorConfiguration = "";
    private string _hardwareConfiguration = "";

    private void ConfigureIntegrations()
    {
        _tray.Configure(Settings);
        Program.Logging = Settings.Logging;
        if (!IsDemo)
        {
            var options = BrightnessControl.Options(Settings, _monitors.Select(m => m.Id));
            var serialized = JsonSerializer.Serialize(options);
            _hardware.Configure(options);
            if (_hardwareConfiguration.Length > 0 && serialized != _hardwareConfiguration) _ = RefreshAsync();
            _hardwareConfiguration = serialized;
        }
        string sensorConfiguration = JsonSerializer.Serialize(Settings.Sensor);
        if (sensorConfiguration != _sensorConfiguration && Settings.Sensor.Provider != "fake")
        {
            _sensor.Configure(new AmbientLightOptions { Provider = Settings.Sensor.Provider, HubUrl = Settings.Sensor.Endpoint, SensorSerial = Settings.Sensor.HardwareId });
            _sensorConfiguration = sensorConfiguration;
        }
        string configuration = $"{Settings.UdpEnabled}|{Settings.UdpRemote}|{Settings.UdpPort}|{Settings.UdpKey}";
        if (configuration == _udpConfiguration) return;
        _udp?.Dispose(); _udp = null;
        if (Settings.UdpEnabled)
        {
            _udp = new UdpControlService(Settings.UdpPort, Settings.UdpRemote, Settings.UdpKey, request =>
            {
                var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_dispatcher.TryEnqueue(async () =>
                {
                    try { completion.TrySetResult(await HandleUdpAsync(request)); }
                    catch (Exception exception) { completion.TrySetResult(new { error = exception.Message }); }
                })) completion.TrySetCanceled();
                return completion.Task;
            });
        }
        _udpConfiguration = configuration;
    }

    private SettingsActions CreateSettingsActions() => new()
    {
        CheckUpdatesAsync = () => CheckUpdatesAsync(true),
        InstallUpdateAsync = InstallUpdateAsync,
        ImportSettingsAsync = ImportSettingsAsync,
        ReapplyImportAsync = async () => await ReplaceFromUpstreamAsync(Settings.ImportedUpstreamJson),
        ExportSettingsAsync = ExportSettingsAsync,
        ResetSettingsAsync = ResetSettingsAsync,
        OpenLogs = () =>
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwinkleTray.WinUI");
            Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        },
        ApplyProfileAsync = ApplyProfileAsync,
        QueryFeaturesAsync = QueryFeaturesAsync,
        QuerySensorsAsync = () => _sensor.GetDevicesAsync(_lifetime.Token),
        GetCoordinatesAsync = GetCoordinatesAsync,
        GenerateDiagnosticsAsync = GenerateDiagnosticsAsync,
        ImportKnownDisplaysAsync = ImportKnownDisplaysAsync
    };

    private async Task<IReadOnlyList<VcpFeature>> QueryFeaturesAsync(string id)
    {
        var features = IsDemo ? new VcpFeature[]
        {
            new(0x10, "Brightness", (uint)(_monitors.FirstOrDefault(x => x.Id == id)?.Brightness ?? 50), 100, Array.Empty<uint>()),
            new(0x12, "Contrast", 75, 100, Array.Empty<uint>()),
            new(0x62, "Volume", 50, 100, Array.Empty<uint>()),
            new(0x60, "Input", 15, 18, new uint[] { 15, 17, 18 }),
            new(0xD6, "Power", 1, 5, new uint[] { 1, 4, 5 })
        } : await _hardware.GetFeaturesAsync(id, _lifetime.Token);
        _features[id] = features; return features;
    }

    public async Task SetFeatureAsync(string id, byte code, double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        var feature = await QueryFeatureAsync(id, code);
        var monitor = _monitors.Single(m => m.Id == id);
        bool mainChannel = code == monitor.BrightnessVcp && IsMainBrightnessChannel(monitor, "brightness");
        if (mainChannel) CancelManualTransition(id);
        uint raw = (uint)Math.Round(value);
        await _operations.WaitAsync();
        try
        {
            if (!IsDemo) await _hardware.SetVcpAsync(id, code, raw, _lifetime.Token);
            if (_features.TryGetValue(id, out var features)) _features[id] = features.Select(f => f.Code == code ? f with { Current = raw } : f).ToArray();
            if (feature.Maximum > 0)
                _monitors = _monitors.Select(m => m.Id != id ? m : code == m.BrightnessVcp ? m with { Brightness = raw * 100d / feature.Maximum } : code == 0x12 ? m with { Contrast = raw * 100d / feature.Maximum } : m).ToArray();
            if (mainChannel) RememberManualChannelLevel(_monitors.Single(m => m.Id == id));
        }
        finally { _operations.Release(); }
    }

    private async Task<VcpFeature> QueryFeatureAsync(string id, byte code)
    {
        if (!IsDemo) return await _hardware.GetVcpAsync(id, code, _lifetime.Token);
        return Features(id).FirstOrDefault(f => f.Code == code) ?? (await QueryFeaturesAsync(id)).FirstOrDefault(f => f.Code == code) ?? new VcpFeature(code, $"VCP 0x{code:X2}", 0, 100, Array.Empty<uint>());
    }

    private object UdpMonitor(MonitorSnapshot monitor) => new
    {
        id = monitor.Id, key = monitor.Id, name = DisplayName(monitor), brightness = LogicalBrightness(monitor),
        rawBrightness = monitor.Brightness, maxBrightness = 100, type = monitor.Connection, brightnessType = Preferences(monitor.Id).MainControl,
        connector = monitor.DeviceName, hwid = monitor.DeviceInstanceId.Split('\\'), order = Preferences(monitor.Id).Order,
        features = Features(monitor.Id).ToDictionary(f => $"0x{f.Code:X2}", f => new[] { f.Current, f.Maximum }),
        hdr = monitor.HdrActive, sdr = monitor.SdrBrightness, gamma = monitor.GammaBrightness
    };

    private async Task<object?> HandleUdpAsync(JsonElement request)
    {
        if (_quitting) throw new InvalidOperationException("Application is closing.");
        string Text(string name, string fallback = "") => request.TryGetProperty(name, out var value) ? value.ToString() : fallback;
        string type = Text("type").ToLowerInvariant();
        if (type == "list") return _monitors.ToDictionary(m => m.Id, UdpMonitor);
        if (type == "refresh") { await RefreshAsync(); return new { ok = true }; }
        if (type == "checktime") { await ApplyManualLevelsAsync(ScheduleEvaluator.GetCurrentLevels(Settings, DateTime.Now, VisibleMonitors.Select(m => m.Id))); return new { ok = true }; }
        string selector = Text("monitor");
        if (selector.Equals("all", StringComparison.OrdinalIgnoreCase)) selector = "all";
        var selected = selector.Equals("all", StringComparison.OrdinalIgnoreCase) ? VisibleMonitors.ToArray() : _monitors.Where(m => m.Id.Contains(selector, StringComparison.OrdinalIgnoreCase) || m.Name.Equals(selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selector.Length == 0 || selected.Length == 0 || (selector != "all" && selected.Length != 1)) throw new ArgumentException("Use 'all' or one unique monitor ID from list.");
        string property = Text("property", "brightness").ToLowerInvariant();
        if (type == "getvcp") { type = "get"; property = "vcp"; }
        byte Code()
        {
            string raw = Text("vcp", Text("code", property));
            return raw.ToLowerInvariant() switch
            {
                "brightness" => 0x10, "contrast" => 0x12, "volume" => 0x62, "power" or "powerstate" => 0xD6,
                _ => raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? byte.Parse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : byte.Parse(raw, CultureInfo.InvariantCulture)
            };
        }
        if (type == "get")
        {
            if (selected.Length != 1) throw new ArgumentException("get requires one monitor.");
            var monitor = selected[0];
            if (property is "vcp" or "contrast" or "maxcontrast" or "volume" or "maxvolume" or "powerstate" or "maxpowerstate")
            {
                byte code = property.StartsWith("max", StringComparison.Ordinal) ? property switch { "maxcontrast" => (byte)0x12, "maxvolume" => (byte)0x62, _ => (byte)0xD6 } : Code();
                var feature = await QueryFeatureAsync(monitor.Id, code);
                return property.StartsWith("max", StringComparison.Ordinal) ? feature.Maximum : feature.Current;
            }
            return property switch
            {
                "brightness" => LogicalBrightness(monitor), "rawbrightness" => monitor.Brightness, "maxbrightness" => 100,
                "id" or "key" => monitor.Id, "name" => DisplayName(monitor), "type" => monitor.Connection,
                "brightnesstype" => Preferences(monitor.Id).MainControl, "connector" => monitor.DeviceName,
                "hwid" => monitor.DeviceInstanceId.Split('\\'), "order" => Preferences(monitor.Id).Order,
                "sdr" => monitor.SdrBrightness, "gamma" => monitor.GammaBrightness,
                _ => throw new ArgumentException("Unknown monitor property.")
            };
        }
        if (type is not ("set" or "setvcp")) throw new ArgumentException("Unknown UDP command.");
        if (!double.TryParse(Text("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) throw new ArgumentException("value must be finite.");
        bool offset = Text("mode") == "offset";
        foreach (var monitor in selected)
        {
            if (type == "set" && (Text("vcp").Length == 0 || Text("vcp") == "brightness")) await TrySetAsync(monitor.Id, offset ? LogicalBrightness(monitor) + value : value, throwOnError: true);
            else
            {
                byte code = Code(); double target = value;
                if (offset) target += (await QueryFeatureAsync(monitor.Id, code)).Current;
                await SetFeatureAsync(monitor.Id, code, target);
            }
        }
        _window.RenderMonitors();
        if (Text("overlay").Equals("true", StringComparison.OrdinalIgnoreCase)) ShowOverlay();
        if (Text("panel").Equals("true", StringComparison.OrdinalIgnoreCase)) _window.ShowPanel();
        return new { ok = true };
    }

    private async Task ImportSettingsAsync()
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow ?? (Microsoft.UI.Xaml.Window)_window));
        var file = await picker.PickSingleFileAsync(); if (file is null) return;
        string json = await File.ReadAllTextAsync(file.Path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("format", out var format) && format.GetString() == "twinkle-tray-winui")
        {
            var imported = JsonSerializer.Deserialize<AppSettings>(document.RootElement.GetProperty("settings"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty settings export.");
            await ReplaceSettingsAsync(imported, "Settings imported.");
        }
        else await ReplaceFromUpstreamAsync(json);
    }

    private async Task ReplaceFromUpstreamAsync(string json)
    {
        var result = UpstreamSettingsImporter.Import(json, Settings.ImportIdentityMap, string.IsNullOrWhiteSpace(Settings.ImportedKnownDisplaysJson) ? null : Settings.ImportedKnownDisplaysJson);
        string note = "Settings imported.";
        if (result.UnmappedMonitorIds.Count > 0) note += $" Map {result.UnmappedMonitorIds.Count} display IDs in Advanced settings to enable their settings.";
        if (result.Warnings.Count > 0) note += " " + string.Join("\n", result.Warnings);
        await ReplaceSettingsAsync(result.Settings, note);
    }

    private async Task ImportKnownDisplaysAsync()
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow ?? (Microsoft.UI.Xaml.Window)_window));
        var file = await picker.PickSingleFileAsync(); if (file is null) return;
        var result = UpstreamSettingsImporter.ImportKnownDisplays(Settings, await File.ReadAllTextAsync(file.Path), Settings.ImportIdentityMap);
        await ReplaceSettingsAsync(result.Settings, "Remembered display levels imported. " + string.Join("\n", result.Warnings));
    }

    private async Task ReplaceSettingsAsync(AppSettings settings, string message)
    {
        if (!IsDemo && File.Exists(_store.FilePath)) File.Copy(_store.FilePath, _store.FilePath + $".before-import-{DateTime.UtcNow:yyyyMMddTHHmmssfff}.json");
        // Changing an imported app's login preference is an explicit setting, not an import side effect.
        settings.RunAtStartup = Settings.RunAtStartup;
        Settings = SettingsNormalizer.Normalize(settings);
        await SaveSettingsAsync();
        if (_settingsWindow is null) OpenSettings(); else _settingsWindow.ReloadSettings(Settings);
        _settingsWindow?.ShowStatus(message);
    }

    private async Task ExportSettingsAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = "TwinkleTray-WinUI-settings" };
        picker.FileTypeChoices.Add("JSON", new[] { ".json" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow ?? (Microsoft.UI.Xaml.Window)_window));
        var file = await picker.PickSaveFileAsync(); if (file is null) return;
        await File.WriteAllTextAsync(file.Path, JsonSerializer.Serialize(new { format = "twinkle-tray-winui", version = UpdateService.CurrentVersion, settings = Settings }, new JsonSerializerOptions { WriteIndented = true }));
        _settingsWindow?.ShowStatus("Settings exported. The file includes your UDP key.");
    }

    private async Task ResetSettingsAsync()
    {
        if (_settingsWindow?.Content is not Microsoft.UI.Xaml.FrameworkElement root) return;
        var dialog = new ContentDialog { Title = "Reset settings?", Content = "Current settings will be backed up before resetting.", PrimaryButtonText = "Reset", CloseButtonText = "Cancel", XamlRoot = root.XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ReplaceSettingsAsync(new AppSettings(), "Settings reset. A backup was kept.");
    }

    private async Task CheckUpdatesAsync(bool explicitRequest)
    {
        try
        {
            var update = await _updates.CheckAsync(Settings.UpdateChannel != "stable", _lifetime.Token);
            if (!explicitRequest && update is not null) _tray.Notify("Twinkle Tray Native", $"Version {update.Version} is available. Open Settings → Updates to install.");
            if (explicitRequest || update is not null) _settingsWindow?.ShowStatus(update is null ? $"No newer WinUI release is available. Current version: {UpdateService.CurrentVersion}." : $"WinUI {update.Version} is available.\n{update.Notes}");
        }
        catch (Exception exception) { if (explicitRequest) Report(exception); else Program.Log(exception); }
    }

    private async Task InstallUpdateAsync()
    {
        if (StartupService.IsPackaged) throw new InvalidOperationException("Use the signed MSIX package or your deployment channel to update this packaged installation.");
        if (_updates.Available is null) { await CheckUpdatesAsync(true); if (_updates.Available is null) return; }
        _settingsWindow?.ShowStatus("Downloading and verifying update…");
        string staging = await _updates.DownloadAsync(_lifetime.Token);
        UpdateService.LaunchInstaller(staging); Quit();
    }

    private async Task<(double Latitude, double Longitude)> GetCoordinatesAsync()
    {
        var access = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync();
        if (access != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed) throw new InvalidOperationException("Windows location access is unavailable. Enter latitude and longitude manually.");
        var locator = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracy = Windows.Devices.Geolocation.PositionAccuracy.Default };
        var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(15));
        return (position.Coordinate.Point.Position.Latitude, position.Coordinate.Point.Position.Longitude);
    }

    private async Task GenerateDiagnosticsAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = "TwinkleTray-WinUI-diagnostics" };
        picker.FileTypeChoices.Add("JSON", new[] { ".json" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow ?? (Microsoft.UI.Xaml.Window)_window));
        var file = await picker.PickSaveFileAsync(); if (file is null) return;
        var report = new
        {
            version = UpdateService.CurrentVersion, generated = DateTimeOffset.Now,
            windows = Environment.OSVersion.VersionString, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            packaged = StartupService.IsPackaged, demo = IsDemo, displays = _monitors,
            features = _features, detectionErrors = _hardware.LastRefreshErrors,
            hardwareOptions = BrightnessControl.Options(Settings, _monitors.Select(m => m.Id)),
            sensor = new { Settings.Sensor.Enabled, Settings.Sensor.Provider, error = _sensor.LastError },
            udp = new { Settings.UdpEnabled, Settings.UdpRemote, requestedPort = Settings.UdpPort, actualPort = _udp?.Port },
            automation = new { schedules = Settings.Schedule.Count, profiles = Settings.Profiles.Count, hotkeys = Settings.Hotkeys.Count, paused = _automationPaused }
        };
        await File.WriteAllTextAsync(file.Path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        _settingsWindow?.ShowStatus("Diagnostics saved. UDP keys, imported settings, and location coordinates are excluded.");
    }
}
