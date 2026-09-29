using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using TwinkleTray.Core;

namespace TwinkleTray.WinUI.Services;

// A native Shell_NotifyIcon window also owns global hotkeys and power/display messages.
internal sealed class TrayService : IDisposable
{
    private const uint Callback = 0x8001;
    private const uint ScrollCallback = 0x8002;
    private readonly WindowProc _proc;
    private readonly MouseHookProc _mouseProc;
    private readonly nint _instance;
    private readonly string _className = "TwinkleTray.WinUI.Tray." + Guid.NewGuid().ToString("N");
    private readonly uint _taskbarCreated;
    private readonly Dictionary<int, HotkeyBinding> _hotkeys = new();
    private HotkeyBinding[] _nativeHotkeys = [];
    private bool _rawInputRegistered;
    private string? _heldBrightnessKey;
    private nint _heldBrightnessDevice;
    private nint _window, _icon, _mouseHook, _lidNotification;
    private bool _disposed, _scrollEnabled, _modernCallbacks;
    private bool _themeNotifications = true, _powerNotifications = true;
    private int _wheelRemainder;
    private string _iconStyle = "fluent";
    private AppProfile[] _profiles = [];
    public bool AutomationPaused { get; set; }
    public event Action? Clicked, SettingsRequested, RefreshRequested, ExitRequested, DisplaysChanged, PauseRequested, Resumed, PowerRequested;
    public event Action<HotkeyBinding>? HotkeyPressed;
    public event Action<int>? Scrolled;
    public event Action<AppProfile>? ProfileRequested;
    public event Action<bool>? LidChanged;

    public TrayService()
    {
        _proc = WndProc;
        _mouseProc = MouseHook;
        _instance = GetModuleHandle(null);
        var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Proc = _proc, Instance = _instance, ClassName = _className };
        if (RegisterClassEx(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _window = CreateWindowEx(0, _className, "Twinkle Tray Native message window", 0, 0, 0, 0, 0, 0, 0, _instance, 0);
        if (_window == 0)
        {
            var error = Marshal.GetLastWin32Error();
            UnregisterClass(_className, _instance);
            throw new Win32Exception(error);
        }
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        try { UpdateIcon(); AddIcon(); }
        catch { Dispose(); throw; }
    }

    public void Configure(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _profiles = settings.Profiles.Where(profile => profile.ShowInTray).ToArray();
        _iconStyle = settings.TrayIcon is "icon" or "mdl2" ? settings.TrayIcon : "fluent";
        _themeNotifications = settings.ThemeNotifications;
        _powerNotifications = settings.PowerNotifications;
        if (_powerNotifications && _lidNotification == 0)
        {
            var lid = new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");
            _lidNotification = RegisterPowerSettingNotification(_window, ref lid, 0);
            if (_lidNotification == 0) Program.Log(new Win32Exception(Marshal.GetLastWin32Error(), "Lid state notifications are unavailable."));
        }
        else if (!_powerNotifications && _lidNotification != 0)
        {
            UnregisterPowerSettingNotification(_lidNotification); _lidNotification = 0;
        }
        UpdateIcon();
        _scrollEnabled = settings.TrayScrollEnabled;
        if (_scrollEnabled && _mouseHook == 0)
        {
            _mouseHook = SetWindowsHookEx(14, _mouseProc, _instance, 0); // WH_MOUSE_LL
            if (_mouseHook == 0) { _scrollEnabled = false; throw new Win32Exception(Marshal.GetLastWin32Error(), "Tray wheel control could not be registered."); }
        }
        else if (!_scrollEnabled && _mouseHook != 0)
        {
            UnhookWindowsHookEx(_mouseHook); _mouseHook = 0; _wheelRemainder = 0;
        }
    }

    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1,
        Flags = 1 | 2 | 4 | 0x80, CallbackMessage = Callback, Icon = _icon,
        Tip = "Twinkle Tray Native", Info = "", InfoTitle = ""
    };

