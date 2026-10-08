// Strip.cs - one monitor's overlay: window + composition target + layered visual tree.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using DesktopGrass.Composition.Interop;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace DesktopGrass.Composition;

internal readonly record struct MonitorInfo(IntPtr Handle, Win32.RECT Bounds, Win32.RECT Work, uint Dpi, bool Primary);

internal static class Monitors
{
    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (Win32.GetMonitorInfo(h, ref mi) != 0)
            {
                uint dpi = Win32.GetDpiForMonitor(h, Win32.MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 ? dx : 96;
                list.Add(new MonitorInfo(h, mi.rcMonitor, mi.rcWork, dpi, (mi.dwFlags & 1) != 0));
            }
            return 1;
        }, IntPtr.Zero);
        list.Sort((a, b) => a.Primary == b.Primary ? a.Bounds.Left.CompareTo(b.Bounds.Left) : a.Primary ? -1 : 1);
        return list;
    }
}

internal sealed class Strip : IDisposable
{
    /// <summary>Strip height in DIPs: 30 DIP of grass plus room for critters, flyers and birds.</summary>
    public const float HeightDip = 150f;

    public readonly MonitorInfo Monitor;
    public readonly float Scale;
    public readonly float Width;
    public readonly float Height = HeightDip;
    public readonly int PxLeft, PxTop, PxWidth, PxHeight;
    public readonly StripWindow Window;
    public readonly DesktopWindowTarget Target;
    public readonly ContainerVisual Root;
    public readonly ContainerVisual BackDecor, BackGrass, Critters, FrontGrass, Sky, Fx, Labels;

    /// <summary>Set while a fullscreen app covers this monitor.</summary>
    public bool CoveredByFullscreen;

    public float GroundY => Height;

    public Strip(Compositor compositor, MonitorInfo monitor)
    {
        Monitor = monitor;
        Scale = monitor.Dpi / 96f;
        PxHeight = (int)MathF.Ceiling(HeightDip * Scale);
        PxLeft = monitor.Work.Left;
        PxWidth = monitor.Work.Width;
        PxTop = monitor.Work.Bottom - PxHeight;
        Width = PxWidth / Scale;

        Window = new StripWindow(PxLeft, PxTop, PxWidth, PxHeight);
        Target = CompositionInterop.CreateDesktopWindowTarget(compositor, Window.Handle, isTopmost: true);

        Root = compositor.CreateContainerVisual();
        Root.Size = new Vector2(Width, Height);
        Root.Scale = new Vector3(Scale, Scale, 1);
        Target.Root = Root;

        BackDecor = Layer(compositor);
        BackGrass = Layer(compositor);
        Critters = Layer(compositor);
        FrontGrass = Layer(compositor);
        Sky = Layer(compositor);
        Fx = Layer(compositor);
        Labels = Layer(compositor);
    }

    private ContainerVisual Layer(Compositor compositor)
    {
        var v = compositor.CreateContainerVisual();
        v.Size = new Vector2(Width, Height);
        Root.Children.InsertAtTop(v);
        return v;
    }

    /// <summary>Physical screen pixels to strip DIPs (origin top-left of the strip).</summary>
    public Vector2 ToLocal(int screenX, int screenY) => new((screenX - PxLeft) / Scale, (screenY - PxTop) / Scale);

    public bool ContainsScreenX(int screenX) => screenX >= PxLeft && screenX < PxLeft + PxWidth;

    public void Show() => Window.Show();

    public void Hide() => Window.Hide();

    public void Dispose()
    {
        try { Target.Root = null; } catch { }
        // Closes the layers and anything still in them (e.g. particles in flight), shapes included.
        try { Shapes.DisposeTree(Root); } catch { }
        Target.Dispose();
        Window.Dispose();
    }
}
