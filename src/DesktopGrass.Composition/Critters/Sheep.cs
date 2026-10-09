// Sheep.cs - a two-tone wool cloud with a dark face. Wanders, grazes, naps, frolics, greets other
// sheep with a nuzzle, and hops away when you click near it.
using System;
using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal sealed class Sheep : Critter
{
    public static readonly string[] Names = { "Bessie", "Wooly", "Clover", "Daisy", "Pippin", "Buttercup", "Mossy", "Hazel" };

    private static readonly Color Wool = Anim.Hex(0xF7F4EB);
    private static readonly Color WoolShade = Anim.Hex(0xDCD3C1);
    private static readonly Color FaceInk = Anim.Hex(0x2B2420);
    private static readonly Color FarLeg = Anim.Hex(0x1A1512);
    private static readonly Color NearLeg = Anim.Hex(0x2E2723);

    private readonly CompositionSpriteShape[] _gait; // near-back, far-back, far-front, near-front
    private readonly CompositionContainerShape _lift, _bob, _headPose, _headMotion;
    private readonly CompositionSpriteShape _ear;

    protected override float ReactRadius => 64f;
    protected override Vector2 HeadAnchor => Mood is Mood.Sleep or Mood.Doze ? new Vector2(13f, -20f) : new Vector2(13f, -26f);

    public Sheep(Herd herd, Rng rng, string name, float x, float speed)
        : base(herd, rng, "sheep", name, x, speed, 48f, 38f, 14f, 25f)
    {
        var farBack = Leg(-7f, -9f, 2.6f, 9f, FarLeg);
        var farFront = Leg(5f, -9f, 2.6f, 9f, FarLeg);
        var nearBack = Leg(-3.5f, -9f, 2.6f, 9f, NearLeg);
        var nearFront = Leg(8.5f, -9f, 2.6f, 9f, NearLeg);
        foreach (var leg in new[] { farBack, farFront, nearBack, nearFront }) Art.Shapes.Add(leg);
        _gait = new[] { nearBack, farBack, farFront, nearFront };

        _lift = Group(P(0, -9f));
        _bob = Group(P(0, -9f));
        _lift.Shapes.Add(_bob);
        Art.Shapes.Add(_lift);

        _bob.Shapes.Add(Blob(WoolShade, (-0.5f, -11f, 12.6f, 7.6f), (-12.8f, -14.4f, 3.3f, 3.3f)));
        _bob.Shapes.Add(Blob(Wool,
            (-0.5f, -13f, 11.8f, 7.2f), (-9.5f, -16.5f, 4.4f, 4.4f), (-4.5f, -19.5f, 5f, 5f), (1.5f, -20f, 5f, 5f),
            (7f, -17.5f, 4.6f, 4.6f), (-11.5f, -12.5f, 4f, 4f), (9.5f, -13f, 3.8f, 3.8f), (-13.2f, -15.6f, 3.1f, 3.1f)));
        _bob.Shapes.Add(Oval(-2f, -21.8f, 4.6f, 1.5f, Anim.Hex(0xFFFFFF, 210)));

        _headPose = Group(P(8.5f, -15f));
        _headMotion = Group(P(8.5f, -15f));
        _headPose.Shapes.Add(_headMotion);
        _bob.Shapes.Add(_headPose);

        _ear = Rotated(Oval(9.6f, -19.4f, 3.2f, 1.4f, FaceInk), P(11.6f, -19.4f), -12f);
        _headMotion.Shapes.Add(_ear);
        _headMotion.Shapes.Add(Rotated(Oval(13.2f, -16f, 4.4f, 5.4f, FaceInk), P(13.2f, -16f), -28f));
        _headMotion.Shapes.Add(Blob(Wool, (11.2f, -21.2f, 3f, 3f), (13.9f, -21.4f, 2.2f, 2.2f)));
        Eyes = Group(P(14.6f, -17.3f));
        Eyes.Shapes.Add(Oval(14.6f, -17.3f, 1.35f, 1.45f, Anim.Hex(0xFFFFFF)));
        Eyes.Shapes.Add(Oval(15f, -17.1f, 0.75f, 0.8f, Anim.Hex(0x120E0C)));
        _headMotion.Shapes.Add(Eyes);
        _headMotion.Shapes.Add(Oval(15.4f, -14.2f, 1.3f, 0.8f, Anim.Hex(0xF48FB1, 150)));
    }

    protected override void Pose(Mood mood, Mood previous)
    {
        bool lying = mood is Mood.Sleep or Mood.Doze;
        bool walking = mood is Mood.Walk or Mood.Flee;
        double step = mood == Mood.Flee ? 0.36 : 0.55;

        if (walking) Swing(_gait, step, 22f);
        else if (mood == Mood.Hop) Tuck();
        else Still(_gait);
        foreach (var leg in _gait) To(leg, "Scale.Y", lying ? 0.3f : 1f, 0.35);
        To(_lift, "Offset.Y", lying ? 6f : 0f, 0.4);

        if (walking) Loop(_bob, "Offset.Y", step / 2, 0f, 0f, 0.5f, -0.8f, 1f, 0f);
        else To(_bob, "Offset.Y", 0f, 0.2);

        float head = mood switch { Mood.Graze => 38f, Mood.Sleep or Mood.Doze => 16f, Mood.Hop => -8f, _ => 0f };
        To(_headPose, "RotationAngleInDegrees", head, 0.35);
        switch (mood)
        {
            case Mood.Graze: Loop(_headMotion, "RotationAngleInDegrees", 0.45, 0f, -4f, 0.5f, 4f, 1f, -4f); break;
            case Mood.Greet: Loop(_headMotion, "RotationAngleInDegrees", 0.7, 0f, 0f, 0.5f, 12f, 1f, 0f); break;
            case Mood.Walk: Loop(_headMotion, "RotationAngleInDegrees", step, 0f, -2.5f, 0.5f, 2.5f, 1f, -2.5f); break;
            default: To(_headMotion, "RotationAngleInDegrees", 0f, 0.2); break;
        }

        if (mood == Mood.Idle)
            Loop(_ear, "RotationAngleInDegrees", Rng.Range(2.2f, 3.4f), 0f, -12f, 0.8f, -12f, 0.86f, -40f, 0.93f, -12f, 1f, -12f);
        else To(_ear, "RotationAngleInDegrees", -12f, 0.2);

        if (lying) SetEyes(false, 0.3);
        else StartBlinking();
    }

    private void Tuck()
    {
        To(_gait[0], "RotationAngleInDegrees", 28f, 0.12);
        To(_gait[1], "RotationAngleInDegrees", 28f, 0.12);
        To(_gait[2], "RotationAngleInDegrees", -30f, 0.12);
        To(_gait[3], "RotationAngleInDegrees", -30f, 0.12);
    }

    protected override void NextMood()
    {
        switch (Mood)
        {
            case Mood.Walk:
                float r = Rng.Next();
                if (r < 0.6f) Enter(Mood.Graze, Rng.Range(3f, 5f));
                else if (r < 0.85f) Enter(Mood.Idle, Rng.Range(1.5f, 3f));
                else Frolic();
                break;
            case Mood.Idle:
                if (Rng.Chance(0.3f)) Enter(Mood.Sleep, Rng.Range(8f, 16f));
                else Walk();
                break;
            case Mood.Graze:
                if (Rng.Chance(0.25f)) Enter(Mood.Idle, Rng.Range(1.5f, 3f));
                else Walk();
                break;
            case Mood.Greet:
                Walk(-Facing);
                break;
            default:
                Walk();
                break;
        }
    }

    private void Walk(float? direction = null)
    {
        double seconds = WalkFor(Rng.Range(8f, 14f), Speed, direction);
        Enter(Mood.Walk, seconds);
    }

    /// <summary>A happy little hop forward.</summary>
    private void Frolic()
    {
        Travel((0.55, ClampX(X + Facing * 9f)));
        Enter(Mood.Hop, 0.55);
        HopArc(0.55, 11f);
    }

    protected override void React(Vector2 click, bool direct)
    {
        float away = MathF.Abs(X - click.X) < 2f ? -Facing : (X > click.X ? 1f : -1f);
        Face(away, 0.12);
        PopAbove(Glyphs.Bang(10f), 10f);
        float fast = Speed * 1.6f;
        float hopTo = ClampX(X + away * fast * 0.55f);
        double dash = Full ? Rng.Range(2.5f, 4f) : 1.2;
        float runTo = ClampX(hopTo + away * fast * (float)dash);
        double dashSeconds = Math.Max(0.1, MathF.Abs(runTo - hopTo) / fast);
        Travel((0.55, hopTo), (0.55 + dashSeconds, runTo));
        Enter(Mood.Hop, 0.55, () => Enter(Mood.Flee, dashSeconds));
        HopArc(0.55, 11f);
        Herd.Grass.Push(X, 40f, 0.16f * away);
    }
}
