using System.Reflection;
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

    using var service = new MonitorService();
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
    service.Dispose();
    service.Dispose();
    await Expect<ObjectDisposedException>(() => service.RefreshAsync());
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
