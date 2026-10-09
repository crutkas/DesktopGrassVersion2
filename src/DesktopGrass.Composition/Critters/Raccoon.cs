// Raccoon.cs - a masked, ring-tailed raccoon. Raccoons waddle about, snuffle through the grass and stop
// for a rest; a click nearby sends one scurrying off, and poking one makes it rear up on its hind legs
// first. Every gang has exactly one Jimothy, who is shorter and rounder than the rest.
using System;
using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal sealed class Raccoon : Critter
{
    public const string Jimothy = "Jimothy";
    public static readonly string[] Names = { "Bandit", "Maple", "Rascal", "Pebble", "Scout", "Mochi", "Tucker" };

    private const float LegLength = 4.2f;
    private const float HeadRadius = 5f;
    private const float TailLength = 13f;

    private static readonly Color Fur = Anim.Hex(0x8C9094);
    private static readonly Color Saddle = Anim.Hex(0x73777B);
    private static readonly Color Belly = Anim.Hex(0xBDC0C1);
    private static readonly Color Ink = Anim.Hex(0x2A2C2F);
    private static readonly Color FarInk = Anim.Hex(0x1C1E21);
    private static readonly Color FaceLight = Anim.Hex(0xDCDEDD);
    private static readonly Color EarRim = Anim.Hex(0xC8CBCA);
    private static readonly Color EarInner = Anim.Hex(0x3E4144);

    private readonly CompositionSpriteShape[] _legs, _gait;
    private readonly CompositionSpriteShape _frontNear, _frontFar;
    private readonly CompositionContainerShape _lift, _bob, _tail, _headPose, _headMotion;
    private readonly Vector2 _head;

    public bool IsJimothy { get; }

    protected override float ReactRadius => 80f;
    protected override Vector2 HeadAnchor => new(_head.X, _head.Y - (Mood == Mood.Doze ? 4f : 8f));

    public Raccoon(Herd herd, Rng rng, string name, float x, float speed)
        : base(herd, rng, "raccoon", name, x, speed, 54f, 46f, name == Jimothy ? 10.6f : 13.8f, name == Jimothy ? 22f : 20f)
    {
        IsJimothy = name == Jimothy;
        // Jimothy is the short-bodied one: 70% as long and 125% as tall as the rest of the gang.
        float br = IsJimothy ? 8.4f : 12f, bh = IsJimothy ? 9.4f : 7.5f;
        float cy = -(bh + LegLength);
        float hip = cy + 0.55f * bh;
        float hr = HeadRadius;
        _head = new Vector2(0.88f * br, cy - 0.1f * bh);
        var h = _head;

        var farBack = Leg(-0.38f * br, hip, 2.5f, -hip, FarInk);
        var farFront = Leg(0.4f * br, hip, 2.5f, -hip, FarInk);
        var nearBack = Leg(-0.52f * br, hip, 2.6f, -hip, Ink);
        var nearFront = Leg(0.54f * br, hip, 2.6f, -hip, Ink);
        _legs = new[] { farBack, farFront, nearBack, nearFront };
        _gait = new[] { nearBack, farBack, farFront, nearFront };
        _frontNear = nearFront;
        _frontFar = farFront;
        Art.Shapes.Add(farBack);
        Art.Shapes.Add(nearBack);

        // Everything but the hind legs rears up together around the hind hip.
        _lift = Group(P(-0.45f * br, hip));
        Art.Shapes.Add(_lift);
        _lift.Shapes.Add(farFront);
        _lift.Shapes.Add(nearFront);
        _bob = Group(P(0, cy));
        _lift.Shapes.Add(_bob);

        // Ringed tail, base to tip (the tip is dark), arcing up a little.
        var tailBase = new Vector2(-0.72f * br, cy - 0.05f * bh);
        _tail = Group(P(tailBase.X, tailBase.Y));
        for (int i = 0; i < 6; i++)
        {
            float t = i / 5f;
            _tail.Shapes.Add(Oval(tailBase.X - t * TailLength, tailBase.Y - 2.2f * MathF.Sin(t * MathF.PI),
                4.2f - 1.5f * t, 3.8f - 1.1f * t, i % 2 == 0 ? Fur : Ink));
        }
        _bob.Shapes.Add(_tail);

        _bob.Shapes.Add(Oval(0, cy, br, bh, Fur));
        _bob.Shapes.Add(Oval(-0.1f * br, cy - 0.45f * bh, 0.75f * br, 0.42f * bh, Saddle));
        _bob.Shapes.Add(Oval(0.1f * br, cy + 0.5f * bh, 0.66f * br, 0.34f * bh, Belly));
        _bob.Shapes.Add(Oval(-0.2f * br, cy - 0.8f * bh, 0.38f * br, 0.1f * bh, Anim.Hex(0xFFFFFF, 46)));

        var neck = P(0.62f * br, cy - 0.1f * bh);
        _headPose = Group(neck);
        _headMotion = Group(neck);
        _headPose.Shapes.Add(_headMotion);
        _bob.Shapes.Add(_headPose);

        _headMotion.Shapes.Add(Poly(EarRim, h.X - 0.3f * hr, h.Y - 1.42f * hr, h.X - 0.88f * hr, h.Y - 0.38f * hr, h.X + 0.12f * hr, h.Y - 0.5f * hr));
        _headMotion.Shapes.Add(Poly(EarInner, h.X - 0.32f * hr, h.Y - 1.18f * hr, h.X - 0.7f * hr, h.Y - 0.5f * hr, h.X - 0.04f * hr, h.Y - 0.56f * hr));
        _headMotion.Shapes.Add(Poly(FaceLight, h.X + 1.12f * hr, h.Y + 0.2f * hr, h.X - 0.2f * hr, h.Y - 0.55f * hr, h.X - 0.2f * hr, h.Y + 0.75f * hr));
        _headMotion.Shapes.Add(Oval(h.X, h.Y, hr, 0.88f * hr, FaceLight));
        // White brow over the bandit mask; a glint keeps the eye readable on the dark mask.
        var brow = new Vector2(h.X + 0.25f * hr, h.Y - 0.6f * hr);
        _headMotion.Shapes.Add(Rotated(Oval(brow.X, brow.Y, 0.58f * hr, 0.16f * hr, Anim.Hex(0xFFFFFF)), P(brow.X, brow.Y), 10f));
        var mask = new Vector2(h.X + 0.2f * hr, h.Y - 0.14f * hr);
        _headMotion.Shapes.Add(Rotated(Oval(mask.X, mask.Y, 0.78f * hr, 0.34f * hr, Ink), P(mask.X, mask.Y), 10f));
        var eye = new Vector2(h.X + 0.45f * hr, h.Y - 0.18f * hr);
        Eyes = Group(P(eye.X, eye.Y));
        Eyes.Shapes.Add(Oval(eye.X, eye.Y, 0.95f, 1f, Anim.Hex(0x0B0C0D)));
        Eyes.Shapes.Add(Oval(eye.X + 0.32f, eye.Y - 0.36f, 0.36f, 0.36f, Anim.Hex(0xFFFFFF)));
        _headMotion.Shapes.Add(Eyes);
        _headMotion.Shapes.Add(Oval(h.X + 1.08f * hr, h.Y + 0.17f * hr, 1.15f, 0.92f, Anim.Hex(0x17181A)));
    }

    protected override void Pose(Mood mood, Mood previous)
    {
        bool lying = mood == Mood.Doze;
        bool resting = mood == Mood.Idle;
        bool standing = mood == Mood.Stand;
        bool walking = mood is Mood.Walk or Mood.Flee;
        double step = mood == Mood.Flee ? 0.42 : 0.78;

        if (walking) Swing(_gait, step, mood == Mood.Flee ? 28f : 18f);
        else Still(_gait);
        if (standing)
        {
            // Little forepaws dangling in front of its chest: rotate back against the body's tilt.
            To(_frontNear, "RotationAngleInDegrees", 32f, 0.2);
            To(_frontFar, "RotationAngleInDegrees", 40f, 0.2);
        }
        foreach (var leg in _legs) To(leg, "Scale.Y", lying ? 0.3f : 1f, 0.35);
        To(_lift, "Offset.Y", lying ? 4.5f : resting ? 2f : 0f, 0.4);
        To(_lift, "RotationAngleInDegrees", standing ? -42f : 0f, standing ? 0.25 : 0.3, standing ? Anim.BackOut : null);

        // The waddle: a bob on every step and a little side-to-side rock.
        if (walking)
        {
            Loop(_bob, "Offset.Y", step / 2, 0f, 0f, 0.5f, -0.7f, 1f, 0f);
            Loop(_bob, "RotationAngleInDegrees", step, 0f, -1.6f, 0.5f, 1.6f, 1f, -1.6f);
        }
        else
        {
            To(_bob, "Offset.Y", 0f, 0.2);
            To(_bob, "RotationAngleInDegrees", 0f, 0.2);
        }

        float head = mood switch { Mood.Graze => 24f, Mood.Doze => 14f, Mood.Stand => 30f, _ => 0f };
        To(_headPose, "RotationAngleInDegrees", head, 0.3);
        if (mood == Mood.Graze) Loop(_headMotion, "Offset.X", 0.9, 0f, -1.2f, 0.5f, 1.2f, 1f, -1.2f);
        else To(_headMotion, "Offset.X", 0f, 0.2);

        // Positive angles lift the tail; standing counter-rotates it to stay along the ground.
        if (walking) Loop(_tail, "RotationAngleInDegrees", step * 2, 0f, -6f, 0.5f, 6f, 1f, -6f);
        else if (resting) Loop(_tail, "RotationAngleInDegrees", 2.6, 0f, -9f, 0.5f, 7f, 1f, -9f);
        else To(_tail, "RotationAngleInDegrees", standing ? 44f : lying ? -14f : 0f, 0.3);

        if (lying) SetEyes(false, 0.3);
        else StartBlinking();
    }

    protected override void NextMood()
    {
        switch (Mood)
        {
            case Mood.Walk:
            case Mood.Flee:
                if (Rng.Chance(0.65f)) Enter(Mood.Graze, Rng.Range(1.5f, 3f));
                else Enter(Mood.Idle, Rng.Range(3f, 6f));
                break;
            default:
                double seconds = WalkFor(Rng.Range(5f, 10f), Speed);
                Enter(Mood.Walk, seconds);
                break;
        }
    }

    protected override void React(Vector2 click, bool direct)
    {
        if (Mood is Mood.Flee or Mood.Stand) return;
        float away = MathF.Abs(X - click.X) < 2f ? -Facing : (X > click.X ? 1f : -1f);
        PopAbove(Glyphs.Bang(10f), 10f);
        if (direct)
        {
            // A poke: it rears up on its hind legs to see what that was, then scurries off.
            Enter(Mood.Stand, 0.75, () => Scurry(away));
            return;
        }
        Scurry(away);
    }

    private void Scurry(float away)
    {
        double seconds = WalkFor(Full ? 3.0 : 1.2, Speed * 1.5f, away);
        Enter(Mood.Flee, seconds);
        Herd.Grass.Push(X, 30f, 0.1f * away);
    }
}
