// Cat.cs - a lean cat in one of six coats. Walks, sits and watches with a swishing tail, loafs for long
// naps, purrs (a heart) when you click it, and pounces at clicks nearby.
using System;
using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal sealed class Cat : Critter
{
    public static readonly string[] Names = { "Mittens", "Whiskers", "Shadow", "Ginger", "Smokey", "Boots", "Sage", "Juno" };
    public const int CoatCount = 6;

    private static readonly (uint Body, uint Dark, uint Eye, uint Whisker, bool Tabby)[] Coats =
    {
        (0x7C7269, 0x4A433D, 0x1A1614, 0x1A1614, true),  // gray tabby
        (0xE0A066, 0xA8682F, 0x2B1A0E, 0x2B1A0E, true),  // ginger
        (0x2E2926, 0x161210, 0xE8C552, 0xA69C90, false), // black, golden eyes
        (0xF1EDE5, 0xC2BBAE, 0x4F86C2, 0x8A8278, false), // white, blue eyes
        (0x80633F, 0x52402A, 0x1A1108, 0x1A1108, true),  // brown tabby
        (0xD2C1A0, 0x96866F, 0x2E251D, 0x2E251D, false), // cream
    };

    private readonly CompositionSpriteShape[] _legs, _gait;
    private readonly CompositionContainerShape _lift, _bob, _tail, _headPose, _headMotion;

    protected override float ReactRadius => 80f;
    protected override Vector2 HeadAnchor => Mood is Mood.Sleep or Mood.Doze ? new Vector2(12f, -18f) : new Vector2(12f, -26f);

    public Cat(Herd herd, Rng rng, string name, float x, float speed, int coat)
        : base(herd, rng, "cat", name, x, speed, 50f, 36f, 13f, 25f)
    {
        var (body, dark, eye, whisker, tabby) = Coats[coat % CoatCount];
        Color cBody = Anim.Hex(body), cDark = Anim.Hex(dark), cLight = Anim.Lighten(cBody, 0.32f), cNear = Anim.Darken(cBody, 0.12f);

        var farBack = Leg(-8f, -8f, 2.1f, 8f, cDark);
        var farFront = Leg(6f, -8f, 2.1f, 8f, cDark);
        var nearBack = Leg(-5f, -8f, 2.2f, 8f, cNear);
        var nearFront = Leg(9f, -8f, 2.2f, 8f, cNear);
        _legs = new[] { farBack, farFront, nearBack, nearFront };
        foreach (var leg in _legs) Art.Shapes.Add(leg);
        _gait = new[] { nearBack, farBack, farFront, nearFront };

        _lift = Group(P(0, -8f));
        _bob = Group(P(8f, -7f)); // pivot at the shoulders so a crouch wiggles the rear
        _lift.Shapes.Add(_bob);
        Art.Shapes.Add(_lift);

        _tail = Group(P(-10f, -12f));
        _tail.Shapes.Add(Curve(cBody, 2.4f, -10f, -12f, -16.5f, -12.5f, -19.5f, -18f, -16.8f, -24f));
        _bob.Shapes.Add(_tail);
        _bob.Shapes.Add(Oval(-0.5f, -12f, 11.5f, 5.6f, cBody));
        _bob.Shapes.Add(Oval(1.5f, -9.2f, 7f, 2.1f, cLight));
        if (tabby)
        {
            var stripe = Anim.Darken(cBody, 0.3f);
            _bob.Shapes.Add(Stroke(-6.2f, -17.1f, -5.4f, -14.4f, stripe, 1.3f));
            _bob.Shapes.Add(Stroke(-2.6f, -17.5f, -2f, -14.6f, stripe, 1.3f));
            _bob.Shapes.Add(Stroke(1f, -17.4f, 1.4f, -14.8f, stripe, 1.3f));
        }

        _headPose = Group(P(8f, -14f));
        _headMotion = Group(P(8f, -14f));
        _headPose.Shapes.Add(_headMotion);
        _bob.Shapes.Add(_headPose);
        var inner = coat == 2 ? Anim.Hex(0x5A4A44) : Anim.Hex(0xF2A7A0);
        _headMotion.Shapes.Add(Poly(cBody, 7.6f, -19.4f, 8.6f, -25.6f, 11.6f, -20.8f));
        _headMotion.Shapes.Add(Poly(cBody, 11.2f, -21f, 14.4f, -26.2f, 15.6f, -20.6f));
        _headMotion.Shapes.Add(Poly(inner, 12.3f, -21.4f, 14.2f, -24.6f, 14.9f, -21.2f));
        _headMotion.Shapes.Add(Oval(11.8f, -17.2f, 5.1f, 5.1f, cBody));
        _headMotion.Shapes.Add(Oval(14.6f, -15.3f, 2.4f, 1.7f, cLight));
        Eyes = Group(P(13.4f, -18.4f));
        Eyes.Shapes.Add(Oval(13.4f, -18.4f, 0.95f, 1.3f, Anim.Hex(eye)));
        Eyes.Shapes.Add(Oval(13.7f, -18.8f, 0.32f, 0.32f, Anim.Hex(0xFFFFFF)));
        _headMotion.Shapes.Add(Eyes);
        _headMotion.Shapes.Add(Poly(Anim.Hex(0xF28B82), 16.2f, -16.6f, 17.4f, -16.6f, 16.8f, -15.7f));
        var w = Anim.Hex(whisker, 150);
        _headMotion.Shapes.Add(Stroke(15.8f, -15.4f, 20.2f, -16.4f, w, 0.35f));
        _headMotion.Shapes.Add(Stroke(15.8f, -14.9f, 20.2f, -14.1f, w, 0.35f));
    }

    protected override void Pose(Mood mood, Mood previous)
    {
        bool lying = mood is Mood.Sleep or Mood.Doze;
        bool walking = mood == Mood.Walk;

        if (walking) Swing(_gait, 0.5, 18f);
        else if (mood != Mood.Pounce) Still(_gait);
        foreach (var leg in _legs) To(leg, "Scale.Y", lying ? 0.05f : 1f, 0.35);
        To(_lift, "Offset.Y", lying ? 7f : 0f, 0.45);
        if (walking) Loop(_bob, "Offset.Y", 0.25, 0f, 0f, 0.5f, -0.5f, 1f, 0f);
        else To(_bob, "Offset.Y", 0f, 0.2);
        if (mood != Mood.Pounce) To(_bob, "RotationAngleInDegrees", 0f, 0.15);

        float head = mood switch { Mood.Sleep or Mood.Doze => 10f, Mood.Idle => -5f, _ => 0f };
        To(_headPose, "RotationAngleInDegrees", head, 0.35);
        if (walking) Loop(_headMotion, "RotationAngleInDegrees", 0.5, 0f, -2f, 0.5f, 2f, 1f, -2f);
        else To(_headMotion, "RotationAngleInDegrees", 0f, 0.2);

        if (lying) To(_tail, "RotationAngleInDegrees", -72f, 0.5);
        else if (mood == Mood.Idle) Loop(_tail, "RotationAngleInDegrees", 1.6, 0f, -14f, 0.5f, 10f, 1f, -14f);
        else if (walking) Loop(_tail, "RotationAngleInDegrees", 2.2, 0f, -8f, 0.5f, 8f, 1f, -8f);
        else To(_tail, "RotationAngleInDegrees", 0f, 0.3);

        if (lying) SetEyes(false, 0.35);
        else StartBlinking();
    }

    protected override void NextMood()
    {
        switch (Mood)
        {
            case Mood.Walk:
                float r = Rng.Next();
                if (r < 0.65f) Enter(Mood.Idle, Rng.Range(4f, 8f));
                else if (r < 0.95f) Enter(Mood.Sleep, Rng.Range(20f, 40f));
                else Walk();
                break;
            case Mood.Idle:
                if (Rng.Chance(0.5f)) Enter(Mood.Sleep, Rng.Range(20f, 40f));
                else Walk();
                break;
            default:
                Walk();
                break;
        }
    }

    private void Walk()
    {
        double seconds = WalkFor(Rng.Range(6f, 10f), Speed);
        Enter(Mood.Walk, seconds);
    }

    protected override void React(Vector2 click, bool direct)
    {
        if (direct)
        {
            // A pat: purr, then sit and watch for a while.
            PopAbove(Glyphs.Heart(10f), 10f);
            Enter(Mood.Idle, Full ? Rng.Range(3f, 6f) : 1.5);
            return;
        }
        if (Mood != Mood.Pounce) Pounce(click.X);
    }

    private void Pounce(float targetX)
    {
        Halt();
        Face(targetX - X, 0.12);
        Enter(Mood.Pounce, 0.34, () => Leap(targetX));
        Still(_gait, 0.1);
        To(_lift, "Offset.Y", 2.5f, 0.15);
        To(_headPose, "RotationAngleInDegrees", 6f, 0.15);
        var wiggle = Anim.Scalar(0.16, Anim.EaseInOut, 0f, 0f, 0.5f, 3.5f, 1f, 0f);
        wiggle.IterationBehavior = AnimationIterationBehavior.Count;
        wiggle.IterationCount = 2;
        _bob.StartAnimation("RotationAngleInDegrees", wiggle);
    }

    private void Leap(float targetX)
    {
        Travel((0.45, ClampX(targetX - Facing * 4f)));
        Enter(Mood.Pounce, 0.45, Landed);
        To(_lift, "Offset.Y", 0f, 0.1);
        To(_headPose, "RotationAngleInDegrees", -4f, 0.1);
        To(_gait[0], "RotationAngleInDegrees", 40f, 0.1);
        To(_gait[1], "RotationAngleInDegrees", 40f, 0.1);
        To(_gait[2], "RotationAngleInDegrees", -45f, 0.1);
        To(_gait[3], "RotationAngleInDegrees", -45f, 0.1);
        HopArc(0.45, 9f);
    }

    private void Landed()
    {
        Herd.Grass.Push(X, 30f, 0.12f * Facing);
        Enter(Mood.Idle, Full ? Rng.Range(2f, 4f) : 1.2);
    }
}
