using System.IO.Pipes;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TwinkleTray.Core;
using TwinkleTray.Hardware;
using TwinkleTray.WinUI.Services;

namespace TwinkleTray.WinUI;

internal sealed partial class AppController
{
    private readonly MonitorService _hardware = new();
    private readonly SettingsStore _store = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly SemaphoreSlim _hotkeyOperations = new(1, 1);
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _hotplug = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, double> _beforeIdle = new();
    private IReadOnlyList<MonitorSnapshot> _monitors = Array.Empty<MonitorSnapshot>();
    private MainWindow _window = null!;
    private SettingsWindow? _settingsWindow;
    private TrayService _tray = null!;
    private DateTime _lastSchedule = DateTime.Now;
    private bool _tickBusy, _dimmed, _quitting, _savedStartup;
    private string? _loadError;
    private bool _settingsSaveBlocked;
    private readonly Dictionary<string, IReadOnlyList<VcpFeature>> _features = new();
    private readonly Dictionary<string, CancellationTokenSource> _transitions = new();
    private OverlayWindow? _overlay;
    private bool _lidClosed;
    public AppSettings Settings { get; private set; }
    public bool IsDemo => Program.Options.Demo || Program.Options.SmokeTest;
    public bool IsSmokeTest => Program.Options.SmokeTest;
    public IEnumerable<MonitorSnapshot> VisibleMonitors => _monitors.Where(m => !Preferences(m.Id).Hidden && !(Settings.HideClosedLid && _lidClosed && m.Connection.Contains("WMI", StringComparison.OrdinalIgnoreCase))).OrderBy(m => Preferences(m.Id).Order);

    internal AppController()
    {
        try { Settings = IsDemo ? new AppSettings() : _store.Load(); }
        catch (SettingsLoadException exception) { Settings = new(); _loadError = exception.Message; _settingsSaveBlocked = exception.BackupPath is null; Program.Log(exception); }
        _savedStartup = Settings.RunAtStartup;
        LocalizationService.Configure(Settings.Language);
    }

    public async Task StartAsync()
    {
        _window = new MainWindow(this);
        _tray = new TrayService();
        _tray.Activated += activation => { _window.TogglePanel(activation); if (_window.IsShown) _ = RefreshAsync(); };
        _tray.PanelRequested += activation => { _window.ShowPanel(activation); _ = RefreshAsync(); };
        _tray.SettingsRequested += OpenSettings;
        _tray.RefreshRequested += () => _ = RefreshAsync();
        _tray.ExitRequested += Quit;
        _tray.HotkeyPressed += binding => _ = HandleHotkeyAsync(binding);
        _tray.Scrolled += steps => _ = OffsetAllAsync(steps * Settings.TrayScrollStep * (Settings.InvertScroll ? -1 : 1));
        _tray.ProfileRequested += profile => _ = ApplyProfileAsync(profile);
        _tray.PauseRequested += () => { _automationPaused = !_automationPaused; _tray.AutomationPaused = _automationPaused; if (_automationPaused) SuspendAutomationTransitions(); };
        _tray.PowerRequested += () => _ = PowerOffAsync("all");
        _tray.LidChanged += closed => { _lidClosed = closed; _window.RenderMonitors(); };
        _tray.DisplaysChanged += () => { if (!Settings.DisableAutoRefresh) { _restoreAfterRefresh = !Settings.DisableAutoApply; _hotplug.Interval = TimeSpan.FromSeconds(Math.Max(1, Settings.HardwareRestoreSeconds)); _hotplug.Stop(); _hotplug.Start(); } };
        _tray.Resumed += () => { if (!Settings.DisableAutoRefresh) { _restoreAfterRefresh = !Settings.DisableAutoApply; _hotplug.Interval = TimeSpan.FromSeconds(Math.Max(2, Settings.WakeRestoreSeconds)); _hotplug.Stop(); _hotplug.Start(); } };
        _hotplug.Tick += async (_, _) => { _hotplug.Stop(); await RefreshAsync(); if (_restoreAfterRefresh) { _restoreAfterRefresh = false; await RestoreSavedLevelsAsync(); } };
        _timer.Tick += async (_, _) => await TickAsync();
        if (!IsDemo) RegisterHotkeys();
        await RefreshAsync();
        if (!Program.Options.Background) _window.ShowPanel();
        ConfigureIntegrations();
        if (!IsDemo && Settings.RestoreBrightnessAtStartup) await RestoreSavedLevelsAsync();
        if (!IsDemo && Settings.CheckScheduleAtStartup) await ApplyLevelsAsync(ScheduleEvaluator.GetCurrentLevels(Settings, DateTime.Now, VisibleMonitors.Select(m => m.Id)));
        _ = ServeCommandsAsync();
        if (_loadError is not null) _window.ShowError(_loadError);
        if (Program.Options.HasMonitorCommand || Program.Options.UseTime || Program.Options.Overlay || Program.Options.Settings) await HandleCommandAsync(Program.Options);
        _timer.Start();
        if (IsSmokeTest)
        {
            IReadOnlyList<string> runtimeChecks = [];
            int settingsPages = 0, uiLayoutCases = 0;
            Window? interactiveStart = null;
            try
            {
                interactiveStart = await StartInteractiveFocusVerificationAsync();
                Settings.Schedule.Add(new ScheduleEntry { Enabled = false, Time = "20:00", Brightness = 40 });
                Settings.Hotkeys.Add(new HotkeyBinding { Enabled = false });
                Settings.Monitors["demo:external"] = new MonitorSettings { ShowContrast = true };
                runtimeChecks = await VerifyRuntimeForSmokeTestAsync();
                OpenSettings();
                settingsPages = _settingsWindow!.VerifyPagesForSmokeTest();
                uiLayoutCases = await VerifyUiForSmokeTestAsync();
                await Task.Delay(2000);
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-test.json"), JsonSerializer.Serialize(new { Passed = true, NativeWinUI = true, DemoDisplays = _monitors.Count, SettingsWindow = _settingsWindow is not null, SettingsPages = settingsPages, RuntimeChecks = runtimeChecks, UiLayoutCases = uiLayoutCases, HardwareWrites = 0 }));
            }
            catch (Exception exception)
            {
                Environment.ExitCode = 1;
                Program.Log(exception);
                try
                {
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-test.json"), JsonSerializer.Serialize(new { Passed = false, NativeWinUI = true, Error = exception.ToString(), DemoDisplays = _monitors.Count, SettingsWindow = _settingsWindow is not null, SettingsPages = settingsPages, RuntimeChecks = runtimeChecks, UiLayoutCases = uiLayoutCases, HardwareWrites = 0 }));
                }
                catch (Exception reportException) { Program.Log(reportException); }
            }
            finally { interactiveStart?.Close(); Quit(); }
        }
    }

