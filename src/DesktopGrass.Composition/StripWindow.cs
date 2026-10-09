// StripWindow.cs - the click-through, topmost, never-activated host window for one monitor.
using System;
using System.Windows.Forms;
using DesktopGrass.Composition.Interop;

namespace DesktopGrass.Composition;

internal sealed class StripWindow : NativeWindow, IDisposable
{
    public StripWindow(int x, int y, int width, int height)
    {
        var cp = new CreateParams
        {
            Caption = "DesktopGrass.Composition",
            Style = Win32.WS_POPUP,
            ExStyle = Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOPMOST |
                      Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_NOREDIRECTIONBITMAP,
            X = x,
            Y = y,
            Width = width,
            Height = height,
        };
        CreateHandle(cp);
        // A layered window stays invisible until its layering is configured. Opaque alpha keeps
        // composition's per-pixel alpha intact while making the whole window click-through.
        Win32.SetLayeredWindowAttributes(Handle, 0, 255, Win32.LWA_ALPHA);
    }

    public bool Visible { get; private set; }

    /// <summary>Raised when any strip's monitor changes DPI (the app rebuilds all strips).</summary>
    public static event Action? DpiChanged;

    public void Show()
    {
        Win32.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);
        Visible = true;
    }

    public void Hide()
    {
        Win32.ShowWindow(Handle, Win32.SW_HIDE);
        Visible = false;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Win32.WM_NCHITTEST:
                m.Result = (IntPtr)Win32.HTTRANSPARENT;
                return;
            case Win32.WM_MOUSEACTIVATE:
                m.Result = (IntPtr)Win32.MA_NOACTIVATE;
                return;
            case Win32.WM_DPICHANGED:
                DpiChanged?.Invoke();
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
