namespace TwinkleTray.Hardware;

public sealed partial class MonitorService
{
    private HardwareOptions _options = new();
    private readonly GammaController _gamma = new();

    /// <summary>Reads one raw MCCS feature, including custom codes not listed by the monitor's capabilities string.</summary>
    public Task<VcpFeature> GetVcpAsync(string id, byte code, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            var display = Find(id);
            var handle = RequireHandle(display);
            WaitBeforeVcpRead(Volatile.Read(ref _options), cancellationToken);
            if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, code, out _, out var current, out var maximum))
                throw NativeFailure($"VCP 0x{code:X2} could not be read for {display.Snapshot.Name}");
            if (current > ushort.MaxValue) throw new InvalidOperationException($"The display returned an invalid value for VCP 0x{code:X2}.");
            var allowed = display.Capabilities?.GetValueOrDefault(code) ?? Array.Empty<uint>();
            if (VcpCapabilities.Names.ContainsKey(code) && !VcpCapabilities.IsContinuous(code) && code != display.BrightnessVcp)
                maximum = allowed.Count > 0 ? allowed.Max() : 0;
            if (maximum > ushort.MaxValue) maximum = 0;
            return new VcpFeature(code, code == display.BrightnessVcp ? "Brightness" :
                VcpCapabilities.Names.GetValueOrDefault(code) ?? $"VCP 0x{code:X2}", current, maximum, allowed);
        }, cancellationToken);

    /// <summary>Snapshots settings. Refresh to re-detect support after changing providers or VCP overrides.</summary>
    public void Configure(HardwareOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var next = options.Copy();
        var previous = Interlocked.Exchange(ref _options, next);
        if (previous.EnableGamma && !next.EnableGamma || previous.SoftwareFallback && !next.SoftwareFallback ||
            previous.GammaDisplays.Except(next.GammaDisplays, StringComparer.OrdinalIgnoreCase).Any())
            _ = RestoreDisabledGammaAsync(next, previous);
    }

    private async Task RestoreDisabledGammaAsync(HardwareOptions next, HardwareOptions previous)
    {
        try
        {
            await RunAsync(() =>
            {
                var disabled = _displays.Values.Where(display => !GammaAllowed(display.Snapshot, next) ||
                    previous.GammaDisplays.Contains(display.Snapshot.Id) && !next.GammaDisplays.Contains(display.Snapshot.Id))
                    .Select(display => display.Snapshot.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var errors = _gamma.Restore(name => !next.EnableGamma && !next.SoftwareFallback && next.GammaDisplays.Count == 0 || disabled.Contains(name));
                if (errors.Count > 0) AppendDiagnostics(errors);
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { } // Dispose restores owned ramps under the same gate.
        catch (Exception ex) { AppendDiagnostics(["Software brightness restoration failed: " + ex.Message]); }
    }

    public Task<IReadOnlyList<VcpFeature>> GetFeaturesAsync(string id, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<VcpFeature>>(() =>
        {
            var display = Find(id);
            var handle = RequireHandle(display);
            try { display.Capabilities ??= VcpCapabilities.Read(handle); }
            catch (FormatException ex) { AppendDiagnostics([display.Snapshot.Name + ": " + ex.Message]); }
            var wanted = VcpCapabilities.Names.Keys.Append(display.BrightnessVcp).ToHashSet();
            foreach (var (key, codes) in Volatile.Read(ref _options).CustomVcpCodes)
                if (Matches(key, display.Snapshot.Id, display.Snapshot.DeviceInstanceId)) wanted.UnionWith(codes);
            var features = new List<VcpFeature>();
            foreach (var code in wanted.Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (display.Capabilities is { Count: > 0 } capabilities && !capabilities.ContainsKey(code)) continue;
                WaitBeforeVcpRead(Volatile.Read(ref _options), cancellationToken);
                if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, code, out _, out var current, out var maximum)) continue;
                var allowed = display.Capabilities?.GetValueOrDefault(code) ?? Array.Empty<uint>();
                if (current > ushort.MaxValue) continue;
                // MCCS does not define a maximum for non-continuous controls.
                if (!VcpCapabilities.IsContinuous(code) && code != display.BrightnessVcp)
                    maximum = allowed.Count > 0 ? allowed.Max() : 0;
                if (maximum > ushort.MaxValue) maximum = 0;
                features.Add(new VcpFeature(code, VcpCapabilities.Names.GetValueOrDefault(code) ?? $"VCP 0x{code:X2}", current, maximum, allowed));
            }
            return features.AsReadOnly();
        }, cancellationToken);

    public Task SetSdrBrightnessAsync(string id, double percentage, CancellationToken cancellationToken = default)
    {
        ValidatePercentage(percentage);
        return RunAsync(() =>
        {
            var display = Find(id);
            // Resolve the current target again: a disconnected target ID must never be
            // reused to adjust a different display after a topology change.
            var errors = new List<string>();
            var force = ForceHdrFor(Volatile.Read(ref _options), display.Snapshot);
            var forcedIds = force ? new HashSet<string>([display.Snapshot.DeviceInstanceId], StringComparer.OrdinalIgnoreCase) : null;
            var current = DisplayColor.Read(errors, forcedIds).GetValueOrDefault(display.Snapshot.DeviceInstanceId);
            if (current is null) throw new InvalidOperationException("The selected HDR display is no longer available. Refresh displays and try again.");
            cancellationToken.ThrowIfCancellationRequested();
            DisplayColor.SetSdrBrightness(current, percentage, force);
            return true;
        }, cancellationToken);
    }

    public Task SetGammaBrightnessAsync(string id, double percentage, CancellationToken cancellationToken = default)
    {
        ValidatePercentage(percentage);
        return RunAsync(() =>
        {
            var display = Find(id);
            if (display.Snapshot.HdrActive) throw new NotSupportedException("GDI software brightness is unavailable while HDR is active.");
            cancellationToken.ThrowIfCancellationRequested();
            _gamma.Set(display.Snapshot.DeviceName, display.Snapshot.DeviceInstanceId, percentage);
            return true;
        }, cancellationToken);
    }

    public Task RestoreGammaAsync(CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        var errors = _gamma.Restore();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        return true;
    }, cancellationToken);

    private void EnrichDisplays(Dictionary<string, DisplayEntry> displays, WmiInventory inventory, HardwareOptions options,
        List<string> errors, CancellationToken cancellationToken)
    {
        var forcedIds = displays.Values.Where(display => ForceHdrFor(options, display.Snapshot)).Select(display => display.Snapshot.DeviceInstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var colors = DisplayColor.Read(errors, forcedIds);
        foreach (var (id, display) in displays.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var color = colors.GetValueOrDefault(display.Snapshot.DeviceInstanceId);
            if (color is null) continue;
            displays[id] = display with
            {
                Color = color,
                Snapshot = display.Snapshot with
                {
                    Name = string.IsNullOrWhiteSpace(color.Name) ? display.Snapshot.Name : color.Name,
                    DeviceName = color.DeviceName, HdrSupported = color.HdrSupported,
                    HdrActive = color.HdrActive, HdrModeKnown = color.HdrModeKnown, SdrBrightness = color.SdrBrightness
                }
            };
        }

        if (!options.DisableAppleStudio)
        {
            var apples = AppleStudioDisplay.Read(errors, cancellationToken);
            var aliases = displays.Values.Where(display => display.Snapshot.DeviceInstanceId.Contains("\\APP", StringComparison.OrdinalIgnoreCase) ||
                display.Snapshot.Name.Replace(" ", "").Contains("StudioDisplay", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var apple in apples)
            {
                var matches = aliases.Where(display => apple.Serial.Length > 0 && string.Equals(
                    inventory.Serials.GetValueOrDefault(display.Snapshot.DeviceInstanceId), apple.Serial, StringComparison.OrdinalIgnoreCase)).ToArray();
                var alias = matches.Length == 1 ? matches[0] : apples.Count == 1 && aliases.Count == 1 ? aliases[0] : null;
                if (alias is not null)
                {
                    aliases.Remove(alias);
                    displays[alias.Snapshot.Id] = alias with { Apple = apple,
                        Snapshot = alias.Snapshot with { Brightness = apple.Brightness, SupportsBrightness = true, Connection = "Apple USB HID" } };
                }
                else
                {
                    var id = "apple:" + (apple.Serial.Length > 0 ? apple.Serial : apple.Path);
                    displays[id] = new DisplayEntry(new MonitorSnapshot(id, apple.Name, "Apple USB HID", apple.Brightness, true, false, null)
                    { DeviceInstanceId = apple.Path }, null, null, 100, 0) { Apple = apple };
                }
            }
            foreach (var alias in aliases.Where(display => !display.Snapshot.SupportsBrightness))
                errors.Add($"{alias.Snapshot.Name}: no accessible Apple brightness HID interface (MI_07) was found. The native Windows HID driver must expose this interface; no drivers were changed.");
        }

        foreach (var (id, display) in displays.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = display.Snapshot;
            if (snapshot.HdrActive || display.Color is null || !GammaAllowed(snapshot, options)) continue;
            var gamma = _gamma.Read(snapshot.DeviceName);
            snapshot = snapshot with { GammaBrightness = gamma };
            // Hardware brightness remains independent. The application explicitly
            // chooses hardware, SDR white level, or gamma for its main slider.
            displays[id] = display with { Snapshot = snapshot };
        }
    }

    private static bool GammaAllowed(MonitorSnapshot display, HardwareOptions options) => options.EnableGamma ||
        options.GammaDisplays.Contains(display.Id) || options.SoftwareFallback;

    private static bool ForceHdrFor(HardwareOptions options, MonitorSnapshot display) => options.ForceHdrDisplays.Any(key => Matches(key, display.Id, display.DeviceInstanceId));

    private static void WaitBeforeVcpRead(HardwareOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.VcpReadDelayMilliseconds > 0 && cancellationToken.WaitHandle.WaitOne(options.VcpReadDelayMilliseconds))
            cancellationToken.ThrowIfCancellationRequested();
    }

    private static byte BrightnessVcpFor(HardwareOptions options, string id, string deviceId)
    {
        foreach (var key in new[] { id, deviceId, deviceId.Split('\\').ElementAtOrDefault(1) ?? "" })
            if (options.BrightnessCodes.TryGetValue(key, out var code)) return code;
        // Upstream monitor-rules.json brightness overrides.
        return deviceId.Split('\\').ElementAtOrDefault(1) switch { "FUS087C" => 0x6B, "FUS06AB" => 0x13, _ => BrightnessCode };
    }
    private static bool Matches(string key, string id, string deviceId) => string.Equals(key, id, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, deviceId, StringComparison.OrdinalIgnoreCase) || string.Equals(key, deviceId.Split('\\').ElementAtOrDefault(1), StringComparison.OrdinalIgnoreCase);

    private void AppendDiagnostics(IEnumerable<string> errors)
    {
        var current = Volatile.Read(ref _lastRefreshErrors);
        Volatile.Write(ref _lastRefreshErrors, Array.AsReadOnly(current.Concat(errors).Distinct().ToArray()));
    }
}
