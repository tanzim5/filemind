using System.Runtime.InteropServices;

namespace FileMind;

/// <summary>The Win32 bits a WinUI window doesn't offer: a system-wide hotkey and a notification-area (tray) icon.
/// Both are attached to one window that lives as long as the app (the quick-search window, which is only hidden).</summary>
public sealed class NativeHost : IDisposable
{
    private const int WM_HOTKEY = 0x0312, WM_APP_TRAY = 0x8000 + 1, WM_COMMAND = 0x0111;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    private const int HotkeyId = 0xF11E;
    private const uint MenuOpen = 1, MenuSearch = 2, MenuQuit = 3;

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc;     // kept alive: the native side holds a pointer to it
    private bool _hotkey, _tray;

    public event Action? HotkeyPressed;
    public event Action? OpenRequested;
    public event Action? QuitRequested;

    public NativeHost(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = WndProc;
        SetWindowSubclass(_hwnd, _proc, 1, IntPtr.Zero);
    }

    /// <summary>Registers e.g. "Ctrl+Alt+Space". Returns false when another app already owns that combination.</summary>
    public bool RegisterHotkey(string combo)
    {
        if (_hotkey) { UnregisterHotKey(_hwnd, HotkeyId); _hotkey = false; }
        if (!TryParse(combo, out var mods, out var vk)) return false;
        _hotkey = RegisterHotKey(_hwnd, HotkeyId, mods | MOD_NOREPEAT, vk);
        return _hotkey;
    }

    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                case "space": vk = 0x20; break;
                case var k when k.Length == 1 && char.IsLetterOrDigit(k[0]): vk = char.ToUpperInvariant(k[0]); break;
                case var k when k.StartsWith('f') && int.TryParse(k[1..], out var n) && n is >= 1 and <= 24: vk = (uint)(0x6F + n); break;
                default: return false;
            }
        }
        return vk != 0 && mods != 0;
    }

    public void ShowTrayIcon(string iconPath, string tip)
    {
        var data = TrayData();
        data.uFlags = 0x1 | 0x2 | 0x4;      // NIF_MESSAGE | NIF_ICON | NIF_TIP
        data.uCallbackMessage = WM_APP_TRAY;
        data.hIcon = LoadImage(IntPtr.Zero, iconPath, 1, 16, 16, 0x10);
        data.szTip = tip;
        _tray = Shell_NotifyIcon(0 /* NIM_ADD */, ref data);
    }

    private NOTIFYICONDATA TrayData() => new() { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1 };

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
    {
        if (msg == WM_HOTKEY && (int)wParam == HotkeyId) { HotkeyPressed?.Invoke(); return IntPtr.Zero; }
        if (msg == WM_APP_TRAY)
        {
            var ev = (int)(lParam.ToInt64() & 0xFFFF);
            if (ev == WM_LBUTTONUP) OpenRequested?.Invoke();
            else if (ev is WM_RBUTTONUP or WM_CONTEXTMENU) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == WM_COMMAND)
        {
            switch ((uint)(wParam.ToInt64() & 0xFFFF))
            {
                case MenuOpen: OpenRequested?.Invoke(); return IntPtr.Zero;
                case MenuSearch: HotkeyPressed?.Invoke(); return IntPtr.Zero;
                case MenuQuit: QuitRequested?.Invoke(); return IntPtr.Zero;
            }
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, MenuOpen, "Open File Mind");
        AppendMenu(menu, 0, MenuSearch, $"Quick search\t{App.ActiveHotkey}");
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenu(menu, 0, MenuQuit, "Quit File Mind");
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);                 // so the menu closes when you click elsewhere
        TrackPopupMenu(menu, 0, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
    }

    public void Dispose()
    {
        if (_hotkey) UnregisterHotKey(_hwnd, HotkeyId);
        if (_tray) { var d = TrayData(); Shell_NotifyIcon(2 /* NIM_DELETE */, ref d); }
        _hotkey = _tray = false;
        RemoveWindowSubclass(_hwnd, _proc, 1);
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);
    [DllImport("user32.dll")] private static extern bool TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int r, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
