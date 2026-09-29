using System.Reflection;
using System.Runtime.InteropServices;
using TwinkleTray.Hardware;

// No path in this program calls a hardware write method with valid arguments.
if (!args.Contains("--read-only", StringComparer.Ordinal))
{
    Console.WriteLine("Use --read-only to enumerate displays twice and check monitor service invariants. No hardware values are written.");
    return 0;
}

try
{
    var normalizer = typeof(MonitorService).Assembly.GetType("TwinkleTray.Hardware.Identity")!
        .GetMethod("Normalize", BindingFlags.Static | BindingFlags.NonPublic)!;
    var interfaceId = (string)normalizer.Invoke(null, [@"\\?\DISPLAY#ACM1234#5&123456&0&UID123#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"])!;
    var wmiId = (string)normalizer.Invoke(null, [@"DISPLAY\ACM1234\5&123456&0&UID123_0"])!;
    Check(interfaceId == wmiId, "WMI and display-interface identities did not match.");
    Check(interfaceId == @"DISPLAY\ACM1234\5&123456&0&UID123", "Normalized device identity is incorrect.");
    var hardware = typeof(MonitorService).Assembly;
    foreach (var (type, expected) in new[] { ("Header", 20), ("PathInfo", 72), ("TargetName", 420), ("SourceName", 84), ("AdvancedColor", 32), ("AdvancedColor2", 36), ("WhiteLevel", 24), ("SetWhiteLevel", 28) })
        Check(Marshal.SizeOf(hardware.GetType("TwinkleTray.Hardware.DisplayColor+" + type)!) == expected, "Incorrect native layout: " + type);
    Check(Marshal.SizeOf(hardware.GetType("TwinkleTray.Hardware.DxgiColor+OutputDesc")!) == (nint.Size == 8 ? 152 : 144), "Incorrect DXGI output layout.");
    var parse = hardware.GetType("TwinkleTray.Hardware.VcpCapabilities")!.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!;
    var parsed = (Dictionary<byte, IReadOnlyList<uint>>)parse.Invoke(null, ["(prot(monitor) model(Test) vcp(10 12 14(01 05 08) 60(0F 11 12) D6(01 04 05)))"])!;
    Check(parsed.Count == 5 && parsed[0x14].SequenceEqual(new uint[] { 1, 5, 8 }) && parsed[0x10].Count == 0, "VCP capability values were parsed incorrectly.");
    try { parse.Invoke(null, ["(vcp(10 14(01"]); throw new InvalidOperationException("Malformed VCP data was accepted."); }
    catch (TargetInvocationException ex) when (ex.InnerException is FormatException) { }
    var gammaType = hardware.GetType("TwinkleTray.Hardware.GammaController")!;
    var buildRamp = gammaType.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic)!;
    var measureRamp = gammaType.GetMethod("Measure", BindingFlags.Static | BindingFlags.NonPublic)!;
    var original = Enumerable.Range(0, 768).Select(i => (ushort)((i % 256) * 257)).ToArray();
    foreach (double percent in new double[] { 20, 35, 60, 75, 100 })
    {
        var ramp = (ushort[])buildRamp.Invoke(null, [original, percent])!;
        var observed = (double)measureRamp.Invoke(null, [original, ramp])!;
        Check(Math.Abs(observed - percent) <= 1, "Software brightness curve failed its read-back invariant.");
        Check(Enumerable.Range(1, 255).All(i => ramp[i] >= ramp[i - 1]), "Gamma curve must be monotonic.");
        if (percent == 100) Check(ramp.SequenceEqual(original), "100% gamma must preserve the original ramp exactly.");
    }

    using var service = new MonitorService();
    service.Configure(new HardwareOptions { EnableGamma = true }); // Read ramps only; no gamma write is called.
    var first = await service.RefreshAsync();
    var second = await service.RefreshAsync();
    foreach (var monitor in second)
    {
        Check(!monitor.SupportsBrightness || double.IsFinite(monitor.Brightness) && monitor.Brightness is >= 0 and <= 100,
            "A display returned an invalid brightness percentage.");
        Check(monitor.SupportsContrast == monitor.Contrast.HasValue,
            "Contrast availability does not match its reading.");
        Check(monitor.Contrast is null or >= 0 and <= 100, "A display returned an invalid contrast percentage.");
        Console.WriteLine($"{monitor.Name} | {monitor.Connection} | Brightness: {(monitor.SupportsBrightness ? monitor.Brightness : "unavailable")} | Contrast: {(monitor.Contrast is { } contrast ? contrast : "unavailable")}");
        Console.WriteLine($"  {monitor.Id}");
        Console.WriteLine($"  GDI: {monitor.DeviceName}; HDR supported/active: {monitor.HdrSupported}/{monitor.HdrActive}; SDR: {monitor.SdrBrightness}; gamma: {monitor.GammaBrightness}");
        if (args.Contains("--features", StringComparer.Ordinal) && monitor.Connection.Contains("DDC", StringComparison.OrdinalIgnoreCase))
        {
            var features = await service.GetFeaturesAsync(monitor.Id);
            Console.WriteLine("  Readable VCP: " + string.Join(", ", features.Select(feature => $"0x{feature.Code:X2} {feature.Name}={feature.Current}/{feature.Maximum}")));
            if (features.Any(feature => feature.Code == monitor.BrightnessVcp))
            {
                var brightness = await service.GetVcpAsync(monitor.Id, monitor.BrightnessVcp);
                Check(brightness.Code == monitor.BrightnessVcp && brightness.Current <= brightness.Maximum && brightness.Maximum > 0,
                    "Individual VCP brightness reading did not match the detected control.");
            }
        }
    }
    Check(second.Select(monitor => monitor.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == second.Count,
        "Duplicate monitor identities were returned.");
    Console.WriteLine($"First refresh: {first.Count}; second refresh: {second.Count}.");
    Console.WriteLine($"Stable IDs across refreshes: {first.Select(monitor => monitor.Id).Order().SequenceEqual(second.Select(monitor => monitor.Id).Order())}");
    foreach (var issue in service.LastRefreshErrors) Console.WriteLine($"Driver diagnostic: {issue}");

    await Expect<ArgumentOutOfRangeException>(() => service.SetBrightnessAsync("invalid", double.NaN));
    await Expect<ArgumentOutOfRangeException>(() => service.SetContrastAsync("invalid", 101));
    await Expect<ArgumentOutOfRangeException>(() => service.SetVcpAsync("invalid", 0x10, uint.MaxValue));
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await Expect<OperationCanceledException>(() => service.RefreshAsync(canceled.Token));
    await Expect<OperationCanceledException>(() => service.GetVcpAsync("invalid", 0x10, canceled.Token));
    service.Dispose();
    service.Dispose();
    await Expect<ObjectDisposedException>(() => service.RefreshAsync());
    if (args.Contains("--environment", StringComparer.Ordinal)) await EnvironmentProbe.RunAsync();
    Console.WriteLine("Read-only probe passed; no brightness, contrast, VCP, or power settings were changed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task Expect<TException>(Func<Task> operation) where TException : Exception
{
    try { await operation(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
