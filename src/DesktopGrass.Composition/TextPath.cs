// TextPath.cs - turns a string into vector outlines (via GDI+) so labels are plain composition
// shapes: no text rendering stack, crisp at any DPI, and animatable like everything else.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using DesktopGrass.Composition.Interop;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal static class TextPath
{
    public readonly record struct Result(CompositionPath Path, float Width, float Height);

    public static Result Create(string text, float emSize, bool bold = true)
    {
        using var gp = new GraphicsPath(FillMode.Alternate);
        using var family = new FontFamily("Segoe UI");
        gp.AddString(text, family, (int)(bold ? FontStyle.Bold : FontStyle.Regular), emSize, new PointF(0, 0), StringFormat.GenericTypographic);
        RectangleF bounds = gp.GetBounds();
        PointF[] points = gp.PathPoints;
        byte[] types = gp.PathTypes;

        var pb = new PathBuilder(winding: false);
        float ox = bounds.X, oy = bounds.Y;
        for (int i = 0; i < points.Length; i++)
        {
            int kind = types[i] & 0x07;
            int last = i;
            switch (kind)
            {
                case 0:
                    pb.MoveTo(points[i].X - ox, points[i].Y - oy);
                    break;
                case 1:
                    pb.LineTo(points[i].X - ox, points[i].Y - oy);
                    break;
                case 3 when i + 2 < points.Length:
                    pb.CubicTo(points[i].X - ox, points[i].Y - oy,
                               points[i + 1].X - ox, points[i + 1].Y - oy,
                               points[i + 2].X - ox, points[i + 2].Y - oy);
                    last = i + 2;
                    i += 2;
                    break;
            }
            if ((types[last] & 0x80) != 0) pb.Close();
        }
        return new Result(pb.Build(), MathF.Max(1, bounds.Width), MathF.Max(1, bounds.Height));
    }
}
