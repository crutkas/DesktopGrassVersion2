// Shapes.cs - helpers for vector art built from composition shapes.
using System.Collections.Generic;
using System.Numerics;
using DesktopGrass.Composition.Interop;
using Windows.UI;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal static class Shapes
{
    private static readonly Dictionary<uint, CompositionColorBrush> s_brushes = new();

    private static Compositor C => Anim.C;

    /// <summary>Shared, immutable solid brush. Do not animate these.</summary>
    public static CompositionColorBrush Solid(Color color)
    {
        uint key = (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B);
        if (!s_brushes.TryGetValue(key, out var brush))
        {
            brush = C.CreateColorBrush(color);
            s_brushes[key] = brush;
        }
        return brush;
    }

    public static CompositionColorBrush Solid(uint rgb, byte alpha = 255) => Solid(Anim.Hex(rgb, alpha));

    public static void ClearCache() => s_brushes.Clear();

    public static ShapeVisual Visual(float width, float height, Vector2 offset = default)
    {
        var v = C.CreateShapeVisual();
        v.Size = new Vector2(width, height);
        v.Offset = new Vector3(offset, 0);
        return v;
    }

    public static ContainerVisual Container(Vector2 offset = default)
    {
        var v = C.CreateContainerVisual();
        v.Offset = new Vector3(offset, 0);
        return v;
    }

    public static CompositionSpriteShape Path(CompositionPath path, CompositionBrush? fill, CompositionBrush? stroke = null, float strokeWidth = 0)
    {
        var shape = C.CreateSpriteShape(C.CreatePathGeometry(path));
        shape.FillBrush = fill;
        if (stroke != null)
        {
            shape.StrokeBrush = stroke;
            shape.StrokeThickness = strokeWidth;
            shape.StrokeStartCap = CompositionStrokeCap.Round;
            shape.StrokeEndCap = CompositionStrokeCap.Round;
            shape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        }
        return shape;
    }

    public static CompositionSpriteShape Path(PathBuilder builder, CompositionBrush? fill, CompositionBrush? stroke = null, float strokeWidth = 0) =>
        Path(builder.Build(), fill, stroke, strokeWidth);

    public static CompositionSpriteShape Ellipse(float cx, float cy, float rx, float ry, CompositionBrush fill)
    {
        var g = C.CreateEllipseGeometry();
        g.Center = new Vector2(cx, cy);
        g.Radius = new Vector2(rx, ry);
        var shape = C.CreateSpriteShape(g);
        shape.FillBrush = fill;
        return shape;
    }

    public static CompositionSpriteShape Circle(float cx, float cy, float r, CompositionBrush fill) => Ellipse(cx, cy, r, r, fill);

    public static CompositionSpriteShape RoundRect(float x, float y, float w, float h, float r, CompositionBrush fill)
    {
        var g = C.CreateRoundedRectangleGeometry();
        g.Offset = new Vector2(x, y);
        g.Size = new Vector2(w, h);
        g.CornerRadius = new Vector2(r, r);
        var shape = C.CreateSpriteShape(g);
        shape.FillBrush = fill;
        return shape;
    }

    public static CompositionSpriteShape Line(float x1, float y1, float x2, float y2, CompositionBrush stroke, float width)
    {
        var g = C.CreateLineGeometry();
        g.Start = new Vector2(x1, y1);
        g.End = new Vector2(x2, y2);
        var shape = C.CreateSpriteShape(g);
        shape.StrokeBrush = stroke;
        shape.StrokeThickness = width;
        shape.StrokeStartCap = CompositionStrokeCap.Round;
        shape.StrokeEndCap = CompositionStrokeCap.Round;
        return shape;
    }

    /// <summary>Soft elliptical contact shadow: a radial gradient that fades to transparent.</summary>
    public static CompositionSpriteShape ContactShadow(float cx, float cy, float rx, float ry, byte alpha = 70)
    {
        var brush = C.CreateRadialGradientBrush();
        brush.MappingMode = CompositionMappingMode.Relative;
        brush.EllipseCenter = new Vector2(0.5f, 0.5f);
        brush.EllipseRadius = new Vector2(0.5f, 0.5f);
        brush.ColorStops.Add(C.CreateColorGradientStop(0f, Anim.Hex(0x10160A, alpha)));
        brush.ColorStops.Add(C.CreateColorGradientStop(0.55f, Anim.Hex(0x10160A, (byte)(alpha * 0.55f))));
        brush.ColorStops.Add(C.CreateColorGradientStop(1f, Anim.Hex(0x10160A, 0)));
        return Ellipse(cx, cy, rx, ry, brush);
    }

    /// <summary>Radial glow (bright center fading out), used for fireflies and highlights.</summary>
    public static CompositionRadialGradientBrush Glow(Color color, byte centerAlpha)
    {
        var brush = C.CreateRadialGradientBrush();
        brush.MappingMode = CompositionMappingMode.Relative;
        brush.EllipseCenter = new Vector2(0.5f, 0.5f);
        brush.EllipseRadius = new Vector2(0.5f, 0.5f);
        brush.ColorStops.Add(C.CreateColorGradientStop(0f, Anim.WithAlpha(color, centerAlpha)));
        brush.ColorStops.Add(C.CreateColorGradientStop(0.4f, Anim.WithAlpha(color, (byte)(centerAlpha * 0.45f))));
        brush.ColorStops.Add(C.CreateColorGradientStop(1f, Anim.WithAlpha(color, 0)));
        return brush;
    }

    /// <summary>Vertical gradient in relative mapping (top color to bottom color).</summary>
    public static CompositionLinearGradientBrush Vertical(Color top, Color bottom)
    {
        var brush = C.CreateLinearGradientBrush();
        brush.MappingMode = CompositionMappingMode.Relative;
        brush.StartPoint = new Vector2(0.5f, 0f);
        brush.EndPoint = new Vector2(0.5f, 1f);
        brush.ColorStops.Add(C.CreateColorGradientStop(0f, top));
        brush.ColorStops.Add(C.CreateColorGradientStop(1f, bottom));
        return brush;
    }
}
