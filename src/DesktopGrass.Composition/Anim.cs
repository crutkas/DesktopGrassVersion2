// Anim.cs - shorthand for building composition animations and colors.
using System;
using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal static class Anim
{
    private static Compositor? s_compositor;
    private static CompositionEasingFunction? s_linear, s_easeOut, s_easeIn, s_easeInOut, s_backOut;

    public static Compositor C => s_compositor ?? throw new InvalidOperationException("Anim.Init not called.");

    public static void Init(Compositor compositor)
    {
        s_compositor = compositor;
        s_linear = compositor.CreateLinearEasingFunction();
        s_easeOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.7f), new Vector2(0.35f, 1f));
        s_easeIn = compositor.CreateCubicBezierEasingFunction(new Vector2(0.55f, 0f), new Vector2(0.8f, 0.4f));
        s_easeInOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));
        s_backOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 1.6f), new Vector2(0.6f, 1f));
    }

    public static CompositionEasingFunction Linear => s_linear!;
    public static CompositionEasingFunction EaseOut => s_easeOut!;
    public static CompositionEasingFunction EaseIn => s_easeIn!;
    public static CompositionEasingFunction EaseInOut => s_easeInOut!;
    public static CompositionEasingFunction BackOut => s_backOut!;

    public static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(Math.Max(0.001, seconds));

    /// <summary>Keyframes given as (progress, value) pairs. No key at 0 means "from current value".</summary>
    public static ScalarKeyFrameAnimation Scalar(double seconds, CompositionEasingFunction? easing, params float[] pairs)
    {
        var a = C.CreateScalarKeyFrameAnimation();
        a.Duration = Sec(seconds);
        var e = easing ?? Linear;
        for (int i = 0; i + 1 < pairs.Length; i += 2) a.InsertKeyFrame(pairs[i], pairs[i + 1], e);
        return a;
    }

    public static ScalarKeyFrameAnimation ScalarLoop(double seconds, CompositionEasingFunction? easing, params float[] pairs)
    {
        var a = Scalar(seconds, easing, pairs);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        return a;
    }

    public static Vector2KeyFrameAnimation Vec2(double seconds, CompositionEasingFunction? easing, params (float Progress, Vector2 Value)[] frames)
    {
        var a = C.CreateVector2KeyFrameAnimation();
        a.Duration = Sec(seconds);
        var e = easing ?? Linear;
        foreach (var (p, v) in frames) a.InsertKeyFrame(p, v, e);
        return a;
    }

    public static Vector3KeyFrameAnimation Vec3(double seconds, CompositionEasingFunction? easing, params (float Progress, Vector3 Value)[] frames)
    {
        var a = C.CreateVector3KeyFrameAnimation();
        a.Duration = Sec(seconds);
        var e = easing ?? Linear;
        foreach (var (p, v) in frames) a.InsertKeyFrame(p, v, e);
        return a;
    }

    public static ColorKeyFrameAnimation Color(double seconds, Color to)
    {
        var a = C.CreateColorKeyFrameAnimation();
        a.Duration = Sec(seconds);
        a.InterpolationColorSpace = CompositionColorSpace.Rgb;
        a.InsertKeyFrame(1f, to, EaseInOut);
        return a;
    }

    public static Color Hex(uint rgb, byte alpha = 255) =>
        Windows.UI.Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static Color WithAlpha(Color c, byte alpha) => Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Windows.UI.Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    public static Color Darken(Color c, float amount) => Mix(c, Windows.UI.Color.FromArgb(c.A, 0, 0, 0), amount);

    public static Color Lighten(Color c, float amount) => Mix(c, Windows.UI.Color.FromArgb(c.A, 255, 255, 255), amount);
}
