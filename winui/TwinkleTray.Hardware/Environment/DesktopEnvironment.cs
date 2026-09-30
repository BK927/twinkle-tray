using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Media.Control;

namespace TwinkleTray.Hardware;

/// <summary>Read-only observations of the current interactive Windows session.</summary>
public static class DesktopEnvironment
{
    public static string? GetForegroundProcessPath()
    {
        var window = GetForegroundWindow();
        if (window == 0) return null;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return null;
        using var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) return null;
        var path = new StringBuilder(32768);
        var capacity = (uint)path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref capacity) ? path.ToString() : null;
    }

    /// <summary>Whether the foreground app covers its full monitor, excluding the desktop and shell.</summary>
    public static bool IsFullscreen()
    {
        var window = GetForegroundWindow();
        if (window == 0 || window == GetDesktopWindow() || window == GetShellWindow() || IsIconic(window)) return false;
        if (!GetWindowRect(window, out var bounds)) return false;
        var monitor = MonitorFromWindow(window, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return false;
        return bounds.Left == info.Monitor.Left && bounds.Top == info.Monitor.Top
            && bounds.Right == info.Monitor.Right && bounds.Bottom == info.Monitor.Bottom;
    }

    /// <summary>Idle time for this session; throws when Windows cannot supply input information.</summary>
    public static TimeSpan IdleTime
    {
        get
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Unsigned subtraction handles the native 32-bit tick counter wrapping.
            return TimeSpan.FromMilliseconds(unchecked(GetTickCount() - info.Time));
        }
    }

    /// <summary>True if any app registered with Windows media controls reports Playing.</summary>
    /// <remarks>Apps without a Windows media session cannot be detected. Access failures propagate.</remarks>
    public static async Task<bool> IsMediaPlayingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken).ConfigureAwait(false);
        foreach (var session in manager.GetSessions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                return true;
        }
        return false;
    }

    /// <summary>True when the current session is locked, false when unlocked, null when unknown.</summary>
    public static bool? IsSessionLocked()
    {
        // SessionFlags distinguish an actual lock from UAC's temporary secure desktop.
        if (!WTSQuerySessionInformation(0, -1, 25, out var buffer, out var bytes)) return null;
        try
        {
            if (buffer == 0 || bytes < 20) return null;
            var info = Marshal.PtrToStructure<WtsInfoExPrefix>(buffer);
            if (info.Level != 1) return null;
            return info.SessionFlags switch { 0 => true, 1 => false, _ => null };
        }
        finally { if (buffer != 0) WTSFreeMemory(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size, Time; }
    // WTSINFOEX's union is 8-byte aligned because WTSINFOEX_LEVEL1 contains LARGE_INTEGER fields.
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct WtsInfoExPrefix
    {
        [FieldOffset(0)] public uint Level;
        [FieldOffset(8)] public uint SessionId;
        [FieldOffset(12)] public int SessionState;
        [FieldOffset(16)] public int SessionFlags;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQuerySessionInformation(nint server, int sessionId, int infoClass, out nint buffer, out uint bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint buffer);
}
