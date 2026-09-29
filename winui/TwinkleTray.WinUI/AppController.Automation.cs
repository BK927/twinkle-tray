using TwinkleTray.Core;
using TwinkleTray.Hardware;
using TwinkleTray.WinUI.Services;

namespace TwinkleTray.WinUI;

internal sealed partial class AppController
{
    private readonly AmbientLightService _sensor = new();
    private readonly Dictionary<string, int> _hotkeyCycles = new();
    private readonly Dictionary<string, double> _beforeProfile = new();
    private AppProfile? _activeProfile;
    private DateTime _lastSensor, _lastPoll, _lastPersist, _lastUpdate;
    private bool _automationPaused, _restoreAfterRefresh, _brightnessDirty;
    private string? _lastAutomationError;
    private DateTime? _idleRestoreDue;
    private readonly Dictionary<string, double> _transitionTargets = new();
    private readonly Dictionary<string, double> _interruptedLevels = new();
    private bool _schedulePending;

    private sealed record AutomationObservation(DateTime Now, bool Locked, string? Foreground, TimeSpan Idle, bool Fullscreen, bool MediaPlaying);

    private async Task TickAsync(AutomationObservation? simulation = null)
    {
        if (simulation is not null && !(IsSmokeTest && IsDemo)) throw new InvalidOperationException("Simulated observations require smoke-test demo mode.");
        if (_tickBusy || _quitting || (IsSmokeTest && simulation is null)) return;
        _tickBusy = true;
        try
        {
            DateTime now = simulation?.Now ?? DateTime.Now;
            if (_brightnessDirty && (now - _lastPersist).TotalSeconds >= 5) PersistLevels();
            if (Settings.CheckForUpdates && !IsDemo && (now - _lastUpdate).TotalHours >= 24)
            {
                _lastUpdate = now;
                _ = CheckUpdatesAsync(false);
            }
            bool locked = Settings.DisableOnLockScreen && (simulation?.Locked ?? DesktopEnvironment.IsSessionLocked() == true);
            if (Settings.PollBrightness && !locked && !_dimmed && (now - _lastPoll).TotalSeconds >= Settings.PollSeconds)
            {
                _lastPoll = now;
                await RefreshAsync();
            }
            var due = ScheduleEvaluator.GetDue(Settings, _lastSchedule, now);
            _lastSchedule = now;
            if (due.Count > 0) _schedulePending = true;
            if (_automationPaused || locked)
            {
                SuspendAutomationTransitions();
                if (!locked) await UpdateIdleAsync(false, simulation);
                return;
            }

            if (_interruptedLevels.Count > 0)
            {
                var resume = _interruptedLevels.ToDictionary();
                _interruptedLevels.Clear();
                await ApplyLevelsAsync(resume);
            }

            // Automatic control priority is idle > foreground profile > light sensor > schedule.
            string? foreground = simulation is null ? DesktopEnvironment.GetForegroundProcessPath() : simulation.Foreground;
            bool ownWindow = foreground is not null && Path.GetFileName(foreground).Equals("TwinkleTray.WinUI.exe", StringComparison.OrdinalIgnoreCase);
            if (!ownWindow)
            {
                var profile = foreground is null ? null : ProfileResolver.Match(foreground, Settings.Profiles);
                if (profile?.Id != _activeProfile?.Id)
                {
                    if (_activeProfile?.RestorePrevious == true) await ApplyLevelsAsync(_beforeProfile, false);
                    _beforeProfile.Clear(); _activeProfile = profile;
                    if (profile is not null)
                    {
                        foreach (var monitor in VisibleMonitors.Where(CanControl)) _beforeProfile[monitor.Id] = _dimmed ? _beforeIdle.GetValueOrDefault(monitor.Id, LogicalBrightness(monitor)) : LogicalBrightness(monitor);
                        await ApplyProfileAsync(profile, automatic: true);
                    }
                }
            }

            var sensorIds = Settings.Sensor.Enabled ? Settings.Sensor.Monitors.Where(x => x.Value.Enabled).Select(x => x.Key).ToHashSet() : new HashSet<string>();
            if (_activeProfile is null && (_schedulePending || Settings.ScheduleInterpolation))
            {
                var levels = ScheduleEvaluator.GetCurrentLevels(Settings, now, VisibleMonitors.Select(m => m.Id));
                await ApplyLevelsAsync(levels.Where(x => !sensorIds.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value), !Settings.ScheduleInterpolation);
                _schedulePending = false;
            }
            if (_activeProfile is null && Settings.Sensor.Enabled && (now - _lastSensor).TotalSeconds >= Settings.Sensor.PollSeconds)
            {
                _lastSensor = now;
                double? lux = Settings.Sensor.Provider == "fake" ? Settings.Sensor.FakeLux : await _sensor.ReadLuxAsync(_lifetime.Token);
                if (lux is { } reading)
                {
                    var levels = Settings.Sensor.Monitors.Where(x => x.Value.Enabled && _monitors.Any(m => m.Id == x.Key))
                        .ToDictionary(x => x.Key, x => (double)SensorCurve.ToBrightness(reading, x.Value));
                    await ApplyLevelsAsync(levels);
                }
                else if (_sensor.LastError is { } error) ReportAutomationError(error);
            }

            await UpdateIdleAsync(true, simulation);
            _lastAutomationError = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportAutomationError(exception.Message); }
        finally { _tickBusy = false; }
    }

