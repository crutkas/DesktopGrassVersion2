// Sky.cs - butterflies, fireflies and bird flocks for one strip.
//
// A butterfly's or firefly's whole life is a looping keyframe path plus a flap or blink loop, so once
// they are built the app never touches them. Birds are one-shot flybys (Poisson, ~15/hour) whose
// visuals are removed when their flight's animation batch completes.
using System;
using System.Collections.Generic;
using System.Numerics;
using DesktopGrass.Composition.Interop;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal sealed class Sky : IDisposable
{
    private const float BirdsPerHour = 15f;

    private static readonly (uint Wing, uint Edge, uint Spot)[] Butterflies =
    {
        (0xF39A2B, 0x2A2018, 0xFFFFFF), // monarch
        (0xFFE36E, 0x2A2018, 0x2A2018), // swallowtail
        (0xF7F6F0, 0x8A8A80, 0x2A2018), // cabbage white
        (0x4FA8F7, 0x16325C, 0xBFE3FF), // morpho
        (0xF7A1C4, 0xB0477A, 0xFFFFFF), // pink
    };

    private static CompositionPath? s_antennaL, s_antennaR, s_wingL, s_wingR;

    private readonly Strip _strip;
    private readonly Scheduler _scheduler;
    private readonly Rng _rng;
    private readonly ContainerVisual _layer;
    private ContainerVisual? _flutter;
    private TimerToken? _birdToken;
    private bool _birds, _disposed;

    public Sky(Strip strip, Scheduler scheduler, Rng rng)
    {
        _strip = strip;
        _scheduler = scheduler;
        _rng = rng;
        _layer = Anim.C.CreateContainerVisual();
        strip.Sky.Children.InsertAtTop(_layer);
    }

    private static Compositor C => Anim.C;
    private float GroundY => _strip.GroundY;

    public void Apply(SceneKind scene, MotionMode motion, bool enabled)
    {
        if (_disposed) return;
        bool full = enabled && motion == MotionMode.Full;
        bool flutter = full && scene == SceneKind.Meadow;
        if (flutter && _flutter == null) BuildFlutter();
        else if (!flutter && _flutter != null) RetireFlutter();

        bool birds = full && scene != SceneKind.Ocean;
        if (birds && !_birds) _birdToken = _scheduler.After(_rng.Range(20f, 70f), BirdTick);
        else if (!birds) _birdToken?.Cancel();
        _birds = birds;
    }

    // ---------------------------------------------------------------- butterflies and fireflies

    private void BuildFlutter()
    {
        _flutter = C.CreateContainerVisual();
        _flutter.Opacity = 0;
        _layer.Children.InsertAtTop(_flutter);
        float w = _strip.Width;
        int butterflies = Math.Clamp((int)(w / 900f) + _rng.Int(1, 3), 2, 6);
        int fireflies = Math.Clamp((int)(w / 700f) + _rng.Int(1, 4), 3, 8);
        for (int i = 0; i < butterflies; i++) _flutter.Children.InsertAtTop(Butterfly());
        for (int i = 0; i < fireflies; i++) _flutter.Children.InsertAtTop(Firefly());
        _flutter.StartAnimation("Opacity", Anim.Scalar(1.2, Anim.EaseInOut, 1f, 1f));
        Log.Write($"sky: {butterflies} butterflies, {fireflies} fireflies");
    }

    private void RetireFlutter()
    {
        var old = _flutter!;
        _flutter = null;
        var batch = C.CreateScopedBatch(CompositionBatchTypes.Animation);
        old.StartAnimation("Opacity", Anim.Scalar(0.8, Anim.EaseInOut, 1f, 0f));
        batch.End();
        batch.Completed += (_, _) => Remove(old);
    }

    private Visual Butterfly()
    {
        var (wing, edge, spot) = _rng.Pick(Butterflies);
        var v = Shapes.Visual(18, 16);
        v.CenterPoint = new Vector3(9, 8, 0);
        var fill = Shapes.Solid(wing);
        var stroke = Shapes.Solid(edge);
        var wings = C.CreateContainerShape();
        wings.CenterPoint = new Vector2(9, 8);
        wings.Shapes.Add(Wing(4.8f, 5.6f, 4.2f, 3.2f, -28f, fill, stroke));
        wings.Shapes.Add(Wing(13.2f, 5.6f, 4.2f, 3.2f, 28f, fill, stroke));
        wings.Shapes.Add(Wing(5.8f, 10.4f, 3f, 2.4f, 30f, fill, stroke));
        wings.Shapes.Add(Wing(12.2f, 10.4f, 3f, 2.4f, -30f, fill, stroke));
        wings.Shapes.Add(Shapes.Circle(3.5f, 4.8f, 0.8f, Shapes.Solid(spot)));
        wings.Shapes.Add(Shapes.Circle(14.5f, 4.8f, 0.8f, Shapes.Solid(spot)));
        v.Shapes.Add(wings);
        var ink = Shapes.Solid(0x2A2018);
        v.Shapes.Add(Shapes.Ellipse(9, 8.4f, 0.85f, 3.9f, ink));
        s_antennaL ??= new PathBuilder().MoveTo(8.6f, 4.8f).QuadTo(7.6f, 2.6f, 6.4f, 1.8f).Build();
        s_antennaR ??= new PathBuilder().MoveTo(9.4f, 4.8f).QuadTo(10.4f, 2.6f, 11.6f, 1.8f).Build();
        v.Shapes.Add(Shapes.Path(s_antennaL, null, ink, 0.5f));
        v.Shapes.Add(Shapes.Path(s_antennaR, null, ink, 0.5f));

        // Flap: a burst of quick wingbeats, then a short glide with wings open.
        double period = _rng.Range(2.2f, 3.2f);
        int beats = _rng.Int(5, 9);
        float burst = (float)(beats * 0.26 / period);
        var flap = C.CreateScalarKeyFrameAnimation();
        flap.InsertKeyFrame(0f, 1f);
        for (int k = 0; k < beats; k++)
        {
            float t0 = burst * k / beats;
            flap.InsertKeyFrame(t0 + burst / beats * 0.5f, 0.18f, Anim.EaseInOut);
            flap.InsertKeyFrame(t0 + burst / beats, 1f, Anim.EaseInOut);
        }
        flap.InsertKeyFrame(1f, 1f);
        flap.Duration = Anim.Sec(period);
        flap.IterationBehavior = AnimationIterationBehavior.Forever;
        wings.StartAnimation("Scale.X", flap);

        Wander(v, new Vector2(9, 8), _rng.Range(120f, _strip.Width - 120f), 220f, GroundY - 100f, GroundY - 34f, _rng.Range(18f, 32f), 9);
        return v;
    }

    private static CompositionSpriteShape Wing(float cx, float cy, float rx, float ry, float degrees, CompositionBrush fill, CompositionBrush stroke)
    {
        var s = Shapes.Ellipse(cx, cy, rx, ry, fill);
        s.StrokeBrush = stroke;
        s.StrokeThickness = 0.6f;
        s.CenterPoint = new Vector2(cx, cy);
        s.RotationAngleInDegrees = degrees;
        return s;
    }

    private Visual Firefly()
    {
        var v = Shapes.Visual(16, 16);
        v.CenterPoint = new Vector3(8, 8, 0);
        v.Shapes.Add(Shapes.Circle(8, 8, 7.5f, Shapes.Glow(Anim.Hex(0xF2E266), 150)));
        v.Shapes.Add(Shapes.Circle(8, 8, 1.4f, Shapes.Solid(0xFFF6A8)));
        double period = _rng.Range(1.4f, 2.6f);
        v.Opacity = 0.1f;
        v.StartAnimation("Opacity", Anim.ScalarLoop(period, Anim.EaseInOut, 0f, 0.1f, 0.12f, 1f, 0.55f, 1f, 0.68f, 0.1f, 1f, 0.1f));
        Wander(v, new Vector2(8, 8), _rng.Range(80f, _strip.Width - 80f), 70f, GroundY - 85f, GroundY - 36f, _rng.Range(4f, 10f), 6);
        return v;
    }

    /// <summary>A closed loop through random waypoints near <paramref name="homeX"/>, eased at each turn.</summary>
    private void Wander(Visual v, Vector2 center, float homeX, float spread, float yMin, float yMax, float speed, int points)
    {
        var pts = new Vector3[points];
        for (int i = 0; i < points; i++)
        {
            float x = Math.Clamp(homeX + _rng.Range(-spread, spread), 12f, _strip.Width - 12f);
            pts[i] = new Vector3(x - center.X, _rng.Range(yMin, yMax) - center.Y, 0);
        }
        var times = new double[points + 1];
        for (int i = 1; i <= points; i++) times[i] = times[i - 1] + Vector3.Distance(pts[i - 1], pts[i % points]) / speed + 0.2;
        double total = times[points];
        var a = C.CreateVector3KeyFrameAnimation();
        a.Duration = Anim.Sec(total);
        a.InsertKeyFrame(0f, pts[0]);
        for (int i = 1; i <= points; i++) a.InsertKeyFrame((float)(times[i] / total), pts[i % points], Anim.EaseInOut);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        v.Offset = pts[0];
        v.StartAnimation("Offset", a);
    }

    // ---------------------------------------------------------------- birds

    private void BirdTick()
    {
        if (_disposed || !_birds) return;
        if (_strip.Window.Visible) Flock();
        _birdToken = _scheduler.After(_rng.Exponential(BirdsPerHour / 3600f), BirdTick);
    }

    /// <summary>Sends a flock of 3-7 birds across the strip in a V (or a loose diagonal).</summary>
    public void Flock()
    {
        if (_disposed) return;
        int n = _rng.Int(3, 8);
        float dir = _rng.Sign();
        float speed = _rng.Range(65f, 95f);
        float y0 = GroundY - 30f - _rng.Range(78f, 96f);
        bool vee = _rng.Chance(0.7f);
        const float spacing = 9f;

        var flock = C.CreateContainerVisual();
        for (int k = 0; k < n; k++)
        {
            int rank = (k + 1) / 2;
            float side = k % 2 == 1 ? -1f : 1f;
            float bx = -dir * rank * spacing;
            float by = vee ? side * rank * spacing * 0.42f : rank * spacing * 0.42f;
            var bird = Bird(_rng.Range(0.85f, 1.15f));
            bird.Offset = new Vector3(bx - 8f + _rng.Range(-1.5f, 1.5f), by - 5f + _rng.Range(-1f, 1f), 0);
            flock.Children.InsertAtTop(bird);
        }
        float x0 = dir > 0 ? -70f : _strip.Width + 70f;
        float x1 = dir > 0 ? _strip.Width + 70f : -70f;
        double seconds = MathF.Abs(x1 - x0) / speed;
        flock.Offset = new Vector3(x0, y0, 0);
        _layer.Children.InsertAtTop(flock);

        var batch = C.CreateScopedBatch(CompositionBatchTypes.Animation);
        flock.StartAnimation("Offset", Anim.Vec3(seconds, Anim.Linear, (1f, new Vector3(x1, y0 + _rng.Range(-10f, 10f), 0))));
        batch.End();
        batch.Completed += (_, _) => Remove(flock);
        Log.Write($"birds: flock of {n} heading {(dir > 0 ? "east" : "west")}");
    }

    private Visual Bird(float scale)
    {
        var v = Shapes.Visual(16, 10);
        v.CenterPoint = new Vector3(8, 5, 0);
        v.Scale = new Vector3(scale, scale, 1);
        var ink = Shapes.Solid(0x1A1610);
        s_wingL ??= new PathBuilder().MoveTo(8f, 5f).QuadTo(5.6f, 1.4f, 1.2f, 3.4f).Build();
        s_wingR ??= new PathBuilder().MoveTo(8f, 5f).QuadTo(10.4f, 1.4f, 14.8f, 3.4f).Build();
        var wings = C.CreateContainerShape();
        wings.CenterPoint = new Vector2(8, 5);
        // A faint light rim keeps the silhouettes visible over dark wallpapers too.
        var rim = Shapes.Solid(0xFFFFFF, 80);
        wings.Shapes.Add(Shapes.Path(s_wingL, null, rim, 2.6f));
        wings.Shapes.Add(Shapes.Path(s_wingR, null, rim, 2.6f));
        wings.Shapes.Add(Shapes.Path(s_wingL, null, ink, 1.25f));
        wings.Shapes.Add(Shapes.Path(s_wingR, null, ink, 1.25f));
        v.Shapes.Add(wings);
        var body = Shapes.Ellipse(8, 5.3f, 1.5f, 0.95f, ink);
        body.StrokeBrush = rim;
        body.StrokeThickness = 0.7f;
        v.Shapes.Add(body);

        // Started before the flock's scoped batch, so these infinite loops don't hold it open.
        var flap = Anim.ScalarLoop(_rng.Range(0.42f, 0.6f), Anim.EaseInOut, 0f, 1f, 0.5f, -0.55f, 1f, 1f);
        flap.DelayTime = TimeSpan.FromSeconds(_rng.Range(0f, 0.4f));
        flap.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        wings.StartAnimation("Scale.Y", flap);
        return v;
    }

    private void Remove(Visual v)
    {
        try
        {
            Shapes.Discard(v);
        }
        catch (Exception)
        {
            // The strip was torn down while it was still flying.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _birdToken?.Cancel();
        try
        {
            Shapes.Discard(_layer);
        }
        catch (Exception)
        {
        }
    }

    public static void ResetCache()
    {
        s_antennaL = s_antennaR = s_wingL = s_wingR = null;
    }
}
