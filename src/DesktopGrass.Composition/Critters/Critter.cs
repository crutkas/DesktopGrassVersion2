// Critter.cs - base class for the ground animals. Each critter is one ShapeVisual "rig" of composition
// shapes; gaits and poses are keyframe animations that DWM runs. The app only runs the state machine
// (one timer per transition) and mirrors the walk as a piecewise-linear x(t) for hit tests, so a walking
// sheep costs the app nothing per frame.
using System;
using System.Collections.Generic;
using System.Numerics;
using DesktopGrass.Composition.Interop;
using Windows.UI;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal enum Mood { Walk, Graze, Idle, Sleep, Hop, Flee, Greet, Pounce, Curl, Doze, Stand }

internal abstract class Critter : IDisposable
{
    /// <summary>How far the feet sit below the ground line, so the turf tucks them in.</summary>
    protected const float Sink = 1.5f;
    private const float TagRadius = 50f;

    protected readonly Herd Herd;
    protected readonly Rng Rng;
    public readonly string Species;
    public readonly string Name;
    public readonly float Speed;
    public readonly float HalfWidth, BodyHeight;
    public readonly ContainerVisual Root;
    protected readonly ShapeVisual Art;
    protected readonly float Cx, Fy;
    private readonly ShapeVisual _shadow;
    private readonly ShapeVisual _tag;
    private readonly List<(double T, float X)> _track = new();
    private TimerToken? _stateToken, _zzzToken;
    private bool _tagShown;
    private double _greetedAt = -1000, _turnedAt = -1000;

    /// <summary>Eye group (CenterPoint on the eye) used for blinks, sleep and peeking at the cursor.</summary>
    protected CompositionContainerShape? Eyes;

    public Mood Mood { get; private set; } = Mood.Idle;
    public float Facing { get; private set; }
    protected bool Disposed { get; private set; }

    protected float BaseY => -Fy + Sink;
    protected bool Full => Herd.Motion == MotionMode.Full;
    protected static Compositor C => Anim.C;

    /// <summary>Click distance (DIPs) that provokes a reaction.</summary>
    protected abstract float ReactRadius { get; }
    /// <summary>Where sleepy Zzz rise from, relative to the feet when facing right (y is up-negative).</summary>
    protected abstract Vector2 HeadAnchor { get; }
    protected virtual float ZzzScale => 1f;

    protected abstract void Pose(Mood mood, Mood previous);
    protected abstract void NextMood();
    protected abstract void React(Vector2 click, bool direct);

    protected Critter(Herd herd, Rng rng, string species, string name, float x, float speed,
        float artWidth, float artHeight, float halfWidth, float bodyHeight)
    {
        Herd = herd;
        Rng = rng;
        Species = species;
        Name = name;
        Speed = speed;
        HalfWidth = halfWidth;
        BodyHeight = bodyHeight;
        Cx = artWidth / 2;
        Fy = artHeight - 3;
        Facing = rng.Sign();

        Root = C.CreateContainerVisual();
        Root.Offset = new Vector3(x, herd.GroundY, 0);
        _track.Add((Clock.Now, x));

        float rx = halfWidth + 1.5f;
        _shadow = Shapes.Visual(rx * 2 + 2, 7, new Vector2(-rx - 1, -3.5f));
        _shadow.CenterPoint = new Vector3(rx + 1, 3.5f, 0);
        _shadow.Shapes.Add(Shapes.ContactShadow(rx + 1, 3.5f, rx, 2.6f, 90));
        Root.Children.InsertAtTop(_shadow);

        Art = Shapes.Visual(artWidth, artHeight, new Vector2(-Cx, BaseY));
        Art.CenterPoint = new Vector3(Cx, Fy, 0);
        Art.Scale = new Vector3(Facing, 1, 1);
        Root.Children.InsertAtTop(Art);

        _tag = BuildTag();
        Root.Children.InsertAtTop(_tag);
    }

    private ShapeVisual BuildTag()
    {
        var text = TextPath.Create(Name, 8.5f);
        float h = MathF.Max(text.Height, 6.5f) + 6.5f, w = text.Width + 12f;
        var v = Shapes.Visual(w, h, new Vector2(-w / 2, -BodyHeight - 7f - h));
        var pill = Shapes.RoundRect(0.5f, 0.5f, w - 1f, h - 1f, (h - 1f) / 2, Shapes.Solid(0x1B2414, 215));
        pill.StrokeBrush = Shapes.Solid(0xFFFFFF, 70);
        pill.StrokeThickness = 0.7f;
        v.Shapes.Add(pill);
        var label = Shapes.Path(text.Path, Shapes.Solid(0xFFFDF5));
        label.Offset = new Vector2((w - text.Width) / 2, (h - text.Height) / 2);
        v.Shapes.Add(label);
        v.Opacity = 0;
        return v;
    }