    private void SuspendAutomationTransitions()
    {
        foreach (var (id, transition) in _transitions.ToArray())
        {
            if (!transition.IsCancellationRequested && _transitionTargets.TryGetValue(id, out var target)) _interruptedLevels[id] = target;
            transition.Cancel();
        }
    }

    private void CancelManualTransition(string id)
    {
        _interruptedLevels.Remove(id);
        if (_transitions.TryGetValue(id, out var transition)) transition.Cancel();
    }

    private bool IsMainBrightnessChannel(MonitorSnapshot monitor, string channel)
    {
        var preferences = Preferences(monitor.Id);
        if (preferences.MainControl == channel) return true;
        return channel == "gamma" && preferences.MainControl == "brightness" &&
            (preferences.ExtendMinimum || preferences.SoftwareFallback && !monitor.SupportsBrightness);
    }

    private void RememberManualChannelLevel(MonitorSnapshot monitor)
    {
        double level = LogicalBrightness(monitor);
        if (_dimmed) _beforeIdle[monitor.Id] = level;
        else Settings.LastBrightness[monitor.Id] = level;
        _brightnessDirty = true;
    }

    private async Task UpdateIdleAsync(bool allowDim, AutomationObservation? simulation = null)
    {
            DateTime now = simulation?.Now ?? DateTime.Now;
            bool idle = allowDim && !Settings.DisableAutoApply && Settings.IdleEnabled && (simulation?.Idle ?? DesktopEnvironment.IdleTime).TotalSeconds >= Settings.IdleMinutes * 60 + Settings.IdleSeconds;
            if (idle && Settings.IdleCheckFullscreen && (simulation?.Fullscreen ?? DesktopEnvironment.IsFullscreen())) idle = false;
            if (idle && Settings.IdleCheckMedia && (simulation?.MediaPlaying ?? await DesktopEnvironment.IsMediaPlayingAsync(_lifetime.Token))) idle = false;
            if (idle && !_dimmed)
            {
                _beforeIdle.Clear();
                foreach (var monitor in VisibleMonitors.Where(m => CanControl(m) && !Preferences(m.Id).SkipRestore))
                {
                    // A schedule/profile may have just started a transition on this tick.
                    // Resume its destination after idle, rather than losing it at an intermediate level.
                    bool transitioning = _transitions.TryGetValue(monitor.Id, out var transition) && !transition.IsCancellationRequested;
                    _beforeIdle[monitor.Id] = transitioning && _transitionTargets.TryGetValue(monitor.Id, out var target) ? target : LogicalBrightness(monitor);
                }
                foreach (var transition in _transitions.Values) transition.Cancel();
                _dimmed = true;
                foreach (var id in _beforeIdle.Keys) await TrySetAsync(id, Settings.IdleBrightness, automatic: true);
                if (_window.IsShown) _window.RenderMonitors();
            }
            else if (!idle && _dimmed)
            {
                _idleRestoreDue ??= now.AddSeconds(Settings.IdleRestoreSeconds);
                if (now < _idleRestoreDue) return;
                var restored = _beforeIdle.ToDictionary(); _beforeIdle.Clear(); _dimmed = false;
                _idleRestoreDue = null;
                await ApplyLevelsAsync(restored, false);
            }
            if (idle) _idleRestoreDue = null;
    }

