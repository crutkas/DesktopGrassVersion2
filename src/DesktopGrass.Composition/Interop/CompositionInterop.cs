// Interop/CompositionInterop.cs - bootstrapping Windows.UI.Composition in a Win32 process:
// a DispatcherQueue for the UI thread and a DesktopWindowTarget bound to an HWND.
using System;
using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace DesktopGrass.Composition.Interop;

internal static unsafe class CompositionInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;    // DQTYPE_THREAD_CURRENT = 2
        public int apartmentType; // DQTAT_COM_NONE = 0
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    private static readonly Guid IID_ICompositorDesktopInterop = new("29E691FA-4567-4DCA-B319-D0F207EB6807");

    /// <summary>Creates a DispatcherQueue pumped by this thread's Win32 message loop.</summary>
    public static DispatcherQueueController CreateDispatcherQueueForCurrentThread()
    {
        var options = new DispatcherQueueOptions
        {
            dwSize = sizeof(DispatcherQueueOptions),
            threadType = 2,
            apartmentType = 0,
        };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out IntPtr ptr));
        try
        {
            return DispatcherQueueController.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    /// <summary>ICompositorDesktopInterop::CreateDesktopWindowTarget.</summary>
    public static DesktopWindowTarget CreateDesktopWindowTarget(Compositor compositor, IntPtr hwnd, bool isTopmost)
    {
        IntPtr unknown = MarshalInspectable<Compositor>.FromManaged(compositor);
        try
        {
            Guid iid = IID_ICompositorDesktopInterop;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out IntPtr interop));
            try
            {
                IntPtr target = IntPtr.Zero;
                var fn = (delegate* unmanaged<IntPtr, IntPtr, int, IntPtr*, int>)(*(IntPtr**)interop)[3];
                Marshal.ThrowExceptionForHR(fn(interop, hwnd, isTopmost ? 1 : 0, &target));
                try
                {
                    return DesktopWindowTarget.FromAbi(target);
                }
                finally
                {
                    Marshal.Release(target);
                }
            }
            finally
            {
                Marshal.Release(interop);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