    /// <summary>Art-space point from feet-relative coordinates (x forward, y up-negative).</summary>
    protected Vector2 P(float dx, float dy) => new(Cx + dx, Fy + dy);

    // ---------------------------------------------------------------- position mirror

    public float X => XAt(Clock.Now);

    public float XAt(double t)
    {
        if (t <= _track[0].T) return _track[0].X;
        for (int i = 1; i < _track.Count; i++)
        {
            var (t1, x1) = _track[i];
            if (t > t1) continue;
            var (t0, x0) = _track[i - 1];
            return t1 - t0 < 1e-6 ? x1 : x0 + (x1 - x0) * (float)((t - t0) / (t1 - t0));
        }
        return _track[^1].X;
    }

    protected bool Moving => _track.Count > 1 && _track[^1].T > Clock.Now;

    protected float ClampX(float x) => Math.Clamp(x, Herd.MinX + HalfWidth, Herd.MaxX - HalfWidth);

    /// <summary>Moves through (seconds-from-now, x) waypoints; DWM interpolates linearly between them.</summary>
    protected void Travel(params (double Dt, float X)[] legs)
    {
        double now = Clock.Now;
        float start = X;
        _track.Clear();
        _track.Add((now, start));
        double total = Math.Max(0.001, legs[^1].Dt);
        var a = C.CreateScalarKeyFrameAnimation();
        a.Duration = Anim.Sec(total);
        foreach (var (dt, x) in legs)
        {
            _track.Add((now + dt, x));
            a.InsertKeyFrame((float)Math.Clamp(dt / total, 0, 1), x, Anim.Linear);
        }
        Root.StartAnimation("Offset.X", a);
    }

    protected void Halt()
    {
        float x = X;
        Root.StopAnimation("Offset.X");
        Root.Offset = new Vector3(x, Herd.GroundY, 0);
        _track.Clear();
        _track.Add((Clock.Now, x));
    }

    /// <summary>Starts a walk of about <paramref name="seconds"/>; returns its actual duration (edges shorten it).</summary>
    protected double WalkFor(double seconds, float speed, float? direction = null)
    {
        float x = X;
        float dir = direction ?? (Rng.Chance(0.3f) ? -Facing : Facing);
        float target = ClampX(x + dir * speed * (float)seconds);
        if (MathF.Abs(target - x) < 16f)
        {
            dir = -dir;
            target = ClampX(x + dir * speed * (float)seconds);
        }
        Face(dir);
        double dt = Math.Max(0.2, MathF.Abs(target - x) / speed);
        Travel((dt, target));
        return dt;
    }

    protected void Face(float dir, double seconds = 0.2)
    {
        float f = dir < 0 ? -1f : 1f;
        if (f == Facing) return;
        Facing = f;
        _turnedAt = Clock.Now;
        Art.StartAnimation("Scale.X", Anim.Scalar(seconds, Anim.EaseInOut, 1f, f));
    }

    // ---------------------------------------------------------------- state machine

    public void Start()
    {
        if (Full) Enter(Mood.Idle, Rng.Range(0.3f, 2.5f));
        else Enter(Mood.Doze, 0);
    }

    /// <summary>Switches mood; <paramref name="then"/> (or the species' NextMood) runs when it expires.</summary>
    protected void Enter(Mood mood, double seconds, Action? then = null)
    {
        if (Disposed) return;
        _stateToken?.Cancel();
        _stateToken = null;
        var previous = Mood;
        Mood = mood;
        if (mood is not (Mood.Walk or Mood.Hop or Mood.Flee or Mood.Pounce))
        {
            if (Moving) Halt();
            Ground();
        }
        Pose(mood, previous);
        if (mood == Mood.Sleep) StartZzz();
        else StopZzz();
        if (seconds > 0)
        {
            _stateToken = Herd.Scheduler.After(seconds, () =>
            {
                if (Disposed) return;
                if (then != null) then();
                else Next();
            });
        }
        Herd.Changed(this);
    }

    protected void Next()
    {
        if (!Full) Enter(Mood.Doze, 0);
        else NextMood();
    }

    public void SetMotion(MotionMode mode)
    {
        if (mode == MotionMode.Calm)
        {
            if (Mood != Mood.Doze) Enter(Mood.Doze, 0);
        }
        else if (Mood == Mood.Doze)
        {
            Enter(Mood.Idle, Rng.Range(0.4f, 2f));
        }
    }

