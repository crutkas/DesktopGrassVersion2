// AppWindow.cs - hidden top-level window. Receives display / work-area / session / power broadcasts,
// fullscreen-app notifications (as an appbar that reserves no space) and the mouse hook's wake-ups.
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DesktopGrass.Composition.Interop;

namespace DesktopGrass.Composition;

internal sealed class AppWindow : NativeWindow, IDisposable
{
    public const int WM_MOUSE_READY = Win32.WM_APP + 1;
    private const int WM_APPBAR_NOTIFY = Win32.WM_APP + 2;

    private readonly IntPtr _powerNotify;
    private readonly bool _appBar;

    public event Action? MouseReady;
    public event Action<string>? DisplayChanged;
    public event Action<bool>? FullscreenChanged;
    public event Action<bool>? SessionLockChanged;
    public event Action<bool>? BatterySaverChanged;
    public event Action? AnimationSettingChanged;

    public AppWindow()
    {
        // Never shown; a top-level (not message-only) window so broadcasts reach it.
        CreateHandle(new CreateParams { Caption = "DesktopGrass.Composition.App", Style = Win32.WS_POPUP, ExStyle = Win32.WS_EX_TOOLWINDOW });
        Win32.WTSRegisterSessionNotification(Handle, Win32.NOTIFY_FOR_THIS_SESSION);
        _powerNotify = Win32.RegisterPowerSettingNotification(Handle, Win32.GUID_POWER_SAVING_STATUS, 0);
        var abd = new Win32.APPBARDATA { cbSize = Marshal.SizeOf<Win32.APPBARDATA>(), hWnd = Handle, uCallbackMessage = WM_APPBAR_NOTIFY };
        _appBar = Win32.SHAppBarMessage(Win32.ABM_NEW, ref abd) != UIntPtr.Zero;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSE_READY:
                MouseReady?.Invoke();
                return;
            case WM_APPBAR_NOTIFY:
                if ((int)m.WParam == Win32.ABN_FULLSCREENAPP) FullscreenChanged?.Invoke(m.LParam != IntPtr.Zero);
                return;
            case Win32.WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke("display change");
                break;
            case Win32.WM_DPICHANGED:
                DisplayChanged?.Invoke("dpi change");
                break;
            case Win32.WM_SETTINGCHANGE:
                if ((int)m.WParam == Win32.SPI_SETWORKAREA) DisplayChanged?.Invoke("work area change");
                else if ((int)m.WParam == Win32.SPI_SETCLIENTAREAANIMATION) AnimationSettingChanged?.Invoke();
                break;
            case Win32.WM_WTSSESSION_CHANGE:
                if ((int)m.WParam == Win32.WTS_SESSION_LOCK) SessionLockChanged?.Invoke(true);
                else if ((int)m.WParam == Win32.WTS_SESSION_UNLOCK) SessionLockChanged?.Invoke(false);
                break;
            case Win32.WM_POWERBROADCAST:
                if ((int)m.WParam == Win32.PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
                {
                    var s = Marshal.PtrToStructure<Win32.POWERBROADCAST_SETTING>(m.LParam);
                    if (s.PowerSetting == Win32.GUID_POWER_SAVING_STATUS) BatterySaverChanged?.Invoke(s.Data != 0);
                }
                m.Result = (IntPtr)1;
                return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        if (_appBar)
        {
            var abd = new Win32.APPBARDATA { cbSize = Marshal.SizeOf<Win32.APPBARDATA>(), hWnd = Handle };
            Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref abd);
        }
        Win32.WTSUnRegisterSessionNotification(Handle);
        if (_powerNotify != IntPtr.Zero) Win32.UnregisterPowerSettingNotification(_powerNotify);
        DestroyHandle();
    }
}
