using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TwinkleTray.Core;

namespace TwinkleTray.WinUI.Services;

// A native Shell_NotifyIcon window also owns global hotkeys and power/display messages.
internal sealed class TrayService : IDisposable
{
    private const uint Callback = 0x8001;
    private readonly WindowProc _proc;
    private readonly nint _instance;
    private readonly string _className = "TwinkleTray.WinUI.Tray." + Guid.NewGuid().ToString("N");
    private readonly uint _taskbarCreated;
    private readonly Dictionary<int, HotkeyBinding> _hotkeys = new();
    private nint _window, _icon;
    private bool _disposed;
    public event Action? Clicked, SettingsRequested, RefreshRequested, ExitRequested, DisplaysChanged;
    public event Action<HotkeyBinding>? HotkeyPressed;

    public TrayService()
    {
        _proc = WndProc;
        _instance = GetModuleHandle(null);
        var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Proc = _proc, Instance = _instance, ClassName = _className };
        if (RegisterClassEx(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _window = CreateWindowEx(0, _className, "Twinkle Tray WinUI message window", 0, 0, 0, 0, 0, 0, 0, _instance, 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        UpdateIcon();
        AddIcon();
    }

    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1,
        Flags = 1 | 2 | 4 | 0x80, CallbackMessage = Callback, Icon = _icon,
        Tip = "Twinkle Tray · WinUI 3", Info = "", InfoTitle = ""
    };

    private void AddIcon()
    {
        var data = Data();
        if (Shell_NotifyIcon(0, ref data)) { data.Version = 4; Shell_NotifyIcon(4, ref data); }
    }

    public void UpdateIcon()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool light = key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", light ? "tray-light.ico" : "tray-dark.ico");
        var next = LoadImage(0, path, 1, 0, 0, 0x10 | 0x40);
        if (next == 0) next = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico"), 1, 0, 0, 0x10 | 0x40);
        var previous = _icon;
        _icon = next;
        var data = Data(); Shell_NotifyIcon(1, ref data);
        if (previous != 0) DestroyIcon(previous);
    }

    public IReadOnlyList<string> RegisterHotkeys(IEnumerable<HotkeyBinding> bindings)
    {
        foreach (int id in _hotkeys.Keys) UnregisterHotKey(_window, id);
        _hotkeys.Clear();
        var errors = new List<string>();
        int nextId = 1;
        foreach (var binding in bindings.Where(x => x.Enabled))
        {
            int id = nextId++;
            if (RegisterHotKey(_window, id, binding.Modifiers | 0x4000, binding.VirtualKey)) _hotkeys[id] = binding;
            else errors.Add($"Hotkey 0x{binding.VirtualKey:X2} could not be registered (already used by another app).");
        }
        return errors;
    }

    private nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreated && _taskbarCreated != 0) AddIcon();
        if (message == Callback)
        {
            switch ((uint)(lParam.ToInt64() & 0xffff))
            {
                case 0x400: case 0x401: Clicked?.Invoke(); break;
                case 0x205: case 0x7B: ShowMenu(); break;
            }
            return 0;
        }
        if (message == 0x312 && _hotkeys.TryGetValue((int)wParam, out var binding)) { HotkeyPressed?.Invoke(binding); return 0; }
        if (message == 0x7E || message == 0x219 || (message == 0x218 && (wParam == 7 || wParam == 18))) DisplaysChanged?.Invoke();
        if (message == 0x1A) UpdateIcon();
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, 1, LocalizationService.Get("PANEL_TITLE", "Adjust Brightness"));
            AppendMenu(menu, 0, 2, LocalizationService.Get("GENERIC_SETTINGS", "Settings"));
            AppendMenu(menu, 0, 3, LocalizationService.Get("GENERIC_REFRESH_DISPLAYS", "Refresh displays"));
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 4, LocalizationService.Get("GENERIC_QUIT", "Quit"));
            GetCursorPos(out var point);
            SetForegroundWindow(_window);
            uint command = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, _window, 0);
            switch (command)
            {
                case 1: Clicked?.Invoke(); break;
                case 2: SettingsRequested?.Invoke(); break;
                case 3: RefreshRequested?.Invoke(); break;
                case 4: ExitRequested?.Invoke(); break;
            }
            PostMessage(_window, 0, 0, 0);
        }
        finally { DestroyMenu(menu); }
    }

    public static TimeSpan IdleTime
    {
        get
        {
            var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
            return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (int id in _hotkeys.Keys) UnregisterHotKey(_window, id);
        var data = Data(); Shell_NotifyIcon(2, ref data);
        DestroyWindow(_window); _window = 0;
        UnregisterClass(_className, _instance);
        if (_icon != 0) DestroyIcon(_icon);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public WindowProc Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background;
        public string? MenuName; public string ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid GuidItem; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint message, nuint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint operation, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int x, int y, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nuint w, nint l);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
}