    public bool CanGreet => Full && !Disposed && (Mood is Mood.Walk or Mood.Idle or Mood.Graze) && Clock.Now - _greetedAt > 25;

    public void Greet(Critter other, double seconds)
    {
        _greetedAt = Clock.Now;
        Halt();
        Face(other.X - X, 0.25);
        Enter(Mood.Greet, seconds);
    }

    // ---------------------------------------------------------------- input

    public void Hover(Vector2? cursor)
    {
        bool near = false;
        if (cursor is { } p)
        {
            near = Vector2.Distance(p, new Vector2(X, Herd.GroundY - BodyHeight * 0.5f)) <= TagRadius;
            if (near) Curious(p);
        }
        if (near == _tagShown) return;
        _tagShown = near;
        if (near)
        {
            _tag.StartAnimation("Opacity", Anim.Scalar(0.2, Anim.EaseOut, 1f, 1f));
        }
        else
        {
            var fade = Anim.Scalar(1.5, Anim.EaseInOut, 1f, 0f);
            fade.DelayTime = TimeSpan.FromSeconds(0.3);
            _tag.StartAnimation("Opacity", fade);
        }
        if (Mood == Mood.Doze) SetEyes(near, 0.25);
    }

    private void Curious(Vector2 cursor)
    {
        if (Mood is not (Mood.Idle or Mood.Graze) || Clock.Now - _turnedAt < 1.0) return;
        float dx = cursor.X - X;
        if (MathF.Abs(dx) > 4f && MathF.Sign(dx) != (int)Facing) Face(dx, 0.25);
    }

    /// <summary>Reacts to a click nearby; returns true when the click landed on the critter itself.</summary>
    public bool Click(Vector2 p)
    {
        if (Disposed) return false;
        float dx = p.X - X;
        float hy = Herd.GroundY - p.Y;
        bool direct = MathF.Abs(dx) <= HalfWidth + 3f && hy >= -8f && hy <= BodyHeight + 4f;
        if (direct || (MathF.Abs(dx) <= ReactRadius && hy <= 80f)) React(p, direct);
        return direct;
    }

    // ---------------------------------------------------------------- shared pose helpers

    protected static void To(CompositionObject target, string property, float value, double seconds = 0.25, CompositionEasingFunction? easing = null) =>
        target.StartAnimation(property, Anim.Scalar(seconds, easing ?? Anim.EaseInOut, 1f, value));

    protected static void Loop(CompositionObject target, string property, double period, params float[] pairs) =>
        target.StartAnimation(property, Anim.ScalarLoop(period, Anim.EaseInOut, pairs));

    /// <summary>Trot: legs ordered near-back, far-back, far-front, near-front swing in diagonal pairs.</summary>
    protected static void Swing(IReadOnlyList<CompositionShape> gait, double period, float degrees)
    {
        for (int i = 0; i < gait.Count; i++)
        {
            float a = (i % 2 == 0 ? 1f : -1f) * degrees;
            gait[i].StartAnimation("RotationAngleInDegrees", Anim.ScalarLoop(period, Anim.EaseInOut, 0f, a, 0.5f, -a, 1f, a));
        }
    }

    protected static void Still(IReadOnlyList<CompositionShape> legs, double seconds = 0.2)
    {
        foreach (var leg in legs) To(leg, "RotationAngleInDegrees", 0f, seconds);
    }

    protected static CompositionContainerShape Group(Vector2 pivot)
    {
        var g = C.CreateContainerShape();
        g.CenterPoint = pivot;
        return g;
    }

    /// <summary>Rounded leg hanging from a hip at (dx, hipDy); rotates around the hip.</summary>
    protected CompositionSpriteShape Leg(float dx, float hipDy, float width, float length, Color color)
    {
        var top = P(dx, hipDy);
        var leg = Shapes.RoundRect(top.X - width / 2, top.Y - width / 2, width, length + width / 2, width / 2, Shapes.Solid(color));
        leg.CenterPoint = top;
        return leg;
    }

    protected static CompositionSpriteShape Rotated(CompositionSpriteShape shape, Vector2 pivot, float degrees)
    {
        shape.CenterPoint = pivot;
        shape.RotationAngleInDegrees = degrees;
        return shape;
    }

    // Drawing helpers in feet-relative coordinates (x forward, y up-negative).

    protected CompositionSpriteShape Oval(float dx, float dy, float rx, float ry, Color color) =>
        Shapes.Ellipse(Cx + dx, Fy + dy, rx, ry, Shapes.Solid(color));

