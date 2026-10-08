// Interop/Win32.cs - the small slice of user32/shell32/wtsapi32 we need.
using System;
using System.Runtime.InteropServices;

namespace DesktopGrass.Composition.Interop;

internal static unsafe class Win32
{
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    public const int WM_QUIT = 0x0012;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_GETOBJECT = 0x003D;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_POWERBROADCAST = 0x0218;
    public const int WM_WTSSESSION_CHANGE = 0x02B1;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_APP = 0x8000;

    public const int HTTRANSPARENT = -1;
    public const int MA_NOACTIVATE = 3;
    public const int SPI_SETWORKAREA = 0x002F;
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    public const int SPI_SETCLIENTAREAANIMATION = 0x1043;
    public const int WTS_SESSION_LOCK = 0x7;
    public const int WTS_SESSION_UNLOCK = 0x8;
    public const int NOTIFY_FOR_THIS_SESSION = 0;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;
    public const int WH_MOUSE_LL = 14;

    public const uint ABM_NEW = 0x0;
    public const uint ABM_REMOVE = 0x1;
    public const int ABN_FULLSCREENAPP = 0x2;

    public const uint MONITOR_DEFAULTTONULL = 0;
    public const int MDT_EFFECTIVE_DPI = 0;

    public static readonly Guid GUID_POWER_SAVING_STATUS = new("E00958C0-C213-4ACE-AC77-FECCED2EEEA5");

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
        public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data;
    }

    public delegate int MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr data);

    [DllImport("user32.dll")]
    public static extern int EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    public static extern int GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern int ShowWindow(IntPtr hwnd, int cmd);

    public const int LWA_ALPHA = 0x2;

    [DllImport("user32.dll")]
    public static extern int SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, int flags);

    [DllImport("user32.dll")]
    public static extern int GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    public static extern int GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hwnd, char* buffer, int max);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    public static extern int PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    public static extern int PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    public static extern IntPtr SetWindowsHookEx(int idHook, IntPtr proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    public static extern int UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern int TranslateMessage(in MSG msg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static extern IntPtr DispatchMessage(in MSG msg);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? name);

    [DllImport("shell32.dll")]
    public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

    [DllImport("wtsapi32.dll")]
    public static extern int WTSRegisterSessionNotification(IntPtr hwnd, int flags);

    [DllImport("wtsapi32.dll")]
    public static extern int WTSUnRegisterSessionNotification(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    public static extern int SystemParametersInfo(uint action, uint param, out int value, uint winIni);

    /// <summary>Windows "Animation effects" setting (Settings > Accessibility > Visual effects).</summary>
    public static bool ClientAreaAnimationEnabled() =>
        SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out int on, 0) == 0 || on != 0;

    public static string ClassName(IntPtr hwnd)
    {
        char* buffer = stackalloc char[128];
        int n = GetClassName(hwnd, buffer, 128);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    [DllImport("user32.dll")]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, in Guid setting, int flags);

    [DllImport("user32.dll")]
    public static extern int UnregisterPowerSettingNotification(IntPtr handle);
}
