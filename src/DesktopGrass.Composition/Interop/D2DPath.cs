// Interop/D2DPath.cs - builds CompositionPath objects from Direct2D path geometries.
//
// Windows.UI.Composition only accepts paths through Windows.Graphics.IGeometrySource2D, an
// interface whose real contract lives on the COM side (IGeometrySource2DInterop::GetGeometry).
// Win2D normally supplies it, but Win2D isn't available for Windows.UI.Composition in a desktop
// app, so we hand-roll a tiny agile COM object that hands composition our ID2D1Geometry.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Graphics;
using Windows.UI.Composition;
using WinRT;

namespace DesktopGrass.Composition.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct D2DPoint
{
    public float X, Y;
    public D2DPoint(float x, float y) { X = x; Y = y; }
}

/// <summary>Fluent builder for a filled/stroked Direct2D path that becomes a CompositionPath.</summary>
internal sealed unsafe class PathBuilder
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BezierSegment { public D2DPoint P1, P2, P3; }

    [StructLayout(LayoutKind.Sequential)]
    private struct QuadSegment { public D2DPoint P1, P2; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ArcSegment
    {
        public D2DPoint Point;
        public float Width, Height, Rotation;
        public int Sweep;   // 0 = counter-clockwise, 1 = clockwise
        public int ArcSize; // 0 = small, 1 = large
    }

    private IntPtr _geometry; // ID2D1PathGeometry*
    private IntPtr _sink;     // ID2D1GeometrySink*
    private bool _figureOpen;
    private float _x, _y;

    public PathBuilder(bool winding = false)
    {
        IntPtr factory = D2D.Factory;
        IntPtr geometry;
        D2D.Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Vt(factory)[10])(factory, &geometry));
        _geometry = geometry;
        IntPtr sink;
        D2D.Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Vt(geometry)[17])(geometry, &sink));
        _sink = sink;
        ((delegate* unmanaged<IntPtr, int, void>)Vt(sink)[3])(sink, winding ? 1 : 0);
    }

    private static IntPtr* Vt(IntPtr p) => *(IntPtr**)p;

    public PathBuilder MoveTo(float x, float y)
    {
        if (_figureOpen) EndFigure(false);
        ((delegate* unmanaged<IntPtr, D2DPoint, int, void>)Vt(_sink)[5])(_sink, new D2DPoint(x, y), 0);
        _figureOpen = true;
        _x = x; _y = y;
        return this;
    }

    public PathBuilder LineTo(float x, float y)
    {
        ((delegate* unmanaged<IntPtr, D2DPoint, void>)Vt(_sink)[10])(_sink, new D2DPoint(x, y));
        _x = x; _y = y;
        return this;
    }

    public PathBuilder QuadTo(float cx, float cy, float x, float y)
    {
        var seg = new QuadSegment { P1 = new(cx, cy), P2 = new(x, y) };
        ((delegate* unmanaged<IntPtr, QuadSegment*, void>)Vt(_sink)[12])(_sink, &seg);
        _x = x; _y = y;
        return this;
    }

    public PathBuilder CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
    {
        var seg = new BezierSegment { P1 = new(c1x, c1y), P2 = new(c2x, c2y), P3 = new(x, y) };
        ((delegate* unmanaged<IntPtr, BezierSegment*, void>)Vt(_sink)[11])(_sink, &seg);
        _x = x; _y = y;
        return this;
    }

    public PathBuilder ArcTo(float x, float y, float rx, float ry, bool clockwise, bool large = false, float rotationDegrees = 0)
    {
        var seg = new ArcSegment
        {
            Point = new(x, y),
            Width = rx,
            Height = ry,
            Rotation = rotationDegrees,
            Sweep = clockwise ? 1 : 0,
            ArcSize = large ? 1 : 0,
        };
        ((delegate* unmanaged<IntPtr, ArcSegment*, void>)Vt(_sink)[14])(_sink, &seg);
        _x = x; _y = y;
        return this;
    }

    public PathBuilder Close() => EndFigure(true);

    public PathBuilder EndFigure(bool closed)
    {
        if (_figureOpen)
        {
            ((delegate* unmanaged<IntPtr, int, void>)Vt(_sink)[8])(_sink, closed ? 1 : 0);
            _figureOpen = false;
        }
        return this;
    }

    public PathBuilder Ellipse(float cx, float cy, float rx, float ry)
    {
        MoveTo(cx - rx, cy);
        ArcTo(cx + rx, cy, rx, ry, clockwise: true);
        ArcTo(cx - rx, cy, rx, ry, clockwise: true);
        return Close();
    }

    public PathBuilder Circle(float cx, float cy, float r) => Ellipse(cx, cy, r, r);

    public PathBuilder Polygon(params float[] xy)
    {
        MoveTo(xy[0], xy[1]);
        for (int i = 2; i + 1 < xy.Length; i += 2) LineTo(xy[i], xy[i + 1]);
        return Close();
    }

    public PathBuilder RoundRect(float x, float y, float w, float h, float r)
    {
        r = MathF.Min(r, MathF.Min(w, h) / 2);
        MoveTo(x + r, y);
        LineTo(x + w - r, y);
        ArcTo(x + w, y + r, r, r, true);
        LineTo(x + w, y + h - r);
        ArcTo(x + w - r, y + h, r, r, true);
        LineTo(x + r, y + h);
        ArcTo(x, y + h - r, r, r, true);
        LineTo(x, y + r);
        ArcTo(x + r, y, r, r, true);
        return Close();
    }

    /// <summary>Finishes the geometry and wraps it for composition.</summary>
    public CompositionPath Build()
    {
        if (_sink == IntPtr.Zero) throw new InvalidOperationException("PathBuilder already built.");
        EndFigure(false);
        int hr = ((delegate* unmanaged<IntPtr, int>)Vt(_sink)[9])(_sink);
        Marshal.Release(_sink);
        _sink = IntPtr.Zero;
        D2D.Check(hr);
        IntPtr geometry = _geometry;
        _geometry = IntPtr.Zero;
        return GeometrySource.CreatePath(geometry);
    }

    public CompositionPathGeometry BuildGeometry(Compositor compositor) => compositor.CreatePathGeometry(Build());
}

