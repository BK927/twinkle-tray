using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TwinkleTray.Core;

namespace TwinkleTray.WinUI;

internal static class Program
{
    internal static bool Logging { get; set; } = true;
    internal static CommandLineOptions Options { get; private set; } = new();
    internal static string PipeName => "TwinkleTray.WinUI." + WindowsIdentity.GetCurrent().User?.Value + "." + System.Diagnostics.Process.GetCurrentProcess().SessionId + (Options.Demo || Options.SmokeTest ? ".Demo" : "");

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() == "--apply-update") return Services.UpdateService.ApplyStagedUpdate(args);
            Options = CommandLine.Parse(args);
            if (Options.Help) { WriteConsole(CommandLine.HelpText); return 0; }
            if (Options.Udp) { WriteConsole(Services.UdpCommandClient.RunAsync(Options).GetAwaiter().GetResult()); return 0; }
            using var instance = new Mutex(true, @"Local\" + PipeName, out bool first);
            if (!first)
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                client.Connect(10000);
                using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, leaveOpen: true);
                writer.WriteLine(JsonSerializer.Serialize(args));
                var responseLine = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                var response = JsonSerializer.Deserialize<CommandResponse>(responseLine ?? "{}");
                if (response is not null && !string.IsNullOrWhiteSpace(response.Message)) WriteConsole(response.Message);
                return response?.Success == true ? 0 : 1;
            }
            if ((Options.List || Options.UseTime || Options.HasMonitorCommand) && !Options.Panel && !Options.Overlay)
            {
                var settings = Options.Demo || Options.List ? new AppSettings() : new SettingsStore().Load();
                bool needsGammaOwner = Options.Vcp is null && (Options.HasMonitorCommand || Options.UseTime) && (settings.UseSoftwareBrightnessFallback || settings.Monitors.Values.Any(m => m.MainControl == "gamma" || m.SoftwareFallback || m.ExtendMinimum));
                if (!needsGammaOwner)
                {
                    var result = CliRunner.RunAsync(Options).GetAwaiter().GetResult();
                    WriteConsole(result); return 0;
                }
                Options = Options with { Background = true };
            }
            WinRT.ComWrappersSupport.InitializeComWrappers();
            if (!Options.HasCommand && Services.StartupService.IsPackaged)
            {
                try
                {
                    if (Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs().Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask)
                        Options = Options with { Background = true };
                }
                catch (Exception exception) { Log(exception); }
            }
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App();
            });
            return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            WriteConsole(ex.Message); Log(ex); return 1;
        }
    }

    internal static void Log(Exception exception)
    {
        if (!Logging) return;
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwinkleTray.WinUI");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "errors.log"), $"{DateTimeOffset.Now:O} {exception}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void WriteConsole(string text)
    {
        AttachConsole(uint.MaxValue);
        Console.WriteLine(text);
    }
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint processId);
}

internal sealed record CommandResponse(bool Success, string Message);
