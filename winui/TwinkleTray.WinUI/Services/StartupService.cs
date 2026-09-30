using Microsoft.Win32;

namespace TwinkleTray.WinUI.Services;

internal static class StartupService
{
    public static bool IsPackaged
    {
        get { try { _ = Windows.ApplicationModel.Package.Current.Id; return true; } catch (InvalidOperationException) { return false; } catch (System.Runtime.InteropServices.COMException) { return false; } }
    }

    public static async Task ApplyAsync(bool enabled)
    {
        if (IsPackaged)
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync("TwinkleTrayStartup");
            if (!enabled) { task.Disable(); return; }
            var state = await task.RequestEnableAsync();
            if (state is not (Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy))
                throw new InvalidOperationException("Windows blocked startup for this application. Enable it in Windows Settings → Apps → Startup.");
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        const string name = "TwinkleTray.WinUI";
        if (enabled) key.SetValue(name, $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue(name, false);
    }
}