    internal bool TryGetTrayIconBounds(out Windows.Graphics.RectInt32 bounds)
    {
        bounds = default;
        return _tray is not null && _tray.TryGetIconBounds(out bounds);
    }
    internal void ReturnFocusToTray() => _tray?.ReturnFocusToIcon();
    internal uint CurrentTrayPointerGesture => _tray?.CurrentPointerGesture ?? 0;
    internal void SetTrayPanelVisible(bool visible) => _tray?.SetPanelVisible(visible);

    public MonitorSettings Preferences(string id)
    {
        var preferences = Settings.Monitors.GetValueOrDefault(id) ?? new MonitorSettings();
        if (id.Contains("\\DEL41D9\\", StringComparison.OrdinalIgnoreCase)) preferences = preferences with { SkipRestore = true };
        return Settings.UseSoftwareBrightnessFallback && !preferences.SoftwareFallback ? preferences with { SoftwareFallback = true } : preferences;
    }
    public string DisplayName(MonitorSnapshot monitor) => string.IsNullOrWhiteSpace(Preferences(monitor.Id).Name) ? monitor.Name : Preferences(monitor.Id).Name;
    public double LogicalBrightness(MonitorSnapshot monitor) => BrightnessControl.Logical(monitor, Preferences(monitor.Id));
    public bool CanControl(MonitorSnapshot monitor) => BrightnessControl.CanControl(monitor, Preferences(monitor.Id));
    public IReadOnlyList<VcpFeature> Features(string id) => _features.GetValueOrDefault(id) ?? Array.Empty<VcpFeature>();

    public async Task RefreshAsync()
    {
        if (_quitting) return;
        await _operations.WaitAsync();
        if (_quitting) { _operations.Release(); return; }
        _window.SetRefreshing(true);
        try
        {
            if (!IsDemo) _hardware.Configure(BrightnessControl.Options(Settings, _monitors.Select(m => m.Id)));
            _monitors = IsDemo ? (_monitors.Count == 0 ? CliRunner.DemoMonitors : _monitors) : await _hardware.RefreshAsync(_lifetime.Token);
            foreach (var monitor in _monitors.Where(m => Preferences(m.Id).Features.Any(x => x.Value.Enabled)))
                try { _features[monitor.Id] = await QueryFeaturesAsync(monitor.Id); } catch (Exception ex) { Program.Log(ex); }
            _window.RenderMonitors(); _settingsWindow?.UpdateMonitors(_monitors);
            if (!IsDemo && _hardware.LastRefreshErrors.Count > 0) _window.ShowError(string.Join("\n", _hardware.LastRefreshErrors));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Report(exception); }
        finally { _window.SetRefreshing(false); _operations.Release(); }
    }

