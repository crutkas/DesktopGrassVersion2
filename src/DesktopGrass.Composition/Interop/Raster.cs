// Interop/Raster.cs - Direct2D rendering into composition drawing surfaces.
//
// Live composition shapes are re-rasterized by DWM every frame they sit in a dirty region, and
// CompositionVisualSurface caches them without anti-aliasing. So static art (grass tiles, mushrooms)
// is rasterized here, once, with Direct2D (full anti-aliasing), into CompositionDrawingSurfaces that DWM
// composes as plain textures. Composition still does all the motion; we redraw a tile only when its
// content changes (a cut, regrowth, a new palette).
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX;
using Windows.UI;
using Windows.UI.Composition;
using WinRT;

namespace DesktopGrass.Composition.Interop;

internal static unsafe class Raster
{
    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_ID2D1Factory1 = new("bb12d362-daee-4b9a-aa1d-14ba401cfa1f");
    private static readonly Guid IID_ICompositorInterop = new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");
    private static readonly Guid IID_ICompositionGraphicsDeviceInterop = new("A116FF71-F8BF-4C8A-9C98-70779A32A9C8");

    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_DRIVER_TYPE_WARP = 5;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, IntPtr immediateContext);

    private static IntPtr s_d2dDevice; // ID2D1Device*
    private static CompositionGraphicsDevice? s_device;
    private static readonly Dictionary<BrushKey, IntPtr> s_brushes = new();

    public static CompositionGraphicsDevice Device => s_device ?? throw new InvalidOperationException("Raster.Init not called.");

    /// <summary>Raised (on the UI thread) after the GPU device was lost and replaced; every surface must be redrawn.</summary>
    public static event Action? DeviceReplaced;

    public static void Init(Compositor compositor)
    {
        s_d2dDevice = CreateD2DDevice();
        IntPtr unknown = MarshalInspectable<Compositor>.FromManaged(compositor);
        try
        {
            Guid iid = IID_ICompositorInterop;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out IntPtr interop));
            try
            {
                IntPtr result = IntPtr.Zero;
                var fn = (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)(*(IntPtr**)interop)[5]; // CreateGraphicsDevice
                Marshal.ThrowExceptionForHR(fn(interop, s_d2dDevice, &result));
                try
                {
                    s_device = CompositionGraphicsDevice.FromAbi(result);
                }
                finally
                {
                    Marshal.Release(result);
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

    private static IntPtr CreateD2DDevice()
    {
        int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0,
            D3D11_SDK_VERSION, out IntPtr d3d, out _, IntPtr.Zero);
        if (hr < 0)
        {
            Log.Write($"raster: hardware D3D device failed (0x{hr:X8}), using WARP");
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION, out d3d, out _, IntPtr.Zero));
        }
        try
        {
            Guid iidDxgi = IID_IDXGIDevice, iidFactory1 = IID_ID2D1Factory1;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in iidDxgi, out IntPtr dxgi));
            try
            {
                // The device must come from the factory our path geometries were created with.
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(D2D.Factory, in iidFactory1, out IntPtr factory1));
                try
                {
                    IntPtr device = IntPtr.Zero;
                    var create = (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)(*(IntPtr**)factory1)[17]; // ID2D1Factory1::CreateDevice
                    Marshal.ThrowExceptionForHR(create(factory1, dxgi, &device));
                    return device;
                }
                finally
                {
                    Marshal.Release(factory1);
                }
            }
            finally
            {
                Marshal.Release(dxgi);
            }
        }
        finally
        {
            Marshal.Release(d3d);
        }
    }

    /// <summary>Replaces a lost rendering device; composition then wants every surface redrawn.</summary>
    internal static void RecoverDevice()
    {
        ClearBrushes();
        IntPtr old = s_d2dDevice;
        s_d2dDevice = CreateD2DDevice();
        IntPtr unknown = MarshalInspectable<CompositionGraphicsDevice>.FromManaged(Device);
        try
        {
            Guid iid = IID_ICompositionGraphicsDeviceInterop;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out IntPtr interop));
            try
            {
                var set = (delegate* unmanaged<IntPtr, IntPtr, int>)(*(IntPtr**)interop)[4]; // SetRenderingDevice
                Marshal.ThrowExceptionForHR(set(interop, s_d2dDevice));
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
        if (old != IntPtr.Zero) Marshal.Release(old);
        Log.Write("raster: rendering device replaced");
        DeviceReplaced?.Invoke();
    }

    // ---------------------------------------------------------------- device-dependent brushes

    internal static bool TryGetBrush(BrushKey key, out IntPtr brush) => s_brushes.TryGetValue(key, out brush);

    internal static void AddBrush(BrushKey key, IntPtr brush) => s_brushes[key] = brush;

    private static void ClearBrushes()
    {
        foreach (var b in s_brushes.Values) Marshal.Release(b);
        s_brushes.Clear();
    }
}

