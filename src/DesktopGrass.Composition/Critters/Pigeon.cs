// Pigeon.cs - a city rock pigeon: slate blue with dark wing bars and an iridescent neck. Pigeons walk with
// the famous head bob, peck about in a loose flock, doze puffed up, and clatter off when you click near
// them (the whole flock, since each one nearby reacts), landing a little way off.
using System;
using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal sealed class Pigeon : Critter
{
    public static readonly string[] Names = { "Vinnie", "Sal", "Frankie", "Gus", "Coco", "Dot", "Lou", "Pidge", "Benny", "Rosie", "Tony", "Marge" };

    /// <summary>Where a flock hangs out. Pigeons wander back toward it, and it moves when they fly off.</summary>
    public sealed class Flock
    {
        public float Home;
    }

    private const double Step = 0.34;
    private static readonly Color Body = Anim.Hex(0xA4ADBA);
    private static readonly Color Wing = Anim.Hex(0x939DAB);
    private static readonly Color Bar = Anim.Hex(0x3B4049);
    private static readonly Color Head = Anim.Hex(0x6F7988);
    private static readonly Color Tail = Anim.Hex(0x737D8B);
    private static readonly Color Foot = Anim.Hex(0xE07A7A);

    private readonly Flock _flock;
    private readonly CompositionSpriteShape[] _legs;
    private readonly CompositionContainerShape _bob, _wing, _headPose, _headMotion;

    protected override float ReactRadius => 70f;
    protected override Vector2 HeadAnchor => new(5f, -14f);
    protected override float ZzzScale => 0.6f;

    public Pigeon(Herd herd, Rng rng, string name, float x, float speed, Flock flock)
        : base(herd, rng, "pigeon", name, x, speed, 30f, 24f, 7f, 12f)
    {
        _flock = flock;
        var far = Leg(-0.4f, -3.6f, 0.9f, 3.6f, Anim.Darken(Foot, 0.18f));
        var near = Leg(1.4f, -3.6f, 1f, 3.6f, Foot);
        Art.Shapes.Add(far);
        Art.Shapes.Add(near);
        _legs = new[] { near, far };

        _bob = Group(P(0, -5f));
        Art.Shapes.Add(_bob);
        _bob.Shapes.Add(Poly(Tail, -4.6f, -6.4f, -11f, -7.8f, -11.2f, -5.8f, -4.4f, -4.4f));
        _bob.Shapes.Add(Poly(Bar, -9.4f, -7.5f, -11f, -7.8f, -11.2f, -5.8f, -9.6f, -5.6f));
        _bob.Shapes.Add(Blob(Body, (0f, -6.3f, 5.8f, 3.9f), (2.9f, -7.5f, 3.3f, 3.3f)));
        _bob.Shapes.Add(Oval(0.4f, -4.3f, 4.2f, 1.5f, Anim.Darken(Body, 0.07f)));
        _wing = Group(P(1.2f, -8.2f));
        _wing.Shapes.Add(Rotated(Oval(-1.4f, -7.3f, 4.7f, 2.5f, Wing), P(1.2f, -8.2f), -6f));
        _wing.Shapes.Add(Stroke(-3.6f, -8.3f, -2.6f, -6.2f, Bar, 0.75f));
        _wing.Shapes.Add(Stroke(-1.8f, -8.7f, -0.8f, -6.6f, Bar, 0.75f));
        _bob.Shapes.Add(_wing);
        // The iridescent neck: green with a purple sheen.
        _bob.Shapes.Add(Oval(3.7f, -9.1f, 2.5f, 2.5f, Anim.Hex(0x6E9C86)));
        _bob.Shapes.Add(Oval(4.3f, -8.1f, 1.9f, 1.5f, Anim.Hex(0x9A7BB0)));

        _headPose = Group(P(3.8f, -9.6f));
        _headMotion = Group(P(3.8f, -9.6f));
        _headPose.Shapes.Add(_headMotion);
        _bob.Shapes.Add(_headPose);
        _headMotion.Shapes.Add(Oval(5.1f, -11.7f, 2.5f, 2.3f, Head));
        _headMotion.Shapes.Add(Poly(Anim.Hex(0x3E3B39), 7.1f, -12f, 9.1f, -11.4f, 7.1f, -10.9f));
        _headMotion.Shapes.Add(Oval(7.3f, -12f, 0.7f, 0.45f, Anim.Hex(0xE6E8EA)));
        Eyes = Group(P(6f, -12.2f));
        Eyes.Shapes.Add(Oval(6f, -12.2f, 0.75f, 0.75f, Anim.Hex(0xE9812F)));
        Eyes.Shapes.Add(Oval(6.1f, -12.2f, 0.38f, 0.38f, Anim.Hex(0x1A1410)));
        _headMotion.Shapes.Add(Eyes);
    }

    protected override void Pose(Mood mood, Mood previous)
    {
        bool flying = mood == Mood.Flee;
        bool walking = mood == Mood.Walk;
        bool resting = mood is Mood.Sleep or Mood.Doze;

        if (walking) Swing(_legs, Step, 26f);
        else if (flying)
        {
            foreach (var leg in _legs) To(leg, "RotationAngleInDegrees", 55f, 0.1);
        }
        else Still(_legs);
        foreach (var leg in _legs) To(leg, "Scale.Y", resting ? 0.45f : flying ? 0.7f : 1f, 0.3);
        To(_bob, "Offset.Y", resting ? 1.6f : 0f, 0.3);
        To(_bob, "Scale.Y", resting ? 1.08f : 1f, 0.3);
        To(_bob, "RotationAngleInDegrees", flying ? -10f : 0f, 0.15);

        // The head bob: a quick thrust forward, then holding still while the body catches up.
        if (walking) _headMotion.StartAnimation("Offset.X", HeadBob());
        else To(_headMotion, "Offset.X", 0f, 0.12);

        float head = mood switch { Mood.Graze => 42f, Mood.Sleep or Mood.Doze => 16f, _ => 0f };
        To(_headPose, "RotationAngleInDegrees", head, 0.2);
        To(_headPose, "Offset.X", resting ? -1.6f : 0f, 0.3);
        To(_headPose, "Offset.Y", resting ? 1.4f : 0f, 0.3);
        if (mood == Mood.Graze) Loop(_headMotion, "RotationAngleInDegrees", Rng.Range(0.45f, 0.7f), 0f, 0f, 0.15f, 24f, 0.3f, 0f, 1f, 0f);
        else if (mood == Mood.Idle) Loop(_headMotion, "RotationAngleInDegrees", Rng.Range(2.6f, 3.6f), 0f, 0f, 0.4f, -12f, 0.6f, -12f, 1f, 0f);
        else To(_headMotion, "RotationAngleInDegrees", 0f, 0.12);

        if (flying) Loop(_wing, "RotationAngleInDegrees", 0.16, 0f, -10f, 0.5f, -78f, 1f, -10f);
        else To(_wing, "RotationAngleInDegrees", 0f, 0.15);

        if (resting) SetEyes(false, 0.3);
        else StartBlinking();
    }

    private static ScalarKeyFrameAnimation HeadBob()
    {
        var a = C.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, -0.9f);
        a.InsertKeyFrame(0.22f, 1.5f, Anim.EaseOut);
        a.InsertKeyFrame(1f, -0.9f, Anim.Linear);
        a.Duration = Anim.Sec(Step);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        return a;
    }

    protected override void NextMood()
    {
        switch (Mood)
        {
            case Mood.Walk:
                float r = Rng.Next();
                if (r < 0.55f) Enter(Mood.Graze, Rng.Range(2f, 5f));
                else if (r < 0.85f) Enter(Mood.Idle, Rng.Range(1.5f, 3.5f));
                else Walk();
                break;
            case Mood.Graze:
                if (Rng.Chance(0.65f)) Walk();
                else Enter(Mood.Idle, Rng.Range(1.5f, 3f));
                break;
            case Mood.Idle:
                if (Rng.Chance(0.12f)) Enter(Mood.Sleep, Rng.Range(6f, 12f));
                else Walk();
                break;
            case Mood.Flee:
                Enter(Mood.Idle, Rng.Range(1f, 2.5f));
                break;
            default:
                Walk();
                break;
        }
    }

    private void Walk()
    {
        // Wander, but drift back toward the flock.
        float off = _flock.Home - X;
        float? dir = MathF.Abs(off) > 55f ? MathF.Sign(off) : null;
        double seconds = WalkFor(Rng.Range(1.5f, 4f), Speed, dir);
        Enter(Mood.Walk, seconds);
    }

    protected override void React(Vector2 click, bool direct)
    {
        if (Mood == Mood.Flee) return;
        float away = MathF.Abs(X - click.X) < 2f ? -Facing : (X > click.X ? 1f : -1f);
        float distance = Rng.Range(70f, 150f) * (Full ? 1f : 0.6f);
        float target = ClampX(X + away * distance);
        if (MathF.Abs(target - X) < 30f) target = ClampX(X - away * distance);
        double seconds = MathF.Abs(target - X) / Rng.Range(70f, 95f) + 0.3;
        _flock.Home = target;
        Face(target - X, 0.08);
        Travel((seconds, target));
        Enter(Mood.Flee, seconds, Landed);
        HopArc(seconds, Rng.Range(18f, 30f));
        var feather = Shapes.Ellipse(3f, 3f, 1.6f, 0.6f, Shapes.Solid(Body));
        Fx.Drift(Herd.Strip.Fx, feather, 6f, World(new Vector2(0, -7f)), new Vector2(Rng.Range(-8f, 8f), 9f), 1.8, spinDegrees: Rng.Range(-160f, 160f));
    }

    private void Landed()
    {
        Herd.Grass.Push(X, 18f, 0.05f * Facing);
        Enter(Mood.Idle, Rng.Range(1f, 2.5f));
    }
}