    public async Task TrySetAsync(string id, double value, bool contrast = false, bool linked = false, bool throwOnError = false, bool automatic = false)
    {
        if (!automatic && !contrast)
        {
            foreach (var monitor in _monitors.Where(m => linked || id == "all" || m.Id == id)) CancelManualTransition(monitor.Id);
        }
        await _operations.WaitAsync();
        try
        {
            var selected = (linked || id == "all" ? VisibleMonitors : _monitors.Where(x => x.Id == id)).Where(x => contrast ? x.SupportsContrast : CanControl(x)).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("No matching display supports the requested control.");
            foreach (var monitor in selected)
            {
                MonitorSnapshot updated;
                if (contrast)
                {
                    double hardwareValue = Math.Clamp(value, 0, 100);
                    if (!IsDemo) await _hardware.SetContrastAsync(monitor.Id, hardwareValue, _lifetime.Token);
                    updated = monitor with { Contrast = hardwareValue };
                }
                else updated = await BrightnessControl.SetAsync(_hardware, monitor, Preferences(monitor.Id), value, IsDemo, _lifetime.Token);
                _monitors = _monitors.Select(x => x.Id == monitor.Id ? updated : x).ToArray();
                if (!contrast && _dimmed && !automatic) _beforeIdle[monitor.Id] = value;
                if (!contrast)
                {
                    if (!_dimmed) Settings.LastBrightness[monitor.Id] = Math.Clamp(value, 0, 100);
                    foreach (var feature in Preferences(monitor.Id).Features.Where(f => f.Value.Enabled && f.Value.LinkedToBrightness))
                    {
                        double raw = feature.Value.Min + (feature.Value.Max - feature.Value.Min) * Math.Clamp(value / Math.Max(1, feature.Value.MaxVisual), 0, 1);
                        if (!IsDemo) await _hardware.SetVcpAsync(monitor.Id, feature.Key, (uint)Math.Round(raw), _lifetime.Token);
                    }
                    _brightnessDirty = true;
                }
            }
        }
        catch (OperationCanceledException) when (!throwOnError) { }
        catch (Exception exception) when (!throwOnError) { Report(exception); }
        finally { _operations.Release(); }
    }

    public async Task PowerOffAsync(string id)
    {
        try
        {
            if (Settings.PowerOffMode is "ddc" or "both")
                foreach (var monitor in VisibleMonitors.Where(m => (id == "all" || m.Id == id) && m.Connection.Contains("DDC", StringComparison.OrdinalIgnoreCase)))
                    if (!IsDemo) await _hardware.SetVcpAsync(monitor.Id, 0xD6, (uint)Settings.PowerOffValue, _lifetime.Token);
            if (!IsDemo && Settings.PowerOffMode is "windows" or "both") TrayService.TurnOffAllDisplays();
            _window.HidePanel();
        }
        catch (Exception exception) { Report(exception); }
    }

    public async void SaveSettings() => await SaveSettingsAsync();

    private async Task SaveSettingsAsync()
    {
        try
        {
            if (!IsDemo)
            {
                if (_settingsSaveBlocked) throw new IOException(_loadError);
                if (_savedStartup != Settings.RunAtStartup) await StartupService.ApplyAsync(Settings.RunAtStartup);
                _store.Save(Settings);
                _savedStartup = Settings.RunAtStartup;
            }
            LocalizationService.Configure(Settings.Language);
            ConfigureIntegrations();
            _window.ApplySettings();
            if (!IsDemo) RegisterHotkeys();
        }
        catch (Exception exception)
        {
            if (!IsDemo && _savedStartup != Settings.RunAtStartup)
            {
                Settings.RunAtStartup = _savedStartup;
                try { await StartupService.ApplyAsync(_savedStartup); } catch (Exception rollbackError) { Program.Log(rollbackError); }
            }
            Report(exception);
        }
    }