/// <summary>One CompositionDrawingSurface plus its interop, drawn with Direct2D.</summary>
internal sealed unsafe class Canvas : IDisposable
{
    private static readonly Guid IID_ICompositionDrawingSurfaceInterop = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");
    private static readonly Guid IID_ID2D1DeviceContext = new("e8f7fe7a-191c-466d-ad95-975678bda998");

    public readonly CompositionDrawingSurface Surface;
    public readonly int PixelWidth, PixelHeight;
    private IntPtr _interop;

    public Canvas(int pixelWidth, int pixelHeight)
    {
        PixelWidth = Math.Max(1, pixelWidth);
        PixelHeight = Math.Max(1, pixelHeight);
        Surface = Raster.Device.CreateDrawingSurface(new Windows.Foundation.Size(PixelWidth, PixelHeight),
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        IntPtr unknown = MarshalInspectable<CompositionDrawingSurface>.FromManaged(Surface);
        try
        {
            Guid iid = IID_ICompositionDrawingSurfaceInterop;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out _interop));
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>
    /// Redraws the whole surface. <paramref name="draw"/> works in pixels from the surface's top-left; the
    /// surface may live in an atlas, which the painter's base transform accounts for.
    /// Returns false if the GPU device was lost (it is replaced, and the app redraws everything).
    /// </summary>
    public bool Draw(Action<Painter> draw)
    {
        if (_interop == IntPtr.Zero) return false;
        IntPtr context = IntPtr.Zero;
        Painter.PointI offset;
        Guid iid = IID_ID2D1DeviceContext;
        var begin = (delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, Painter.PointI*, int>)(*(IntPtr**)_interop)[3];
        int hr = begin(_interop, IntPtr.Zero, &iid, &context, &offset);
        if (hr < 0)
        {
            Log.Write($"raster: BeginDraw failed (0x{hr:X8})");
            if (IsDeviceLost(hr)) Raster.RecoverDevice();
            return false;
        }
        try
        {
            var painter = new Painter(context, Matrix3x2.CreateTranslation(offset.X, offset.Y));
            painter.Clear();
            draw(painter);
        }
        finally
        {
            var end = (delegate* unmanaged<IntPtr, int>)(*(IntPtr**)_interop)[4];
            hr = end(_interop);
            Marshal.Release(context);
        }
        if (hr < 0)
        {
            Log.Write($"raster: EndDraw failed (0x{hr:X8})");
            if (IsDeviceLost(hr)) Raster.RecoverDevice();
            return false;
        }
        return true;
    }

    private static bool IsDeviceLost(int hr) =>
        hr is unchecked((int)0x887A0005) or unchecked((int)0x887A0006) or unchecked((int)0x887A0007) or unchecked((int)0x8899000C);

    public void Dispose()
    {
        if (_interop != IntPtr.Zero)
        {
            Marshal.Release(_interop);
            _interop = IntPtr.Zero;
        }
        Surface.Dispose();
    }
}

/// <summary>A Direct2D device context inside Canvas.Draw. Transforms are relative to the surface's top-left, in pixels.</summary>
internal readonly unsafe struct Painter
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PointI { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ColorF { public float R, G, B, A; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BrushProperties { public float Opacity; public Matrix3x2 Transform; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GradientStop { public float Position; public ColorF Color; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinearProperties { public D2DPoint Start, End; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RadialProperties { public D2DPoint Center, OriginOffset; public float RadiusX, RadiusY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EllipseF { public D2DPoint Center; public float RadiusX, RadiusY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RoundedRectF { public float Left, Top, Right, Bottom, RadiusX, RadiusY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectF { public float Left, Top, Right, Bottom; }

    private readonly IntPtr _ctx;
    private readonly Matrix3x2 _base;

    internal Painter(IntPtr context, Matrix3x2 baseTransform)
    {
        _ctx = context;
        _base = baseTransform;
    }

    private IntPtr* Vt => *(IntPtr**)_ctx;

    /// <summary>Sets the current transform (pixels relative to the surface's top-left).</summary>
    public void Transform(Matrix3x2 m)
    {
        var t = m * _base;
        ((delegate* unmanaged<IntPtr, Matrix3x2*, void>)Vt[30])(_ctx, &t); // SetTransform
    }

    public void Clear()
    {
        var transparent = default(ColorF);
        ((delegate* unmanaged<IntPtr, ColorF*, void>)Vt[47])(_ctx, &transparent); // Clear
    }

    public void Fill(IntPtr geometry, IntPtr brush) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)Vt[23])(_ctx, geometry, brush, IntPtr.Zero); // FillGeometry

    public void Ellipse(float cx, float cy, float rx, float ry, IntPtr brush)
    {
        var e = new EllipseF { Center = new D2DPoint(cx, cy), RadiusX = rx, RadiusY = ry };
        ((delegate* unmanaged<IntPtr, EllipseF*, IntPtr, void>)Vt[21])(_ctx, &e, brush); // FillEllipse
    }

    public void RoundRect(float x, float y, float w, float h, float r, IntPtr brush)
    {
        var rr = new RoundedRectF { Left = x, Top = y, Right = x + w, Bottom = y + h, RadiusX = r, RadiusY = r };
        ((delegate* unmanaged<IntPtr, RoundedRectF*, IntPtr, void>)Vt[19])(_ctx, &rr, brush); // FillRoundedRectangle
    }

    public void Rect(float x, float y, float w, float h, IntPtr brush)
    {
        var r = new RectF { Left = x, Top = y, Right = x + w, Bottom = y + h };
        ((delegate* unmanaged<IntPtr, RectF*, IntPtr, void>)Vt[17])(_ctx, &r, brush); // FillRectangle
    }

    public void Line(float x1, float y1, float x2, float y2, float width, IntPtr brush) =>
        ((delegate* unmanaged<IntPtr, D2DPoint, D2DPoint, IntPtr, float, IntPtr, void>)Vt[15])(
            _ctx, new D2DPoint(x1, y1), new D2DPoint(x2, y2), brush, width, IntPtr.Zero); // DrawLine

    public void StrokeEllipse(float cx, float cy, float rx, float ry, float width, IntPtr brush)
    {
        var e = new EllipseF { Center = new D2DPoint(cx, cy), RadiusX = rx, RadiusY = ry };
        ((delegate* unmanaged<IntPtr, EllipseF*, IntPtr, float, IntPtr, void>)Vt[20])(_ctx, &e, brush, width, IntPtr.Zero); // DrawEllipse
    }

    /// <summary>Fills with a multi-stop linear gradient (geometry space). The brush is made for this one fill and released.</summary>
    public void FillGradient(IntPtr geometry, Vector2 from, Vector2 to, params (float Position, Color Color)[] colors)
    {
        var stops = stackalloc GradientStop[colors.Length];
        for (int i = 0; i < colors.Length; i++) stops[i] = new GradientStop { Position = colors[i].Position, Color = F(colors[i].Color) };
        IntPtr collection = Stops(stops, (uint)colors.Length);
        IntPtr brush;
        try
        {
            var lp = new LinearProperties { Start = new D2DPoint(from.X, from.Y), End = new D2DPoint(to.X, to.Y) };
            var props = new BrushProperties { Opacity = 1, Transform = Matrix3x2.Identity };
            D2D.Check(((delegate* unmanaged<IntPtr, LinearProperties*, BrushProperties*, IntPtr, IntPtr*, int>)Vt[10])(_ctx, &lp, &props, collection, &brush)); // CreateLinearGradientBrush
        }
        finally
        {
            Marshal.Release(collection);
        }
        try
        {
            Fill(geometry, brush);
        }
        finally
        {
            Marshal.Release(brush);
        }
    }

    // ---------------------------------------------------------------- brushes (cached per device)

    private static ColorF F(Color c) => new() { R = c.R / 255f, G = c.G / 255f, B = c.B / 255f, A = c.A / 255f };

    private static long Key(Color c) => (long)c.A << 24 | (long)c.R << 16 | (long)c.G << 8 | c.B;

    public IntPtr Solid(Color color)
    {
        var key = new BrushKey(0, Key(color), 0, default, default);
        if (Raster.TryGetBrush(key, out var brush)) return brush;
        var c = F(color);
        var props = new BrushProperties { Opacity = 1, Transform = Matrix3x2.Identity };
        D2D.Check(((delegate* unmanaged<IntPtr, ColorF*, BrushProperties*, IntPtr*, int>)Vt[8])(_ctx, &c, &props, &brush)); // CreateSolidColorBrush
        Raster.AddBrush(key, brush);
        return brush;
    }

    /// <summary>Linear gradient from <paramref name="a"/> at <paramref name="from"/> to <paramref name="b"/> at <paramref name="to"/> (geometry space).</summary>
    public IntPtr Linear(Color a, Color b, Vector2 from, Vector2 to)
    {
        var key = new BrushKey(1, Key(a), Key(b), from, to);
        if (Raster.TryGetBrush(key, out var brush)) return brush;
        var stops = stackalloc GradientStop[2];
        stops[0] = new GradientStop { Position = 0, Color = F(a) };
        stops[1] = new GradientStop { Position = 1, Color = F(b) };
        IntPtr collection = Stops(stops, 2);
        try
        {
            var lp = new LinearProperties { Start = new D2DPoint(from.X, from.Y), End = new D2DPoint(to.X, to.Y) };
            var props = new BrushProperties { Opacity = 1, Transform = Matrix3x2.Identity };
            D2D.Check(((delegate* unmanaged<IntPtr, LinearProperties*, BrushProperties*, IntPtr, IntPtr*, int>)Vt[10])(_ctx, &lp, &props, collection, &brush)); // CreateLinearGradientBrush
        }
        finally
        {
            Marshal.Release(collection);
        }
        Raster.AddBrush(key, brush);
        return brush;
    }

    /// <summary>Soft round shadow: a unit-radius radial gradient at the origin; draw it with a transform that places and sizes it.</summary>
    public IntPtr UnitShadow(Color color)
    {
        var key = new BrushKey(2, Key(color), 0, default, default);
        if (Raster.TryGetBrush(key, out var brush)) return brush;
        var stops = stackalloc GradientStop[3];
        stops[0] = new GradientStop { Position = 0, Color = F(color) };
        stops[1] = new GradientStop { Position = 0.55f, Color = F(Anim.WithAlpha(color, (byte)(color.A * 0.55f))) };
        stops[2] = new GradientStop { Position = 1, Color = F(Anim.WithAlpha(color, 0)) };
        IntPtr collection = Stops(stops, 3);
        try
        {
            var rp = new RadialProperties { Center = new D2DPoint(0, 0), OriginOffset = new D2DPoint(0, 0), RadiusX = 1, RadiusY = 1 };
            var props = new BrushProperties { Opacity = 1, Transform = Matrix3x2.Identity };
            D2D.Check(((delegate* unmanaged<IntPtr, RadialProperties*, BrushProperties*, IntPtr, IntPtr*, int>)Vt[11])(_ctx, &rp, &props, collection, &brush)); // CreateRadialGradientBrush
        }
        finally
        {
            Marshal.Release(collection);
        }
        Raster.AddBrush(key, brush);
        return brush;
    }

    private IntPtr Stops(GradientStop* stops, uint count)
    {
        IntPtr collection;
        // CreateGradientStopCollection(stops, count, D2D1_GAMMA_2_2, D2D1_EXTEND_MODE_CLAMP, out collection)
        D2D.Check(((delegate* unmanaged<IntPtr, GradientStop*, uint, int, int, IntPtr*, int>)Vt[9])(_ctx, stops, count, 0, 0, &collection));
        return collection;
    }
}

/// <summary>Identifies a cached Direct2D brush (kind 0 solid, 1 linear gradient, 2 unit shadow).</summary>
internal readonly record struct BrushKey(int Kind, long A, long B, Vector2 From, Vector2 To);