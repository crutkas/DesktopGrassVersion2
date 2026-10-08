// Hedgehog.cs - a slow, solitary hedgehog: waddles, snuffles for bugs, naps curled up, and rolls into
// a spiky ball (with a nervous shake) when you click near it.
using System;
using System.Numerics;
using DesktopGrass.Composition.Interop;
using Windows.UI;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal sealed class Hedgehog : Critter
{
    public static readonly string[] Names =
        { "Bristle", "Quill", "Mossy", "Truffle", "Prickles", "Snuffles", "Pinecone", "Hazel", "Bramble", "Pip", "Sage", "Burdock" };

    private static readonly Color Spikes = Anim.Hex(0x3E2C20);
    private static readonly Color Body = Anim.Hex(0x6E5541);
    private static readonly Color Quill = Anim.Hex(0x4A3727);
    private static readonly Color Snout = Anim.Hex(0xD1AE87);
    private static readonly Color Ink = Anim.Hex(0x1A1208);

    private readonly CompositionContainerShape _normal, _ball, _lift, _bob, _facePose, _snuffle;
    private readonly CompositionSpriteShape[] _gait;
    private bool _curled;
    private Mood _resume = Mood.Walk;

    protected override float ReactRadius => 70f;
    protected override Vector2 HeadAnchor => new(3f, -19f);
    protected override float ZzzScale => 0.7f;

    public Hedgehog(Herd herd, Rng rng, string name, float x, float speed)
        : base(herd, rng, "hedgehog", name, x, speed, 36f, 26f, 10f, 15f)
    {
        _normal = Group(P(0, -6f));
        Art.Shapes.Add(_normal);
        var farBack = Leg(-5.5f, -3.2f, 2f, 3.2f, Anim.Darken(Snout, 0.5f));
        var farFront = Leg(3.5f, -3.2f, 2f, 3.2f, Anim.Darken(Snout, 0.5f));
        var nearBack = Leg(-2.5f, -3.2f, 2f, 3.2f, Anim.Darken(Snout, 0.35f));
        var nearFront = Leg(6.5f, -3.2f, 2f, 3.2f, Anim.Darken(Snout, 0.35f));
        foreach (var leg in new[] { farBack, farFront, nearBack, nearFront }) _normal.Shapes.Add(leg);
        _gait = new[] { nearBack, farBack, farFront, nearFront };

        _lift = Group(P(0, -3f));
        _bob = Group(P(0, -3f));
        _lift.Shapes.Add(_bob);
        _normal.Shapes.Add(_lift);

        var c = P(-1.5f, -5.4f);
        _bob.Shapes.Add(Shapes.Path(Spiky(c, 10.6f, 8.2f, 172f, 14f, 14, 1.3f, 4f,
            new Vector2(c.X + 10.6f * 0.55f, c.Y + 8.2f * 0.55f), new Vector2(c.X - 10.8f, c.Y + 8.2f * 0.5f)), Shapes.Solid(Spikes)));
        _bob.Shapes.Add(Oval(-1.8f, -4.6f, 8.6f, 6f, Body));
        AddQuills(_bob, P(-1.8f, -4.6f), 8.6f, 6f, 160f, -17f, 8);

        _facePose = Group(P(5.5f, -5f));
        _snuffle = Group(P(5.5f, -5f));
        _facePose.Shapes.Add(_snuffle);
        _bob.Shapes.Add(_facePose);
        _snuffle.Shapes.Add(Oval(6f, -10f, 1.4f, 1.4f, Anim.Darken(Snout, 0.25f)));
        var face = new PathBuilder()
            .MoveTo(Cx + 4.2f, Fy - 10f)
            .CubicTo(Cx + 7.8f, Fy - 10.4f, Cx + 11.6f, Fy - 6.8f, Cx + 13.4f, Fy - 4.4f)
            .CubicTo(Cx + 11.2f, Fy - 2.6f, Cx + 7.4f, Fy - 1.4f, Cx + 4.2f, Fy - 1.8f)
            .Close();
        _snuffle.Shapes.Add(Shapes.Path(face, Shapes.Solid(Snout)));
        Eyes = Group(P(8.4f, -6.8f));
        Eyes.Shapes.Add(Oval(8.4f, -6.8f, 0.95f, 1f, Ink));
        Eyes.Shapes.Add(Oval(8.7f, -7.15f, 0.3f, 0.3f, Anim.Hex(0xFFFFFF)));
        _snuffle.Shapes.Add(Eyes);
        _snuffle.Shapes.Add(Oval(13.2f, -4.5f, 1.1f, 1f, Ink));

        // The ball (sleeping or startled): hidden until needed.
        var bc = P(0, -9.2f);
        _ball = Group(bc);
        _ball.Scale = new Vector2(0.001f);
        _ball.Shapes.Add(Shapes.Path(Spiky(bc, 7.6f, 7.6f, 0f, 360f, 22, 1.27f, 3f), Shapes.Solid(Spikes)));
        _ball.Shapes.Add(Oval(0, -9.2f, 6.2f, 6.2f, Body));
        AddQuills(_ball, bc, 6.2f, 6.2f, 150f, -40f, 7);
        _ball.Shapes.Add(Oval(3.6f, -6f, 2.6f, 1.8f, Snout));
        _ball.Shapes.Add(Oval(5.4f, -5.8f, 0.6f, 0.6f, Ink));
        Art.Shapes.Add(_ball);
    }

    /// <summary>Zig-zag outline along an elliptical arc (spikes lean by <paramref name="lean"/> degrees), then optional closing points.</summary>
    private static PathBuilder Spiky(Vector2 c, float rx, float ry, float fromDeg, float toDeg, int spikes, float tip, float lean, params Vector2[] close)
    {
        Vector2 At(float deg, float k)
        {
            float a = deg * MathF.PI / 180f;
            return new Vector2(c.X + rx * k * MathF.Cos(a), c.Y - ry * k * MathF.Sin(a));
        }
        var pb = new PathBuilder(winding: true);
        var p0 = At(fromDeg, 1f);
        pb.MoveTo(p0.X, p0.Y);
        float step = (toDeg - fromDeg) / spikes;
        for (int i = 0; i < spikes; i++)
        {
            float a0 = fromDeg + step * i;
            var t = At(a0 + step * 0.5f + lean, tip);
            var v = At(a0 + step, 1f);
            pb.LineTo(t.X, t.Y).LineTo(v.X, v.Y);
        }
        foreach (var q in close) pb.LineTo(q.X, q.Y);
        return pb.Close();
    }

    private static void AddQuills(CompositionContainerShape target, Vector2 c, float rx, float ry, float fromDeg, float stepDeg, int count)
    {
        var brush = Shapes.Solid(Quill);
        for (int i = 0; i < count; i++)
        {
            float a = (fromDeg + stepDeg * i) * MathF.PI / 180f, b = a + 0.35f;
            var p1 = new Vector2(c.X + rx * 0.5f * MathF.Cos(a), c.Y - ry * 0.5f * MathF.Sin(a));
            var p2 = new Vector2(c.X + rx * 0.85f * MathF.Cos(b), c.Y - ry * 0.85f * MathF.Sin(b));
            target.Shapes.Add(Shapes.Line(p1.X, p1.Y, p2.X, p2.Y, brush, 0.9f));
        }
    }

    private void Curl(bool on)
    {
        if (on == _curled) return;
        _curled = on;
        var hide = Anim.Vec2(0.16, Anim.EaseIn, (1f, new Vector2(0.001f)));
        var show = Anim.Vec2(0.32, Anim.BackOut, (1f, Vector2.One));
        show.DelayTime = TimeSpan.FromSeconds(0.08);
        (on ? _normal : _ball).StartAnimation("Scale", hide);
        (on ? _ball : _normal).StartAnimation("Scale", show);
    }

    protected override void Pose(Mood mood, Mood previous)
    {
        Curl(mood is Mood.Sleep or Mood.Doze or Mood.Curl);
        if (mood == Mood.Walk)
        {
            Swing(_gait, 0.36, 26f);
            Loop(_bob, "Offset.Y", 0.72, 0f, 0f, 0.25f, -0.7f, 0.5f, 0f, 0.75f, -0.7f, 1f, 0f);
        }
        else
        {
            Still(_gait);
            To(_bob, "Offset.Y", 0f, 0.2);
        }
        To(_lift, "Offset.Y", mood == Mood.Idle ? -1f : 0f, 0.3);
        To(_facePose, "RotationAngleInDegrees", mood == Mood.Graze ? 10f : 0f, 0.3);
        if (mood == Mood.Graze) Loop(_snuffle, "Offset.X", 1.26, 0f, -0.8f, 0.5f, 0.8f, 1f, -0.8f);
        else To(_snuffle, "Offset.X", 0f, 0.2);
        // Curled up the face is hidden, but a blink loop left running would keep Calm mode from idling.
        if (!_curled) StartBlinking();
        else SetEyes(false, 0.1);
    }

    protected override void NextMood()
    {
        switch (Mood)
        {
            case Mood.Walk:
                if (Rng.Chance(0.5f)) Enter(Mood.Sleep, Rng.Range(10f, 25f));
                else if (Rng.Chance(0.55f / 0.85f)) Enter(Mood.Graze, Rng.Range(3f, 6f));
                else Enter(Mood.Idle, Rng.Range(1.5f, 3f));
                break;
            case Mood.Curl when _resume != Mood.Walk:
                Enter(_resume, Rng.Range(1.5f, 3f));
                break;
            default:
                double seconds = WalkFor(Rng.Range(6f, 12f), Speed);
                Enter(Mood.Walk, seconds);
                break;
        }
    }

    protected override void React(Vector2 click, bool direct)
    {
        if (Mood != Mood.Curl) _resume = Mood is Mood.Graze or Mood.Idle ? Mood : Mood.Walk;
        Enter(Mood.Curl, Full ? Rng.Range(3f, 5.5f) : 2.5);
        var shake = Anim.Scalar(0.14, Anim.EaseInOut, 0f, 0f, 0.25f, -9f, 0.75f, 9f, 1f, 0f);
        shake.IterationBehavior = AnimationIterationBehavior.Count;
        shake.IterationCount = 3;
        shake.DelayTime = TimeSpan.FromSeconds(0.2);
        _ball.StartAnimation("RotationAngleInDegrees", shake);
    }
}
