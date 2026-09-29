using System.IO.Pipes;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TwinkleTray.Core;
using TwinkleTray.Hardware;
using TwinkleTray.WinUI.Services;

namespace TwinkleTray.WinUI;

internal sealed class AppController
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
    public AppSettings Settings { get; private set; }
    public bool IsDemo => Program.Options.Demo || Program.Options.SmokeTest;
    public bool IsSmokeTest => Program.Options.SmokeTest;
    public IEnumerable<MonitorSnapshot> VisibleMonitors => _monitors.Where(m => !Preferences(m.Id).Hidden).OrderBy(m => Preferences(m.Id).Order);

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
        _tray.Clicked += () => { _window.TogglePanel(); if (_window.IsShown) _ = RefreshAsync(); };
        _tray.SettingsRequested += OpenSettings;
        _tray.RefreshRequested += () => _ = RefreshAsync();
        _tray.ExitRequested += Quit;
        _tray.HotkeyPressed += binding => _ = HandleHotkeyAsync(binding);
        _tray.DisplaysChanged += () => { _hotplug.Stop(); _hotplug.Start(); };
        _hotplug.Tick += async (_, _) => { _hotplug.Stop(); await RefreshAsync(); };
        _timer.Tick += async (_, _) => await TickAsync();
        if (!IsDemo) RegisterHotkeys();
        if (!Program.Options.Background) _window.ShowPanel();
        await RefreshAsync();
        _ = ServeCommandsAsync();
        if (_loadError is not null) _window.ShowError(_loadError);
        if (Program.Options.HasMonitorCommand) await HandleCommandAsync(Program.Options);
        _timer.Start();
        if (IsSmokeTest)
        {
            Settings.Schedule.Add(new ScheduleEntry { Enabled = false, Time = "20:00", Brightness = 40 });
            Settings.Hotkeys.Add(new HotkeyBinding { Enabled = false });
            Settings.Monitors["demo:external"] = new MonitorSettings { ShowContrast = true };
            OpenSettings();
            int settingsPages = _settingsWindow!.VerifyPagesForSmokeTest();
            await Task.Delay(2000);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-test.json"), JsonSerializer.Serialize(new { Passed = true, NativeWinUI = true, DemoDisplays = _monitors.Count, SettingsWindow = _settingsWindow is not null, SettingsPages = settingsPages, HardwareWrites = 0 }));
            Quit();
        }
    }

    public MonitorSettings Preferences(string id) => Settings.Monitors.GetValueOrDefault(id) ?? new MonitorSettings();
    public string DisplayName(MonitorSnapshot monitor) => string.IsNullOrWhiteSpace(Preferences(monitor.Id).Name) ? monitor.Name : Preferences(monitor.Id).Name;
    public double LogicalBrightness(MonitorSnapshot monitor) => BrightnessMath.ToLogical(monitor.Brightness, Preferences(monitor.Id).MinBrightness, Preferences(monitor.Id).MaxBrightness);

    public async Task RefreshAsync()
    {
        if (_quitting) return;
        await _operations.WaitAsync();
        if (_quitting) { _operations.Release(); return; }
        _window.SetRefreshing(true);
        try
        {
            _monitors = IsDemo ? (_monitors.Count == 0 ? CliRunner.DemoMonitors : _monitors) : await _hardware.RefreshAsync(_lifetime.Token);
            _window.RenderMonitors(); _settingsWindow?.UpdateMonitors(_monitors);
            if (!IsDemo && _hardware.LastRefreshErrors.Count > 0) _window.ShowError(string.Join("\n", _hardware.LastRefreshErrors));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Report(exception); }
        finally { _window.SetRefreshing(false); _operations.Release(); }
    }

    public async Task TrySetAsync(string id, double value, bool contrast = false, bool linked = false, bool throwOnError = false, bool automatic = false)
    {
        await _operations.WaitAsync();
        try
        {
            var selected = (linked || id == "all" ? VisibleMonitors : _monitors.Where(x => x.Id == id)).Where(x => contrast ? x.SupportsContrast : x.SupportsBrightness).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("No matching display supports the requested control.");
            foreach (var monitor in selected)
            {
                if (contrast ? !monitor.SupportsContrast : !monitor.SupportsBrightness) continue;
                double hardwareValue = contrast ? Math.Clamp(value, 0, 100) : BrightnessMath.ToHardware(value, Preferences(monitor.Id).MinBrightness, Preferences(monitor.Id).MaxBrightness);
                if (!IsDemo)
                {
                    if (contrast) await _hardware.SetContrastAsync(monitor.Id, hardwareValue, _lifetime.Token);
                    else await _hardware.SetBrightnessAsync(monitor.Id, hardwareValue, _lifetime.Token);
                }
                _monitors = _monitors.Select(x => x.Id != monitor.Id ? x : contrast ? x with { Contrast = hardwareValue } : x with { Brightness = hardwareValue }).ToArray();
                if (!contrast && _dimmed && !automatic) _beforeIdle[monitor.Id] = value;
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
            foreach (var monitor in VisibleMonitors.Where(m => (id == "all" || m.Id == id) && m.Connection.Contains("DDC", StringComparison.OrdinalIgnoreCase)))
                if (!IsDemo) await _hardware.PowerOffAsync(monitor.Id, _lifetime.Token);
            _window.HidePanel();
        }
        catch (Exception exception) { Report(exception); }
    }

    public void SaveSettings()
    {
        try
        {
            if (!IsDemo)
            {
                if (_settingsSaveBlocked) throw new IOException(_loadError);
                if (_savedStartup != Settings.RunAtStartup) StartupService.Apply(Settings.RunAtStartup);
                _store.Save(Settings);
                _savedStartup = Settings.RunAtStartup;
            }
            LocalizationService.Configure(Settings.Language);
            _window.ApplySettings();
            if (!IsDemo) RegisterHotkeys();
        }
        catch (Exception exception)
        {
            if (!IsDemo && _savedStartup != Settings.RunAtStartup)
            {
                Settings.RunAtStartup = _savedStartup;
                try { StartupService.Apply(_savedStartup); } catch (Exception rollbackError) { Program.Log(rollbackError); }
            }
            Report(exception);
        }
    }

    public void OpenSettings()
    {
        _window.HidePanel();
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(Settings, _monitors, SaveSettings, () => _ = RefreshAsync());
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
            if (binding.Action == "power") { await PowerOffAsync(binding.MonitorId); return; }
            foreach (var monitor in VisibleMonitors.Where(m => m.SupportsBrightness && (binding.MonitorId == "all" || m.Id == binding.MonitorId)).ToArray())
                await TrySetAsync(monitor.Id, LogicalBrightness(monitor) + (binding.Action == "decrease" ? -binding.Step : binding.Step));
            _window.RenderMonitors(); _window.ShowPanel();
        }
        finally { _hotkeyOperations.Release(); }
    }

    private async Task TickAsync()
    {
        if (_tickBusy || _quitting || IsSmokeTest) return;
        _tickBusy = true;
        try
        {
            DateTime now = DateTime.Now;
            var due = ScheduleEvaluator.GetDue(Settings.Schedule, _lastSchedule, now);
            _lastSchedule = now;
            foreach (var schedule in due)
            {
                if (_dimmed)
                {
                    foreach (var monitor in VisibleMonitors.Where(m => schedule.MonitorId == "all" || m.Id == schedule.MonitorId)) _beforeIdle[monitor.Id] = schedule.Brightness;
                }
                else await TrySetAsync(schedule.MonitorId, schedule.Brightness, automatic: true);
            }
            bool idle = Settings.IdleEnabled && TrayService.IdleTime.TotalMinutes >= Settings.IdleMinutes;
            if (idle && !_dimmed)
            {
                _beforeIdle.Clear();
                foreach (var monitor in VisibleMonitors.Where(m => m.SupportsBrightness)) _beforeIdle[monitor.Id] = LogicalBrightness(monitor);
                _dimmed = true;
                await TrySetAsync("all", Settings.IdleBrightness, automatic: true);
            }
            else if (!idle && _dimmed)
            {
                _dimmed = false;
                foreach (var pair in _beforeIdle.ToArray()) await TrySetAsync(pair.Key, pair.Value, automatic: true);
                _beforeIdle.Clear();
            }
            if ((due.Count > 0 || idle) && _window.IsShown) _window.RenderMonitors();
        }
        catch (Exception exception) { Report(exception); }
        finally { _tickBusy = false; }
    }

    private async Task<string> HandleCommandAsync(CommandLineOptions command)
    {
        if (command.List) return JsonSerializer.Serialize(_monitors.Select((m, i) => new { Number = i + 1, m.Id, m.Name, m.Connection, m.Brightness, m.SupportsBrightness, m.SupportsContrast, m.Contrast }), new JsonSerializerOptions { WriteIndented = true });
        var selected = CliRunner.Select(_monitors, command).Where(m => command.Vcp is not null || m.SupportsBrightness).ToArray();
        if (command.HasMonitorCommand && selected.Length == 0) throw new InvalidOperationException("No matching displays were detected.");
        foreach (var monitor in selected)
        {
            if (command.Vcp is { } vcp)
            {
                await _operations.WaitAsync();
                try { if (!IsDemo) await _hardware.SetVcpAsync(monitor.Id, vcp.Code, vcp.Value, _lifetime.Token); }
                finally { _operations.Release(); }
            }
            else await TrySetAsync(monitor.Id, command.Set ?? LogicalBrightness(monitor) + (command.Offset ?? 0), throwOnError: true);
        }
        if (command.Vcp is not null) await RefreshAsync();
        _window.RenderMonitors();
        if (!command.HasMonitorCommand || command.Panel || command.Overlay) _window.ShowPanel();
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
        if (_dimmed)
        {
            foreach (var pair in _beforeIdle.ToArray()) await TrySetAsync(pair.Key, pair.Value, automatic: true);
            _beforeIdle.Clear(); _dimmed = false;
        }
        _lifetime.Cancel();
        _settingsWindow?.Close(); _window.CloseForExit();
        // MonitorService serializes disposal behind pending native calls off the UI thread.
        _ = Task.Run(() => _hardware.Dispose());
        Application.Current.Exit();
    }
}