internal static unsafe class D2D
{
    private static readonly Guid IID_ID2D1Factory = new("06152247-6f50-465a-9245-118bfd3b6007");
    private static IntPtr s_factory;

    [DllImport("d2d1.dll")]
    private static extern int D2D1CreateFactory(int factoryType, in Guid riid, IntPtr options, out IntPtr factory);

    /// <summary>Process-lifetime multi-threaded D2D factory (never released).</summary>
    public static IntPtr Factory
    {
        get
        {
            if (s_factory == IntPtr.Zero)
            {
                Check(D2D1CreateFactory(1, in IID_ID2D1Factory, IntPtr.Zero, out IntPtr f));
                if (Interlocked.CompareExchange(ref s_factory, f, IntPtr.Zero) != IntPtr.Zero) Marshal.Release(f);
            }
            return s_factory;
        }
    }

    public static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }
}

/// <summary>
/// Minimal agile COM object implementing IGeometrySource2D (IInspectable) and
/// IGeometrySource2DInterop over an owned ID2D1Geometry.
/// </summary>
internal static unsafe class GeometrySource
{
    private const int S_OK = 0;
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_POINTER = unchecked((int)0x80004003);

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90");
    private static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    private static readonly Guid IID_IGeometrySource2D = new("CAFF7902-670C-4181-A624-DA977203B845");
    private static readonly Guid IID_IGeometrySource2DInterop = new("0657AF73-53FD-47CF-84FF-C8492D2A80A3");

    [StructLayout(LayoutKind.Sequential)]
    private struct Ccw
    {
        public IntPtr VtblSource;  // IUnknown/IInspectable/IGeometrySource2D identity
        public IntPtr VtblInterop; // IGeometrySource2DInterop (this + sizeof(IntPtr))
        public int RefCount;
        public IntPtr Geometry;    // owned ID2D1Geometry*
    }

    private static readonly IntPtr* s_vtblSource = CreateSourceVtbl();
    private static readonly IntPtr* s_vtblInterop = CreateInteropVtbl();

    private static IntPtr* CreateSourceVtbl()
    {
        var v = (IntPtr*)NativeMemory.Alloc(6, (nuint)sizeof(IntPtr));
        v[0] = (IntPtr)(delegate* unmanaged<Ccw*, Guid*, IntPtr*, int>)&QueryInterface;
        v[1] = (IntPtr)(delegate* unmanaged<Ccw*, uint>)&AddRef;
        v[2] = (IntPtr)(delegate* unmanaged<Ccw*, uint>)&Release;
        v[3] = (IntPtr)(delegate* unmanaged<Ccw*, uint*, IntPtr*, int>)&GetIids;
        v[4] = (IntPtr)(delegate* unmanaged<Ccw*, IntPtr*, int>)&GetRuntimeClassName;
        v[5] = (IntPtr)(delegate* unmanaged<Ccw*, int*, int>)&GetTrustLevel;
        return v;
    }

