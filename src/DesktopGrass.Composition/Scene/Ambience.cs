// Ambience.cs - per-scene weather and props for one strip: Winter snow and pines, Autumn leaves and
// pumpkins, Ocean bubbles, fish and coral, Desert tumbleweeds and cacti.
//
// Weather particles are small static-content visuals on seamless keyframe cycles that start at random
// phases, so DWM only re-composes transforms. Props are static shapes behind the grass. In Calm mode the
// weather fades out entirely (nothing left animating); props stay.
using System;
using System.Numerics;
using DesktopGrass.Composition.Interop;
using Windows.UI;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal sealed class Ambience : IDisposable
{
    private readonly Strip _strip;
    private readonly Scheduler _scheduler;
    private readonly Rng _rng;
    private ContainerVisual? _weather, _swimmers, _props;
    private TimerToken? _tumbleToken;
    private SceneKind _scene;
    private MotionMode _motion;
    private bool _applied, _disposed;

    public Ambience(Strip strip, Scheduler scheduler, Rng rng)
    {
        _strip = strip;
        _scheduler = scheduler;
        _rng = rng;
    }

    private static Compositor C => Anim.C;
    private float W => _strip.Width;
    private float GroundY => _strip.GroundY;

    public void Apply(SceneKind scene, MotionMode motion)
    {
        if (_disposed) return;
        bool sceneChanged = !_applied || scene != _scene;
        bool motionChanged = !_applied || motion != _motion;
        _applied = true;
        _scene = scene;
        _motion = motion;

        if (sceneChanged)
        {
            Retire(ref _props);
            _props = BuildProps(scene);
        }
        if (sceneChanged || motionChanged)
        {
            Retire(ref _weather);
            Retire(ref _swimmers);
            _tumbleToken?.Cancel();
            if (motion == MotionMode.Full) BuildWeather(scene);
        }
    }

    private ContainerVisual NewLayer(ContainerVisual parent, bool atBottom = false)
    {
        var v = C.CreateContainerVisual();
        v.Opacity = 0;
        if (atBottom) parent.Children.InsertAtBottom(v);
        else parent.Children.InsertAtTop(v);
        v.StartAnimation("Opacity", Anim.Scalar(1.2, Anim.EaseInOut, 1f, 1f));
        return v;
    }

    private void Retire(ref ContainerVisual? layer)
    {
        var old = layer;
        layer = null;
        if (old == null) return;
        var batch = C.CreateScopedBatch(CompositionBatchTypes.Animation);
        old.StartAnimation("Opacity", Anim.Scalar(0.9, Anim.EaseInOut, 1f, 0f));
        batch.End();
        batch.Completed += (_, _) =>
        {
            try
            {
                Shapes.Discard(old);
            }
            catch (Exception)
            {
            }
        };
    }

    // ---------------------------------------------------------------- weather

    private void BuildWeather(SceneKind scene)
    {
        switch (scene)
        {
            case SceneKind.Winter:
                _weather = NewLayer(_strip.Sky);
                for (int i = 0, n = (int)(W / 26f); i < n; i++) _weather.Children.InsertAtTop(Snowflake());
                break;
            case SceneKind.Autumn:
                _weather = NewLayer(_strip.Sky);
                for (int i = 0, n = Math.Max(6, (int)(W / 140f)); i < n; i++) _weather.Children.InsertAtTop(Leaf());
                break;
            case SceneKind.Ocean:
                _weather = NewLayer(_strip.Sky);
                for (int i = 0, n = (int)(W / 80f); i < n; i++) _weather.Children.InsertAtTop(Bubble());
                _swimmers = NewLayer(_strip.Critters);
                for (int i = 0, n = Math.Clamp((int)(W / 700f) + 1, 2, 6); i < n; i++) _swimmers.Children.InsertAtTop(Fish());
                break;
            case SceneKind.Desert:
                _swimmers = NewLayer(_strip.Critters);
                _tumbleToken = _scheduler.After(_rng.Range(3f, 12f), TumbleTick);
                break;
        }
    }

    /// <summary>Linear travel from <paramref name="from"/> to <paramref name="to"/>, looping forever, starting at <paramref name="phase"/> of the cycle.</summary>
    private static ScalarKeyFrameAnimation Cycle(double seconds, float from, float to, float phase)
    {
        var a = C.CreateScalarKeyFrameAnimation();
        a.Duration = Anim.Sec(seconds);
        float start = from + (to - from) * phase, cut = 1f - phase;
        a.InsertKeyFrame(0f, start);
        if (cut > 0.001f) a.InsertKeyFrame(cut, to, Anim.Linear);
        if (cut < 0.998f)
        {
            a.InsertKeyFrame(cut + 0.001f, from, Anim.Linear);
            a.InsertKeyFrame(1f, start, Anim.Linear);
        }
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        return a;
    }

    private static ScalarKeyFrameAnimation Sway(double period, float center, float amplitude) =>
        Anim.ScalarLoop(period, Anim.EaseInOut, 0f, center - amplitude, 0.5f, center + amplitude, 1f, center - amplitude);

    private Visual Snowflake()
    {
        float r = _rng.Range(0.8f, 2.1f);
        var v = Shapes.Visual(6, 6);
        v.Shapes.Add(Shapes.Circle(3, 3, r, Shapes.Solid(0xFFFFFF, (byte)_rng.Int(170, 250))));
        float x = _rng.Range(0f, W);
        double fall = (GroundY + 8f) / _rng.Range(10f, 24f);
        v.StartAnimation("Offset.Y", Cycle(fall, -6f, GroundY - 5f, _rng.Next()));
        v.StartAnimation("Offset.X", Sway(_rng.Range(2.5f, 5f), x, _rng.Range(3f, 10f) * _rng.Sign()));
        return v;
    }

    private static readonly uint[] LeafColors = { 0xD96B0C, 0xE89A3C, 0xC23E12, 0xD9A65C, 0xB54D1E, 0xF2C14E };
    private static CompositionPath? s_leaf, s_vein;

    private Visual Leaf()
    {
        s_leaf ??= new PathBuilder(winding: true).MoveTo(6f, 0.8f).CubicTo(10.4f, 3f, 10.2f, 8.4f, 6f, 11.2f).CubicTo(1.8f, 8.4f, 1.6f, 3f, 6f, 0.8f).Close().Build();
        s_vein ??= new PathBuilder().MoveTo(6f, 1.8f).LineTo(6f, 10.4f).Build();
        var color = Anim.Hex(_rng.Pick(LeafColors));
        var v = Shapes.Visual(12, 12);
        v.CenterPoint = new Vector3(6, 6, 0);
        v.Shapes.Add(Shapes.Path(s_leaf, Shapes.Solid(color)));
        v.Shapes.Add(Shapes.Path(s_vein, null, Shapes.Solid(Anim.Darken(color, 0.3f)), 0.6f));
        float scale = _rng.Range(0.75f, 1.15f);
        v.Scale = new Vector3(scale, scale, 1);
        float x = _rng.Range(0f, W);
        double period = _rng.Range(3f, 5.5f);
        double fall = GroundY / _rng.Range(8f, 16f);
        v.StartAnimation("Offset.Y", Cycle(fall, -14f, GroundY - 8f, _rng.Next()));
        v.StartAnimation("Offset.X", Sway(period, x, _rng.Range(10f, 28f) * _rng.Sign()));
        v.StartAnimation("RotationAngleInDegrees", Sway(period, _rng.Range(-30f, 30f), 50f));
        return v;
    }

    private Visual Bubble()
    {
        float r = _rng.Range(1.4f, 3.4f);
        var v = Shapes.Visual(10, 10);
        var ring = Shapes.Circle(5, 5, r, Shapes.Solid(0xE6FFFB, 45));
        ring.StrokeBrush = Shapes.Solid(0xE6FFFB, 210);
        ring.StrokeThickness = 0.7f;
        v.Shapes.Add(ring);
        v.Shapes.Add(Shapes.Circle(5 - r * 0.4f, 5 - r * 0.4f, MathF.Max(0.45f, r * 0.22f), Shapes.Solid(0xFFFFFF, 230)));
        float x = _rng.Range(0f, W);
        double rise = (GroundY + 10f) / _rng.Range(10f, 22f);
        v.StartAnimation("Offset.Y", Cycle(rise, GroundY - 8f, -12f, _rng.Next()));
        v.StartAnimation("Offset.X", Sway(_rng.Range(1.2f, 2.2f), x, _rng.Range(1.5f, 3.5f) * _rng.Sign()));
        return v;
    }

    private static readonly uint[] FishColors = { 0xFF8A65, 0xFFD54F, 0x4FC3F7, 0xF06292, 0x9575CD };

    private Visual Fish()
    {
        var body = Anim.Hex(_rng.Pick(FishColors));
        var v = Shapes.Visual(22, 12);
        v.CenterPoint = new Vector3(11, 6, 0);
        var tail = C.CreateContainerShape();
        tail.CenterPoint = new Vector2(6.5f, 6f);
        tail.Shapes.Add(Shapes.Path(new PathBuilder(winding: true).Polygon(6.5f, 6f, 1f, 1.8f, 1f, 10.2f), Shapes.Solid(Anim.Darken(body, 0.18f))));
        v.Shapes.Add(tail);
        v.Shapes.Add(Shapes.Path(new PathBuilder(winding: true).Polygon(10.6f, 2.4f, 13.8f, 0.6f, 15f, 2.8f), Shapes.Solid(Anim.Darken(body, 0.12f))));
        v.Shapes.Add(Shapes.Ellipse(12f, 6f, 7f, 4.2f, Shapes.Solid(body)));
        v.Shapes.Add(Shapes.Ellipse(13f, 7.6f, 4.6f, 1.6f, Shapes.Solid(Anim.Lighten(body, 0.3f))));
        if (_rng.Chance(0.4f)) v.Shapes.Add(Shapes.RoundRect(10.4f, 2.1f, 1.6f, 7.8f, 0.8f, Shapes.Solid(0xFFFFFF, 220)));
        v.Shapes.Add(Shapes.Circle(16.4f, 5.2f, 1.05f, Shapes.Solid(0xFFFFFF)));
        v.Shapes.Add(Shapes.Circle(16.7f, 5.2f, 0.58f, Shapes.Solid(0x10202A)));

        float range = _rng.Range(180f, 520f);
        float x0 = _rng.Range(20f, MathF.Max(40f, W - range - 20f));
        double period = 2 * range / _rng.Range(14f, 30f);
        float y = GroundY - _rng.Range(14f, 70f);
        v.Offset = new Vector3(x0 - 11f, y - 6f, 0);
        v.StartAnimation("Offset.X", Anim.ScalarLoop(period, Anim.EaseInOut, 0f, x0 - 11f, 0.5f, x0 + range - 11f, 1f, x0 - 11f));
        v.StartAnimation("Scale.X", Anim.ScalarLoop(period, Anim.EaseInOut, 0f, 1f, 0.47f, 1f, 0.53f, -1f, 0.97f, -1f, 1f, 1f));
        v.StartAnimation("Offset.Y", Sway(_rng.Range(2.5f, 4f), y - 6f, 2f));
        tail.StartAnimation("RotationAngleInDegrees", Sway(_rng.Range(0.4f, 0.6f), 0f, 16f));
        return v;
    }

    private void TumbleTick()
    {
        if (_disposed || _swimmers == null || _scene != SceneKind.Desert) return;
        if (_strip.Window.Visible) Tumbleweed();
        _tumbleToken = _scheduler.After(_rng.Range(18f, 45f), TumbleTick);
    }

    private void Tumbleweed()
    {
        var layer = _swimmers!;
        const float d = 22f;
        var v = Shapes.Visual(26, 26);
        v.CenterPoint = new Vector3(13, 13, 0);
        var brush = Shapes.Solid(0xA1887F);
        var dark = Shapes.Solid(0x7B5E4F);
        for (int k = 0; k < 5; k++)
        {
            var e = Shapes.Ellipse(13, 13, _rng.Range(6f, 10.5f), _rng.Range(4f, 9f), Shapes.Solid(0, 0));
            e.FillBrush = null;
            e.StrokeBrush = k % 2 == 0 ? brush : dark;
            e.StrokeThickness = 0.9f;
            e.CenterPoint = new Vector2(13, 13);
            e.RotationAngleInDegrees = _rng.Range(0f, 180f);
            v.Shapes.Add(e);
        }
        float dir = _rng.Sign();
        float x0 = dir > 0 ? -30f : W + 30f, x1 = dir > 0 ? W + 30f : -30f;
        double seconds = MathF.Abs(x1 - x0) / _rng.Range(70f, 120f);
        float baseY = GroundY - d / 2 - 13f + 2f;
        v.Offset = new Vector3(x0 - 13f, baseY, 0);
        layer.Children.InsertAtTop(v);

        var batch = C.CreateScopedBatch(CompositionBatchTypes.Animation);
        v.StartAnimation("Offset.X", Anim.Scalar(seconds, Anim.Linear, 1f, x1 - 13f));
        batch.End();
        batch.Completed += (_, _) =>
        {
            try
            {
                Shapes.Discard(v);
            }
            catch (Exception)
            {
            }
        };
        float turns = MathF.Abs(x1 - x0) / (MathF.PI * d);
        v.StartAnimation("RotationAngleInDegrees", Anim.Scalar(seconds, Anim.Linear, 1f, dir * turns * 360f));
        var bounce = C.CreateScalarKeyFrameAnimation();
        float h = _rng.Range(6f, 14f);
        bounce.InsertKeyFrame(0f, baseY);
        bounce.InsertKeyFrame(0.5f, baseY - h, Anim.EaseOut);
        bounce.InsertKeyFrame(1f, baseY, Anim.EaseIn);
        bounce.Duration = Anim.Sec(_rng.Range(0.7f, 1.0f));
        bounce.IterationBehavior = AnimationIterationBehavior.Forever;
        v.StartAnimation("Offset.Y", bounce);
        Log.Write("tumbleweed!");
    }

    // ---------------------------------------------------------------- props

    private ContainerVisual? BuildProps(SceneKind scene)
    {
        Func<float, Visual>? make = scene switch
        {
            SceneKind.Desert => Cactus,
            SceneKind.Winter => Pine,
            SceneKind.Ocean => Coral,
            SceneKind.Autumn => Pumpkin,
            _ => null,
        };
        if (make == null) return null;
        // Tall props stand behind the grass; short ones sit in front of it (behind the front tufts).
        var layer = NewLayer(scene == SceneKind.Autumn ? _strip.Critters : _strip.BackDecor, atBottom: scene == SceneKind.Autumn);
        float spacing = scene is SceneKind.Desert or SceneKind.Winter ? 520f : 420f;
        for (float x = _rng.Range(60f, spacing); x < W - 40f; x += spacing * _rng.Range(0.6f, 1.4f))
            layer.Children.InsertAtTop(make(x));
        return layer;
    }

    private ShapeVisual Prop(float x, float width, float height)
    {
        var v = Shapes.Visual(width, height, new Vector2(x - width / 2, GroundY - height + 1f));
        return v;
    }

    private Visual Cactus(float x)
    {
        float h = _rng.Range(26f, 44f);
        var v = Prop(x, 30, h + 2);
        var green = Anim.Hex(0x5E8C4A);
        var fill = Shapes.Solid(green);
        var rib = Shapes.Solid(Anim.Darken(green, 0.25f));
        float top = 2f;
        v.Shapes.Add(Shapes.RoundRect(11.5f, top, 7f, h, 3.5f, fill));
        float armY = top + h * _rng.Range(0.3f, 0.5f);
        v.Shapes.Add(Shapes.RoundRect(4f, armY + 4f, 9f, 4f, 2f, fill));
        v.Shapes.Add(Shapes.RoundRect(4f, armY - 6f, 4f, 13f, 2f, fill));
        if (_rng.Chance(0.7f))
        {
            float arm2 = top + h * _rng.Range(0.45f, 0.65f);
            v.Shapes.Add(Shapes.RoundRect(17f, arm2 + 3f, 9f, 4f, 2f, fill));
            v.Shapes.Add(Shapes.RoundRect(22f, arm2 - 5f, 4f, 12f, 2f, fill));
        }
        v.Shapes.Add(Shapes.Line(15f, top + 3f, 15f, top + h - 2f, rib, 0.7f));
        v.Shapes.Add(Shapes.Line(13.3f, top + 4f, 13.3f, top + h - 2f, rib, 0.5f));
        v.Shapes.Add(Shapes.Line(16.7f, top + 4f, 16.7f, top + h - 2f, rib, 0.5f));
        if (_rng.Chance(0.5f)) v.Shapes.Add(Shapes.Circle(15f, top + 0.5f, 1.6f, Shapes.Solid(0xF06292)));
        return v;
    }

    private Visual Pine(float x)
    {
        float h = _rng.Range(34f, 52f);
        var v = Prop(x, 30, h);
        var green = Shapes.Solid(Anim.Hex(0x2F5D4A));
        var snow = Shapes.Solid(0xF4F8FC);
        v.Shapes.Add(Shapes.RoundRect(13.5f, h - 7f, 3f, 7f, 1f, Shapes.Solid(0x5D4037)));
        for (int k = 0; k < 3; k++)
        {
            float tierTop = h * (0.02f + k * 0.24f), tierBottom = h * (0.42f + k * 0.2f), half = 6.5f + k * 3.6f;
            v.Shapes.Add(Shapes.Path(new PathBuilder(winding: true).Polygon(15f, tierTop, 15f + half, tierBottom, 15f - half, tierBottom), green));
            float capBottom = tierTop + (tierBottom - tierTop) * 0.38f, capHalf = half * 0.38f;
            v.Shapes.Add(Shapes.Path(new PathBuilder(winding: true)
                .MoveTo(15f, tierTop).LineTo(15f + capHalf, capBottom).QuadTo(15f, capBottom - 1.6f, 15f - capHalf, capBottom).Close(), snow));
        }
        return v;
    }

    private Visual Coral(float x)
    {
        var color = Anim.Hex(_rng.Pick(new uint[] { 0xFF8A80, 0xF48FB1, 0xFFAB91, 0xCE93D8 }));
        var v = Prop(x, 28, 34);
        var brush = Shapes.Solid(color);
        void Branch(float x0, float y0, float angle, float length, int depth)
        {
            float a = angle * MathF.PI / 180f;
            float x1 = x0 + MathF.Sin(a) * length, y1 = y0 - MathF.Cos(a) * length;
            v.Shapes.Add(Shapes.Line(x0, y0, x1, y1, brush, 2.4f - depth * 0.5f));
            if (depth >= 2) return;
            Branch(x1, y1, angle - _rng.Range(18f, 34f), length * 0.72f, depth + 1);
            Branch(x1, y1, angle + _rng.Range(18f, 34f), length * 0.72f, depth + 1);
        }
        Branch(14f, 33f, _rng.Range(-8f, 8f), _rng.Range(9f, 12f), 0);
        return v;
    }

    private Visual Pumpkin(float x)
    {
        float s = _rng.Range(0.8f, 1.2f);
        var v = Prop(x, 22, 18);
        var orange = Anim.Hex(0xE8781E);
        v.Scale = new Vector3(s, s, 1);
        v.CenterPoint = new Vector3(11, 18, 0);
        v.Shapes.Add(Shapes.Ellipse(6.5f, 12f, 4.5f, 5f, Shapes.Solid(Anim.Darken(orange, 0.12f))));
        v.Shapes.Add(Shapes.Ellipse(15.5f, 12f, 4.5f, 5f, Shapes.Solid(Anim.Darken(orange, 0.12f))));
        v.Shapes.Add(Shapes.Ellipse(11f, 12f, 5.2f, 5.6f, Shapes.Solid(orange)));
        v.Shapes.Add(Shapes.RoundRect(10f, 4.4f, 2f, 3.6f, 0.8f, Shapes.Solid(0x5D7A2E)));
        return v;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tumbleToken?.Cancel();
        foreach (var layer in new[] { _weather, _swimmers, _props })
        {
            try
            {
                if (layer != null) Shapes.Discard(layer);
            }
            catch (Exception)
            {
            }
        }
    }

    public static void ResetCache()
    {
        s_leaf = s_vein = null;
    }
}
