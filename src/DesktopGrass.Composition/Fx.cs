// Fx.cs - fire-and-forget particles. Each particle is a tiny ShapeVisual whose keyframe animations
// run on DWM; a ScopedBatch removes it when they finish, so the app does no per-frame work.
using System;
using System.Numerics;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal static class Fx
{
    public const int MaxLive = 300;

    public static int Live { get; private set; }

    /// <summary>Ballistic toss: rises for the first 30% of the flight, then falls by <paramref name="drop"/>, spinning and fading.</summary>
    public static void Toss(ContainerVisual layer, CompositionShape shape, float size, Vector2 center, float dx, float rise, float drop, float spinDegrees, double seconds)
    {
        if (Live >= MaxLive) return;
        var c = Anim.C;
        var v = Spawn(layer, shape, size, center);
        float x0 = v.Offset.X, y0 = v.Offset.Y;

        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        v.StartAnimation("Offset.X", Anim.Scalar(seconds, Anim.Linear, 1f, x0 + dx));
        var y = c.CreateScalarKeyFrameAnimation();
        y.Duration = Anim.Sec(seconds);
        y.InsertKeyFrame(0.3f, y0 - rise, Anim.EaseOut);
        y.InsertKeyFrame(1f, y0 - rise + drop, Anim.EaseIn);
        v.StartAnimation("Offset.Y", y);
        if (spinDegrees != 0) v.StartAnimation("RotationAngleInDegrees", Anim.Scalar(seconds, Anim.Linear, 1f, spinDegrees));
        v.StartAnimation("Opacity", Anim.Scalar(seconds, Anim.Linear, 0.7f, 1f, 1f, 0f));
        batch.End();
        batch.Completed += (_, _) => Remove(layer, v);
    }

    /// <summary>Eased drift by <paramref name="delta"/> that fades in, holds and fades out (spores, Zzz, hearts, sparkles).</summary>
    public static void Drift(ContainerVisual layer, CompositionShape shape, float size, Vector2 center, Vector2 delta, double seconds,
        double delay = 0, float scaleFrom = 1f, float scaleTo = 1f, float spinDegrees = 0)
    {
        if (Live >= MaxLive) return;
        var c = Anim.C;
        var v = Spawn(layer, shape, size, center);
        v.Opacity = 0;
        v.Scale = new Vector3(scaleFrom, scaleFrom, 1);

        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        var offset = Anim.Vec3(seconds, Anim.EaseOut, (1f, v.Offset + new Vector3(delta, 0)));
        var opacity = Anim.Scalar(seconds, Anim.Linear, 0f, 0f, 0.18f, 1f, 0.7f, 1f, 1f, 0f);
        var scale = Anim.Vec3(seconds, Anim.EaseOut, (1f, new Vector3(scaleTo, scaleTo, 1)));
        foreach (var a in new KeyFrameAnimation[] { offset, opacity, scale })
        {
            a.DelayTime = TimeSpan.FromSeconds(delay);
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        v.StartAnimation("Offset", offset);
        v.StartAnimation("Opacity", opacity);
        if (scaleFrom != scaleTo) v.StartAnimation("Scale", scale);
        if (spinDegrees != 0)
        {
            var spin = Anim.Scalar(seconds, Anim.EaseOut, 1f, spinDegrees);
            spin.DelayTime = TimeSpan.FromSeconds(delay);
            v.StartAnimation("RotationAngleInDegrees", spin);
        }
        batch.End();
        batch.Completed += (_, _) => Remove(layer, v);
    }

    private static ShapeVisual Spawn(ContainerVisual layer, CompositionShape shape, float size, Vector2 center)
    {
        var v = Anim.C.CreateShapeVisual();
        v.Size = new Vector2(size, size);
        v.CenterPoint = new Vector3(size / 2, size / 2, 0);
        v.Offset = new Vector3(center.X - size / 2, center.Y - size / 2, 0);
        v.Shapes.Add(shape);
        layer.Children.InsertAtTop(v);
        Live++;
        return v;
    }

    private static void Remove(ContainerVisual layer, Visual v)
    {
        Live--;
        try
        {
            layer.Children.Remove(v);
            v.Dispose();
        }
        catch (Exception)
        {
            // The strip was torn down (rebuild or quit) while the particle was in flight.
        }
    }
}