    private void AddIcon()
    {
        if (_disposed) return;
        var data = Data();
        if (Shell_NotifyIcon(0, ref data))
        {
            data.Version = 4; _modernCallbacks = Shell_NotifyIcon(4, ref data);
            KillTimer(_window, 1);
        }
        else SetTimer(_window, 1, 1000, 0); // Explorer may not be ready during sign-in.
    }

    public void UpdateIcon()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool light = key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "tray-icons", light ? "light" : "dark", _iconStyle + ".ico");
        var next = LoadImage(0, path, 1, 0, 0, 0x10 | 0x40);
        if (next == 0) next = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", light ? "tray-light.ico" : "tray-dark.ico"), 1, 0, 0, 0x10 | 0x40);
        if (next == 0) next = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico"), 1, 0, 0, 0x10 | 0x40);
        if (next == 0) throw new FileNotFoundException("The tray icon assets could not be loaded.");
        var previous = _icon;
        _icon = next;
        var data = Data(); Shell_NotifyIcon(1, ref data);
        if (previous != 0) DestroyIcon(previous);
    }

    public void Notify(string title, string message)
    {
        if (_disposed) return;
        var data = Data(); data.Flags = 0x10;
        data.InfoTitle = title.Length > 63 ? title[..63] : title;
        data.Info = message.Length > 255 ? message[..255] : message;
        data.InfoFlags = 1;
        Shell_NotifyIcon(1, ref data);
    }

    public IReadOnlyList<string> RegisterHotkeys(IEnumerable<HotkeyBinding> bindings)
    {
        StopBrightnessRepeat();
        var enabled = bindings.Where(x => x.Enabled).ToArray();
        foreach (int id in _hotkeys.Keys) UnregisterHotKey(_window, id);
        _hotkeys.Clear();
        var errors = new List<string>();
        _nativeHotkeys = enabled.Where(binding => binding.NativeKey is "BrightnessUp" or "BrightnessDown").ToArray();
        if (_nativeHotkeys.Length > 0 && !_rawInputRegistered)
        {
            var device = new RawInputDevice { UsagePage = 0x0C, Usage = 1, Flags = 0x100 | 0x2000, Target = _window };
            _rawInputRegistered = RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
            if (!_rawInputRegistered) errors.Add("The native brightness keys could not be registered (Windows error " + Marshal.GetLastWin32Error() + ").");
        }
        else if (_nativeHotkeys.Length == 0) UnregisterBrightnessKeys();
        int nextId = 1;
        foreach (var binding in enabled.Where(binding => string.IsNullOrEmpty(binding.NativeKey)))
        {
            int id = nextId++;
            if (RegisterHotKey(_window, id, binding.Modifiers | 0x4000, binding.VirtualKey)) _hotkeys[id] = binding;
            else errors.Add($"Hotkey 0x{binding.VirtualKey:X2} could not be registered (already used by another app).");
        }
        return errors;
    }

    private nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (_disposed) return DefWindowProc(hwnd, message, wParam, lParam);
            if (message == _taskbarCreated && _taskbarCreated != 0) { AddIcon(); return 0; }
            if (message == 0x113 && wParam == 1) { AddIcon(); return 0; }
            if (message == 0x113 && wParam == 2)
            {
                if (_heldBrightnessKey is { } key)
                {
                    SetTimer(_window, 2, 100, 0);
                    RaiseBrightnessKey(key);
                }
                return 0;
            }
            if (message == 0xFE && wParam == 2 && lParam == _heldBrightnessDevice) StopBrightnessRepeat(); // GIDC_REMOVAL
            if (message == 0xFF && _rawInputRegistered)
            {
                ProcessBrightnessInput(lParam);
                // Foreground WM_INPUT still needs DefWindowProc for raw-input cleanup.
                return DefWindowProc(hwnd, message, wParam, lParam);
            }
            if (message == Callback)
            {
                var notification = (uint)(lParam.ToInt64() & 0xffff);
                if (_modernCallbacks)
                {
                    if (((ulong)lParam >> 16 & 0xffff) != 1) return 0;
                    if (notification is 0x400 or 0x401) Clicked?.Invoke();
                    else if (notification == 0x405) SettingsRequested?.Invoke();
                    else if (notification == 0x7B) ShowMenu(new Point { X = unchecked((short)(wParam & 0xffff)), Y = unchecked((short)(wParam >> 16 & 0xffff)) });
                }
                else
                {
                    if (wParam != 1) return 0;
                    if (notification == 0x202) Clicked?.Invoke();
                    else if (notification == 0x205) ShowMenu(null);
                }
                return 0;
            }
            if (message == ScrollCallback)
            {
                if (!_scrollEnabled) return 0;
                _wheelRemainder += unchecked((short)wParam);
                int steps = _wheelRemainder / 120;
                _wheelRemainder %= 120;
                if (steps != 0) Scrolled?.Invoke(steps);
                return 0;
            }
            if (message == 0x312 && _hotkeys.TryGetValue((int)wParam, out var binding)) { HotkeyPressed?.Invoke(binding); return 0; }
            if (message is 0x7E or 0x219) DisplaysChanged?.Invoke();
            if (message == 0x218 && wParam == 4) StopBrightnessRepeat();
            if (message == 0x218 && _powerNotifications)
            {
                if (wParam is 7 or 18) { Resumed?.Invoke(); return 1; }
                if (wParam == 0x8013 && lParam != 0)
                {
                    var setting = Marshal.PtrToStructure<PowerSetting>(lParam);
                    if (setting.Guid == new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3") && setting.Length >= 4)
                    {
                        var state = Marshal.ReadInt32(lParam, 20);
                        if (state is 0 or 1) LidChanged?.Invoke(state == 0); // true means lid closed.
                    }
                    return 1;
                }
            }
            if (message == 0x1A && _themeNotifications) UpdateIcon();
        }
        catch (Exception exception) { Program.Log(exception); } // Never unwind across a native window-procedure boundary.
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu(Point? anchor)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, 1, LocalizationService.Get("PANEL_TITLE", "Adjust Brightness"));
            AppendMenu(menu, 0, 2, LocalizationService.Get("GENERIC_SETTINGS", "Settings"));
            AppendMenu(menu, 0, 3, LocalizationService.Get("GENERIC_REFRESH_DISPLAYS", "Refresh displays"));
            if (PowerRequested is not null) AppendMenu(menu, 0, 6, LocalizationService.Get("PANEL_BUTTON_TURN_OFF_DISPLAYS", "Turn off displays"));
            AppendMenu(menu, AutomationPaused ? 8u : 0u, 5, LocalizationService.Get(AutomationPaused ? "NATIVE_RESUME_AUTOMATION" : "NATIVE_PAUSE_AUTOMATION", AutomationPaused ? "Resume automatic adjustments" : "Pause automatic adjustments"));
            var profiles = _profiles.ToArray();
            if (profiles.Length > 0)
            {
                AppendMenu(menu, 0x800, 0, null);
                for (var i = 0; i < profiles.Length; i++)
                    AppendMenu(menu, 0, (nuint)(100 + i), (string.IsNullOrWhiteSpace(profiles[i].Name) ? "Profile " + (i + 1) : profiles[i].Name).Replace("&", "&&"));
            }
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 4, LocalizationService.Get("GENERIC_QUIT", "Quit"));
            Point point;
            if (anchor is { X: not -1, Y: not -1 } supplied) point = supplied;
            else if (TryGetIconRect(out var rect)) point = new Point { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
            else GetCursorPos(out point);
            SetForegroundWindow(_window);
            uint command = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, _window, 0);
            switch (command)
            {
                case 1: Clicked?.Invoke(); break;
                case 2: SettingsRequested?.Invoke(); break;
                case 3: RefreshRequested?.Invoke(); break;
                case 4: ExitRequested?.Invoke(); break;
                case 5: PauseRequested?.Invoke(); break;
                case 6: PowerRequested?.Invoke(); break;
                default:
                    if (command >= 100 && command - 100 < profiles.Length) ProfileRequested?.Invoke(profiles[command - 100]);
                    break;
            }
            if (!_disposed)
            {
                PostMessage(_window, 0, 0, 0);
                var data = Data(); Shell_NotifyIcon(3, ref data);
            }
        }
        finally { DestroyMenu(menu); }
    }

    private nint MouseHook(int code, nuint wParam, nint lParam)
    {
        // WH_MOUSE_LL executes on the installing UI thread. Only wheel events over
        // this exact visible notification icon are consumed; all other input passes on.
        if (code >= 0 && wParam == 0x20A && _scrollEnabled && !_disposed)
        {
            try
            {
                var mouse = Marshal.PtrToStructure<MouseData>(lParam);
                if (TryGetIconRect(out var rect) && mouse.Point.X >= rect.Left && mouse.Point.X < rect.Right &&
                    mouse.Point.Y >= rect.Top && mouse.Point.Y < rect.Bottom && IsVisibleTaskbarAt(mouse.Point))
                {
                    var delta = unchecked((short)(mouse.Data >> 16));
                    if (delta != 0 && PostMessage(_window, ScrollCallback, unchecked((nuint)(ushort)delta), 0)) return 1;
                }
            }
            catch (Exception) { } // Input hooks must be short and must never swallow input on failure.
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void ProcessBrightnessInput(nint input)
    {
        // Only Consumer Control brightness usages are interpreted. Keyboard input,
        // other consumer controls and report bytes are never retained or logged.
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint size = 0;
        if (GetRawInputData(input, 0x10000003, 0, ref size, headerSize) == uint.MaxValue || size < headerSize + 8 || size > 65536) return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var allocated = size;
            if (GetRawInputData(input, 0x10000003, buffer, ref size, headerSize) == uint.MaxValue || size > allocated || size < headerSize + 8) return;
            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != 2 || header.Device == 0) return;
            uint reportSize = unchecked((uint)Marshal.ReadInt32(buffer, (int)headerSize));
            uint count = unchecked((uint)Marshal.ReadInt32(buffer, (int)headerSize + 4));
            if (reportSize == 0 || count == 0 || (ulong)reportSize * count > size - headerSize - 8) return;
            uint preparsedSize = 0;
            if (GetRawInputDeviceInfo(header.Device, 0x20000005, 0, ref preparsedSize) == uint.MaxValue || preparsedSize == 0 || preparsedSize > 65536) return;
            var preparsed = Marshal.AllocHGlobal((int)preparsedSize);
            var caps = Marshal.AllocHGlobal(64); // HIDP_CAPS consists of 32 USHORTs.
            try
            {
                allocated = preparsedSize;
                if (GetRawInputDeviceInfo(header.Device, 0x20000005, preparsed, ref preparsedSize) == uint.MaxValue || preparsedSize > allocated ||
                    HidP_GetCaps(preparsed, caps) != 0x110000 || Marshal.ReadInt16(caps) != 1 || Marshal.ReadInt16(caps, 2) != 0x0C) return;
                var maximum = HidP_MaxUsageListLength(0, 0x0C, preparsed);
                if (maximum == 0 || maximum > 4096) return;
                var usages = new ushort[maximum];
                string? pressed = null;
                bool validReport = false;
                for (uint index = 0; index < count; index++)
                {
                    var report = buffer + (int)(headerSize + 8 + index * reportSize);
                    uint usageCount = maximum;
                    if (HidP_GetUsages(0, 0x0C, 0, usages, ref usageCount, preparsed, report, reportSize) != 0x110000 || usageCount > maximum) continue;
                    validReport = true;
                    for (var usage = 0; usage < usageCount; usage++)
                    {
                        if (usages[usage] == 0x6F) { pressed = "BrightnessUp"; break; }
                        if (usages[usage] == 0x70) { pressed = "BrightnessDown"; break; }
                    }
                    if (pressed is not null) break;
                }
                if (!validReport) return;
                if (pressed is null)
                {
                    if (header.Device == _heldBrightnessDevice) StopBrightnessRepeat();
                    return;
                }
                if (pressed == _heldBrightnessKey && header.Device == _heldBrightnessDevice) return;
                StopBrightnessRepeat();
                if (!_nativeHotkeys.Any(binding => binding.NativeKey == pressed)) return;
                _heldBrightnessKey = pressed; _heldBrightnessDevice = header.Device;
                SetTimer(_window, 2, 400, 0);
                RaiseBrightnessKey(pressed);
            }
            finally { Marshal.FreeHGlobal(caps); Marshal.FreeHGlobal(preparsed); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void RaiseBrightnessKey(string key)
    {
        foreach (var binding in _nativeHotkeys.Where(binding => binding.NativeKey == key).ToArray()) HotkeyPressed?.Invoke(binding);
    }

    private void StopBrightnessRepeat()
    {
        KillTimer(_window, 2);
        _heldBrightnessKey = null; _heldBrightnessDevice = 0;
    }

    private void UnregisterBrightnessKeys()
    {
        StopBrightnessRepeat();
        if (!_rawInputRegistered) return;
        var device = new RawInputDevice { UsagePage = 0x0C, Usage = 1, Flags = 1, Target = 0 };
        RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        _rawInputRegistered = false;
    }

    private bool TryGetIconRect(out Rect rect)
    {
        var identifier = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = _window, Id = 1 };
        return Shell_NotifyIconGetRect(ref identifier, out rect) == 0 && rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    private static bool IsVisibleTaskbarAt(Point point)
    {
        var window = GetAncestor(WindowFromPoint(point), 2); // GA_ROOT
        if (window == 0) return false;
        var name = new StringBuilder(128);
        if (GetClassName(window, name, name.Capacity) == 0) return false;
        return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
    }

    public static TimeSpan IdleTime
    {
        get
        {
            var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
            return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
        }
    }

    /// <summary>Requests Windows monitor power-off. Call only for an explicit power action.</summary>
    public static void TurnOffAllDisplays()
    {
        // HWND_BROADCAST / WM_SYSCOMMAND / SC_MONITORPOWER / power-off (2).
        if (SendMessageTimeout((nint)0xffff, 0x112, 0xF170, 2, 2, 1000, out _) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not complete the display power-off request.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scrollEnabled = false;
        UnregisterBrightnessKeys();
        if (_mouseHook != 0) { UnhookWindowsHookEx(_mouseHook); _mouseHook = 0; }
        if (_lidNotification != 0) { UnregisterPowerSettingNotification(_lidNotification); _lidNotification = 0; }
        KillTimer(_window, 1);
        foreach (int id in _hotkeys.Keys) UnregisterHotKey(_window, id);
        var data = Data(); Shell_NotifyIcon(2, ref data);
        DestroyWindow(_window); _window = 0;
        UnregisterClass(_className, _instance);
        if (_icon != 0) DestroyIcon(_icon);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint MouseHookProc(int code, nuint wParam, nint lParam);
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
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public Point Point; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NotifyIconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct PowerSetting { public Guid Guid; public uint Length; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInputHeader { public uint Type, Size; public nint Device; public nuint WParam; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetRawInputDeviceInfo(nint device, uint command, nint data, ref uint size);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(nint preparsed, nint capabilities);
    [DllImport("hid.dll")] private static extern uint HidP_MaxUsageListLength(int type, ushort page, nint preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetUsages(int type, ushort page, ushort collection, [Out] ushort[] usages, ref uint count, nint preparsed, nint report, uint length);
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
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int hook, MouseHookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint timerProc);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
}
