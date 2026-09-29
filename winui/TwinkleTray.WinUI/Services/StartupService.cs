using Microsoft.Win32;

namespace TwinkleTray.WinUI.Services;

internal static class StartupService
{
    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        const string name = "TwinkleTray.WinUI";
        if (enabled) key.SetValue(name, $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue(name, false);
    }
}