    public void OpenSettings()
    {
        _window.HidePanel();
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(Settings, _monitors, SaveSettings, () => _ = RefreshAsync(), CreateSettingsActions());
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    private void RegisterHotkeys()
    {
        var errors = _tray.RegisterHotkeys(Settings.Hotkeys);
        if (errors.Count > 0) ShowError(string.Join("\n", errors));
    }

    private async Task HandleHotkeyAsync(HotkeyBinding binding)
    {
        await _hotkeyOperations.WaitAsync();
        try
        {
            if (_quitting) return;
            var actions = binding.Actions.Count > 0 ? binding.Actions : new List<HotkeyAction> { new() { Type = binding.Action == "power" ? "power" : "offset", MonitorId = binding.MonitorId, Value = binding.Action == "decrease" ? -binding.Step : binding.Step } };
            if (Settings.LinkedBrightness && Settings.HotkeysBreakLinkedLevels && actions.Any(action => action.Target == "brightness" && action.Type is "offset" or "set" or "cycle"))
            {
                Settings.LinkedBrightness = false;
                await SaveSettingsAsync();
            }
            var firstCycle = actions.FirstOrDefault(action => action.Type == "cycle" && action.Values.Count > 0);
            int cycle = firstCycle is null ? 0 : (_hotkeyCycles.GetValueOrDefault(binding.Id) + 1) % firstCycle.Values.Count;
            foreach (var action in actions) await ExecuteHotkeyActionAsync(action, cycle, binding.NativeKey.Length > 0);
            if (firstCycle is not null) _hotkeyCycles[binding.Id] = cycle;
            _window.RenderMonitors(); ShowOverlay();
        }
        catch (Exception exception) { Report(exception); }
        finally { _hotkeyOperations.Release(); }
    }

    private async Task<string> HandleCommandAsync(CommandLineOptions command)
    {
        if (command.Settings) { OpenSettings(); return "OK"; }
        if (command.UseTime) { await ApplyManualLevelsAsync(ScheduleEvaluator.GetCurrentLevels(Settings, DateTime.Now, VisibleMonitors.Select(m => m.Id)), false); if (command.Overlay) ShowOverlay(); if (command.Panel) _window.ShowPanel(); return "OK"; }
        if (command.List) return JsonSerializer.Serialize(_monitors.Select((m, i) => new { Number = i + 1, m.Id, m.Name, m.Connection, Brightness = LogicalBrightness(m), RawBrightness = m.Brightness, m.SupportsBrightness, m.SupportsContrast, m.Contrast, m.HdrSupported, m.HdrActive, m.SdrBrightness, m.GammaBrightness }), new JsonSerializerOptions { WriteIndented = true });
        var selected = CliRunner.Select(_monitors, command).Where(m => command.Vcp is not null || CanControl(m)).ToArray();
        if (command.HasMonitorCommand && selected.Length == 0) throw new InvalidOperationException("No matching displays were detected.");
        foreach (var monitor in selected)
        {
            if (command.Vcp is { } vcp)
            {
                await SetFeatureAsync(monitor.Id, vcp.Code, vcp.Value);
            }
            else await TrySetAsync(monitor.Id, command.Set ?? LogicalBrightness(monitor) + (command.Offset ?? 0), throwOnError: true);
        }
        if (command.Vcp is not null) await RefreshAsync();
        _window.RenderMonitors();
        if (command.Overlay) ShowOverlay();
        else if (!command.HasMonitorCommand || command.Panel) _window.ShowPanel();
        return "OK";
    }

    private async Task ServeCommandsAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Program.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_lifetime.Token);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(requestTimeout.Token);
                var completion = new TaskCompletionSource<CommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_dispatcher.TryEnqueue(async () =>
                {
                    try
                    {
                        var args = JsonSerializer.Deserialize<string[]>(line ?? "[]") ?? Array.Empty<string>();
                        completion.SetResult(new CommandResponse(true, await HandleCommandAsync(CommandLine.Parse(args))));
                    }
                    catch (Exception ex) { completion.SetResult(new CommandResponse(false, ex.Message)); }
                })) break;
                var response = await completion.Task.WaitAsync(requestTimeout.Token);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch (OperationCanceledException) { if (_lifetime.IsCancellationRequested) break; }
            catch (Exception exception) { Program.Log(exception); }
        }
    }

    private void ShowError(string message)
    {
        if (_quitting) return;
        if (_settingsWindow is not null) _settingsWindow.ShowError(message);
        else _window.ShowError(message);
    }
    private void Report(Exception exception) { Program.Log(exception); ShowError(exception.Message); }
    private async void Quit()
    {
        if (_quitting) return;
        _quitting = true; _timer.Stop(); _hotplug.Stop(); _tray.Dispose();
        foreach (var transition in _transitions.Values) transition.Cancel();
        if (_dimmed)
        {
            foreach (var pair in _beforeIdle.ToArray()) await TrySetAsync(pair.Key, pair.Value, automatic: true);
            _beforeIdle.Clear(); _dimmed = false;
        }
        _lifetime.Cancel();
        PersistLevels(); _udp?.Dispose(); _sensor.Dispose(); _updates.Dispose();
        await Task.Run(() => _hardware.Dispose());
        _overlay?.Close(); _settingsWindow?.Close(); _window.CloseForExit();
        Application.Current.Exit();
    }
}