    private void ReportAutomationError(string error)
    {
        if (_lastAutomationError == error) return;
        _lastAutomationError = error; Program.Log(new InvalidOperationException(error)); ShowError(error);
    }

    private async Task ApplyLevelsAsync(IReadOnlyDictionary<string, double> levels, bool smooth = true)
    {
        var work = new List<Task>();
        foreach (var pair in levels.ToArray())
        {
            var monitor = _monitors.FirstOrDefault(m => m.Id == pair.Key);
            if (monitor is null || !CanControl(monitor)) continue;
            if (!smooth && _transitions.TryGetValue(pair.Key, out var previous)) previous.Cancel();
            if (_dimmed) { _beforeIdle[pair.Key] = pair.Value; continue; }
            double start = LogicalBrightness(monitor);
            if (Math.Abs(start - pair.Value) < .75) continue;
            if (smooth && Settings.SmoothTransitions && Settings.TransitionSeconds > 0)
            {
                if (!_transitions.TryGetValue(pair.Key, out var running) || running.IsCancellationRequested ||
                    !_transitionTargets.TryGetValue(pair.Key, out var target) || Math.Abs(target - pair.Value) >= .75) _ = TransitionAsync(pair.Key, start, pair.Value);
            }
            else work.Add(TrySetAsync(pair.Key, pair.Value, automatic: true));
        }
        await Task.WhenAll(work);
        if (work.Count > 0 && _window.IsShown) _window.RenderMonitors();
    }