    /// <summary>One filled shape made of overlapping ellipses (a wool cloud, a body with a tail...).</summary>
    protected CompositionSpriteShape Blob(Color color, params (float Dx, float Dy, float Rx, float Ry)[] parts)
    {
        var pb = new PathBuilder(winding: true);
        foreach (var (dx, dy, rx, ry) in parts) pb.Ellipse(Cx + dx, Fy + dy, rx, ry);
        return Shapes.Path(pb, Shapes.Solid(color));
    }

    protected CompositionSpriteShape Poly(Color color, params float[] dxdy)
    {
        var xy = new float[dxdy.Length];
        for (int i = 0; i + 1 < dxdy.Length; i += 2)
        {
            xy[i] = Cx + dxdy[i];
            xy[i + 1] = Fy + dxdy[i + 1];
        }
        return Shapes.Path(new PathBuilder(winding: true).Polygon(xy), Shapes.Solid(color));
    }

    protected CompositionSpriteShape Stroke(float dx1, float dy1, float dx2, float dy2, Color color, float width) =>
        Shapes.Line(Cx + dx1, Fy + dy1, Cx + dx2, Fy + dy2, Shapes.Solid(color), width);

    /// <summary>Stroked cubic curve: a start point, then (control1, control2, end) triples.</summary>
    protected CompositionSpriteShape Curve(Color color, float width, params float[] p)
    {
        var pb = new PathBuilder().MoveTo(Cx + p[0], Fy + p[1]);
        for (int i = 2; i + 5 < p.Length; i += 6)
            pb.CubicTo(Cx + p[i], Fy + p[i + 1], Cx + p[i + 2], Fy + p[i + 3], Cx + p[i + 4], Fy + p[i + 5]);
        return Shapes.Path(pb, null, Shapes.Solid(color), width);
    }

    protected void SetEyes(bool open, double seconds = 0.15) =>
        Eyes?.StartAnimation("Scale.Y", Anim.Scalar(seconds, Anim.EaseInOut, 1f, open ? 1f : 0.12f));

    protected void StartBlinking()
    {
        if (Eyes == null) return;
        if (!Full)
        {
            SetEyes(true);
            return;
        }
        double period = Rng.Range(2.8f, 5.2f);
        float k = (float)(0.09 / period);
        Eyes.StartAnimation("Scale.Y", Anim.ScalarLoop(period, Anim.EaseInOut, 0f, 1f, 1f - 2 * k, 1f, 1f - k, 0.1f, 1f, 1f));
    }

    /// <summary>Settles the whole rig on the ground (ends any hop).</summary>
    protected void Ground(double seconds = 0.15)
    {
        Art.StartAnimation("Offset.Y", Anim.Scalar(seconds, Anim.EaseIn, 1f, BaseY));
        Art.StartAnimation("Scale.Y", Anim.Scalar(seconds, Anim.EaseOut, 1f, 1f));
        _shadow.StartAnimation("Scale", Anim.Vec3(seconds, Anim.EaseOut, (1f, Vector3.One)));
    }

    /// <summary>
    /// <paramref name="count"/> hops of <paramref name="hopSeconds"/> each, separated by ground pauses of
    /// <paramref name="gapSeconds"/>: parabolic lift, squash-and-stretch, and a shadow that shrinks in the air.
    /// </summary>
    protected void HopArc(double hopSeconds, float height, int count = 1, double gapSeconds = 0)
    {
        double period = hopSeconds + gapSeconds;
        float up = (float)(hopSeconds / period);
        bool gap = up < 0.999f;

        var y = C.CreateScalarKeyFrameAnimation();
        y.InsertKeyFrame(0f, BaseY);
        y.InsertKeyFrame(up * 0.5f, BaseY - height, Anim.EaseOut);
        y.InsertKeyFrame(up, BaseY, Anim.EaseIn);
        if (gap) y.InsertKeyFrame(1f, BaseY);

        var sy = C.CreateScalarKeyFrameAnimation();
        sy.InsertKeyFrame(0f, 1f);
        sy.InsertKeyFrame(up * 0.1f, 0.9f, Anim.EaseOut);
        sy.InsertKeyFrame(up * 0.3f, 1.08f, Anim.EaseOut);
        sy.InsertKeyFrame(up * 0.75f, 1.02f, Anim.Linear);
        sy.InsertKeyFrame(up * 0.95f, 0.9f, Anim.EaseIn);
        if (gap) sy.InsertKeyFrame(up + (1f - up) * 0.5f, 1f, Anim.EaseOut);
        sy.InsertKeyFrame(1f, 1f, Anim.EaseOut);

        var sh = C.CreateVector3KeyFrameAnimation();
        sh.InsertKeyFrame(0f, Vector3.One);
        sh.InsertKeyFrame(up * 0.5f, new Vector3(0.66f, 0.66f, 1), Anim.EaseOut);
        sh.InsertKeyFrame(up, Vector3.One, Anim.EaseIn);
        if (gap) sh.InsertKeyFrame(1f, Vector3.One);

        foreach (var a in new KeyFrameAnimation[] { y, sy, sh })
        {
            a.Duration = Anim.Sec(period);
            a.IterationBehavior = AnimationIterationBehavior.Count;
            a.IterationCount = Math.Max(1, count);
        }
        Art.StartAnimation("Offset.Y", y);
        Art.StartAnimation("Scale.Y", sy);
        _shadow.StartAnimation("Scale", sh);
    }