    private static IntPtr* CreateInteropVtbl()
    {
        var v = (IntPtr*)NativeMemory.Alloc(5, (nuint)sizeof(IntPtr));
        v[0] = (IntPtr)(delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&InteropQueryInterface;
        v[1] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&InteropAddRef;
        v[2] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&InteropRelease;
        v[3] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&GetGeometry;
        v[4] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&TryGetGeometryUsingFactory;
        return v;
    }

    /// <summary>Takes ownership of <paramref name="geometry"/>.</summary>
    public static CompositionPath CreatePath(IntPtr geometry)
    {
        var ccw = (Ccw*)NativeMemory.Alloc((nuint)sizeof(Ccw));
        ccw->VtblSource = (IntPtr)s_vtblSource;
        ccw->VtblInterop = (IntPtr)s_vtblInterop;
        ccw->RefCount = 1;
        ccw->Geometry = geometry;
        try
        {
            IGeometrySource2D source = MarshalInterface<IGeometrySource2D>.FromAbi((IntPtr)ccw);
            return new CompositionPath(source);
        }
        finally
        {
            ReleaseCore(ccw);
        }
    }

    private static Ccw* FromInterop(IntPtr p) => (Ccw*)(p - sizeof(IntPtr));

    private static uint ReleaseCore(Ccw* self)
    {
        int count = Interlocked.Decrement(ref self->RefCount);
        if (count == 0)
        {
            if (self->Geometry != IntPtr.Zero) Marshal.Release(self->Geometry);
            NativeMemory.Free(self);
        }
        return (uint)count;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(Ccw* self, Guid* riid, IntPtr* ppv)
    {
        if (ppv == null) return E_POINTER;
        Guid iid = *riid;
        if (iid == IID_IUnknown || iid == IID_IInspectable || iid == IID_IGeometrySource2D || iid == IID_IAgileObject)
        {
            *ppv = (IntPtr)self;
        }
        else if (iid == IID_IGeometrySource2DInterop)
        {
            *ppv = (IntPtr)(&self->VtblInterop);
        }
        else
        {
            *ppv = IntPtr.Zero;
            return E_NOINTERFACE;
        }
        Interlocked.Increment(ref self->RefCount);
        return S_OK;
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(Ccw* self) => (uint)Interlocked.Increment(ref self->RefCount);

    [UnmanagedCallersOnly]
    private static uint Release(Ccw* self) => ReleaseCore(self);

    [UnmanagedCallersOnly]
    private static int GetIids(Ccw* self, uint* count, IntPtr* iids)
    {
        if (count != null) *count = 0;
        if (iids != null) *iids = IntPtr.Zero;
        return S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetRuntimeClassName(Ccw* self, IntPtr* name)
    {
        if (name != null) *name = IntPtr.Zero;
        return S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetTrustLevel(Ccw* self, int* level)
    {
        if (level != null) *level = 0;
        return S_OK;
    }

    [UnmanagedCallersOnly]
    private static int InteropQueryInterface(IntPtr self, Guid* riid, IntPtr* ppv)
    {
        Ccw* ccw = FromInterop(self);
        var qi = (delegate* unmanaged<Ccw*, Guid*, IntPtr*, int>)s_vtblSource[0];
        return qi(ccw, riid, ppv);
    }

    [UnmanagedCallersOnly]
    private static uint InteropAddRef(IntPtr self) => (uint)Interlocked.Increment(ref FromInterop(self)->RefCount);

    [UnmanagedCallersOnly]
    private static uint InteropRelease(IntPtr self) => ReleaseCore(FromInterop(self));

    [UnmanagedCallersOnly]
    private static int GetGeometry(IntPtr self, IntPtr* value)
    {
        if (value == null) return E_POINTER;
        IntPtr g = FromInterop(self)->Geometry;
        if (g != IntPtr.Zero) Marshal.AddRef(g);
        *value = g;
        return S_OK;
    }

    [UnmanagedCallersOnly]
    private static int TryGetGeometryUsingFactory(IntPtr self, IntPtr factory, IntPtr* value)
    {
        if (value == null) return E_POINTER;
        *value = IntPtr.Zero;
        if (factory == D2D.Factory)
        {
            IntPtr g = FromInterop(self)->Geometry;
            if (g != IntPtr.Zero) Marshal.AddRef(g);
            *value = g;
        }
        return S_OK;
    }
}