    private async Task TransitionAsync(string id, double from, double to)
    {
        if (_transitions.TryGetValue(id, out var previous)) previous.Cancel();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _transitions[id] = cancel;
        _transitionTargets[id] = to;
        try
        {
            int steps = Math.Max(1, Settings.TransitionSeconds * 4);
            for (int step = 1; step <= steps; step++)
            {
                await Task.Delay(250, cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                await TrySetAsync(id, from + (to - from) * step / steps, automatic: true);
            }
            if (_window.IsShown) _window.RenderMonitors();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Report(exception); }
        finally { if (_transitions.GetValueOrDefault(id) == cancel) { _transitions.Remove(id); _transitionTargets.Remove(id); } }
    }

    private async Task RestoreSavedLevelsAsync() => await ApplyLevelsAsync(Settings.LastBrightness
        .Where(x => !Preferences(x.Key).SkipRestore).ToDictionary(x => x.Key, x => x.Value), false);

    private Task ApplyManualLevelsAsync(IReadOnlyDictionary<string, double> levels, bool smooth = true)
    {
        // Clear earlier intent even when the requested value already matches the current level.
        foreach (var id in levels.Keys) CancelManualTransition(id);
        return ApplyLevelsAsync(levels, smooth);
    }

    private Task ApplyProfileAsync(AppProfile profile) => ApplyProfileAsync(profile, automatic: false);

    private async Task ApplyProfileAsync(AppProfile profile, bool automatic)
    {
        var levels = profile.Brightness.ToDictionary(x => x.Key, x => (double)x.Value);
        if (automatic) await ApplyLevelsAsync(levels);
        else await ApplyManualLevelsAsync(levels);
        _window.RenderMonitors();
        if (profile.OverlayType is not ("none" or "disabled")) ShowOverlay(profile.OverlayType is "force" or "aggressive");
    }

    private async Task ExecuteHotkeyActionAsync(HotkeyAction action, int cycle, bool nativeBrightnessKey = false)
    {
        if (action.Type is not ("offset" or "set" or "cycle" or "power" or "profile" or "refresh" or "panel" or "vcp") || action.Target is not ("brightness" or "sdr" or "gamma" or "vcp")) throw new ArgumentException("The hotkey contains an unsupported action. Edit it in Settings.");
        if (action.Type == "cycle" && action.Values.Count == 0) return;
        if (action.Type == "power") { await PowerOffAsync(action.MonitorId); return; }
        if (action.Type == "panel") { _window.TogglePanel(); return; }
        if (action.Type == "refresh") { await RefreshAsync(); return; }
        if (action.Type == "profile")
        {
            var profile = Settings.Profiles.FirstOrDefault(x => x.Id == action.ProfileId);
            if (profile is not null) await ApplyProfileAsync(profile);
            return;
        }
        string id = Settings.LinkedBrightness && !Settings.HotkeysBreakLinkedLevels && action.Target == "brightness" ? "all" : action.MonitorId;
        foreach (var monitor in VisibleMonitors.Where(m => id == "all" || m.Id == id).ToArray())
        {
            if (nativeBrightnessKey && action.Type == "offset" && action.Target == "brightness" && monitor.Connection.Contains("WMI", StringComparison.OrdinalIgnoreCase)) continue;
            double value = action.Type == "cycle" && action.Values.Count > 0 ? action.Values[cycle % action.Values.Count] : action.Value;
            if (action.Target == "vcp" || action.Type == "vcp")
            {
                if (action.Type == "offset")
                {
                    var feature = await QueryFeatureAsync(monitor.Id, action.Vcp);
                    value += feature.Current;
                }
                await SetFeatureAsync(monitor.Id, action.Vcp, value);
            }
            else if (action.Target is "gamma" or "sdr")
            {
                if (action.Type == "offset") value += action.Target == "gamma" ? monitor.GammaBrightness ?? 100 : monitor.SdrBrightness ?? 0;
                value = Math.Clamp(value, action.Target == "gamma" ? 20 : 0, 100);
                bool mainChannel = IsMainBrightnessChannel(monitor, action.Target);
                if (mainChannel) CancelManualTransition(monitor.Id);
                await _operations.WaitAsync();
                try
                {
                    if (!IsDemo)
                    {
                        if (action.Target == "gamma") await _hardware.SetGammaBrightnessAsync(monitor.Id, value, _lifetime.Token);
                        else await _hardware.SetSdrBrightnessAsync(monitor.Id, value, _lifetime.Token);
                    }
                    _monitors = _monitors.Select(m => m.Id != monitor.Id ? m : action.Target == "gamma" ? m with { GammaBrightness = value } : m with { SdrBrightness = value }).ToArray();
                    if (mainChannel) RememberManualChannelLevel(_monitors.Single(m => m.Id == monitor.Id));
                }
                finally { _operations.Release(); }
            }
            else if (CanControl(monitor)) await TrySetAsync(monitor.Id, action.Type == "offset" ? LogicalBrightness(monitor) + value : value, throwOnError: true);
        }
    }

    private async Task OffsetAllAsync(double offset)
    {
        await _hotkeyOperations.WaitAsync();
        try
        {
            foreach (var monitor in VisibleMonitors.Where(CanControl).ToArray()) await TrySetAsync(monitor.Id, LogicalBrightness(monitor) + offset);
            _window.RenderMonitors(); ShowOverlay();
        }
        finally { _hotkeyOperations.Release(); }
    }

    private void ShowOverlay(bool force = false)
    {
        if (_quitting || (!force && (!Settings.ShowOverlay || _activeProfile?.OverlayType is "none" or "disabled"))) return;
        string policy = _activeProfile?.OverlayType is "safe" or "aggressive" ? _activeProfile.OverlayType : Settings.OverlayPolicy;
        if (!force && policy == "safe" && DesktopEnvironment.IsFullscreen()) return;
        _overlay ??= new OverlayWindow();
        _overlay.ShowLevels(VisibleMonitors.Where(CanControl).Select(m => (DisplayName(m), LogicalBrightness(m))), Settings);
    }

    private void PersistLevels()
    {
        if (IsDemo || _settingsSaveBlocked || !_brightnessDirty) return;
        try { _store.Save(Settings); _brightnessDirty = false; _lastPersist = DateTime.Now; }
        catch (Exception exception) { Program.Log(exception); }
    }
}