    /// <summary>World position of a feet-relative point (accounts for facing).</summary>
    protected Vector2 World(Vector2 local) => new(X + Facing * local.X, Herd.GroundY + local.Y);

    protected void PopAbove(CompositionShape glyph, float size, float rise = 12f) =>
        Herd.Pop(glyph, size, World(HeadAnchor + new Vector2(7f, 3f)), new Vector2(Facing * 4f, -rise));

    // ---------------------------------------------------------------- sleep

    private void StartZzz()
    {
        StopZzz();
        if (Full) _zzzToken = Herd.Scheduler.After(Rng.Range(0.5f, 1.2f), SpawnZ);
    }

    private void SpawnZ()
    {
        if (Disposed || Mood != Mood.Sleep) return;
        float s = ZzzScale;
        Fx.Drift(Herd.Strip.Fx, Glyphs.Z(9f * s), 9f * s, World(HeadAnchor), new Vector2(Facing * 4f, -15f) * s, 2.4,
            scaleFrom: 0.5f, scaleTo: 1.1f);
        _zzzToken = Herd.Scheduler.After(Rng.Range(1.4f, 2.0f), SpawnZ);
    }

    private void StopZzz()
    {
        _zzzToken?.Cancel();
        _zzzToken = null;
    }

    /// <summary>Cancels pending transitions (the herd is fading out).</summary>
    public void Stop()
    {
        _stateToken?.Cancel();
        StopZzz();
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        Stop();
        try
        {
            // Closes every shape too, which is what stops the gait, blink and tail loops.
            Shapes.Discard(Root);
        }
        catch (Exception)
        {
            // Already gone with its strip.
        }
    }
}

/// <summary>Little pop-up glyphs (Zzz, hearts, surprise) sized to fit a size x size box for Fx.Drift.</summary>
internal static class Glyphs
{
    private static CompositionPath? s_z, s_heart, s_bang;

    private static CompositionContainerShape Box(float size)
    {
        var c = Anim.C.CreateContainerShape();
        c.Scale = new Vector2(size / 10f);
        return c;
    }

    public static CompositionShape Z(float size)
    {
        s_z ??= new PathBuilder().MoveTo(2.6f, 2.8f).LineTo(7.4f, 2.8f).LineTo(2.6f, 7.2f).LineTo(7.4f, 7.2f).Build();
        var box = Box(size);
        box.Shapes.Add(Shapes.Path(s_z, null, Shapes.Solid(0x22303A, 190), 2.6f));
        box.Shapes.Add(Shapes.Path(s_z, null, Shapes.Solid(0xFFFFFF), 1.3f));
        return box;
    }

    public static CompositionShape Heart(float size)
    {
        s_heart ??= new PathBuilder(winding: true)
            .MoveTo(5f, 8.8f).CubicTo(-0.6f, 4.9f, 1.4f, -0.4f, 5f, 2.7f).CubicTo(8.6f, -0.4f, 10.6f, 4.9f, 5f, 8.8f).Close().Build();
        var box = Box(size);
        box.Shapes.Add(Shapes.Path(s_heart, Shapes.Solid(0xF0567A), Shapes.Solid(0xFFFFFF, 225), 0.9f));
        return box;
    }

    public static CompositionShape Bang(float size)
    {
        s_bang ??= new PathBuilder(winding: true).RoundRect(4.1f, 0.6f, 1.8f, 5.8f, 0.9f).Circle(5f, 8.5f, 1.05f).Build();
        var box = Box(size);
        box.Shapes.Add(Shapes.Path(s_bang, Shapes.Solid(0xFFC83D), Shapes.Solid(0x3A2A12, 230), 0.8f));
        return box;
    }

    public static void Reset()
    {
        s_z = s_heart = s_bang = null;
    }
}
