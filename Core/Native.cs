using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Ripple.Core;

/// <summary>The handful of Windows calls Ripple needs.</summary>
internal static class Native
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_DISABLED = 0x08000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const uint LWA_ALPHA = 0x2;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_APP = 0x8000;
    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint WM_WTSSESSION_CHANGE = 0x02B1;

    public const int HTTRANSPARENT = -1;
    public const int WH_MOUSE_LL = 14;
    public const uint SMTO_NORMAL = 0;
    public const uint SPI_GETDESKWALLPAPER = 0x0073;
    public const uint SPI_SETDESKWALLPAPER = 0x0014;
    public const int COLOR_DESKTOP = 1;

    public const uint RDW_INVALIDATE = 0x0001;
    public const uint RDW_ERASE = 0x0004;
    public const uint RDW_ALLCHILDREN = 0x0080;
    public const uint RDW_UPDATENOW = 0x0100;

    public const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    /// <summary>Only the two fields Ripple reads; the offsets are Windows' own.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 220, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [FieldOffset(68)] public short dmSize;
        [FieldOffset(184)] public int dmDisplayFrequency;
    }

    public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(uint exStyle, string cls, string? title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] public static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE devMode);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern uint GetSysColor(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, StringBuilder? value, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    public static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }
}
