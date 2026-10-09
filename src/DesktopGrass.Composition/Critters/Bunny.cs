// Bunny.cs - a round brown bunny that only ever hops: bouts of parabolic hops with little ground pauses,
// nibbling, nose twitches and ear wiggles. Clicks nearby send it bounding away.
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal sealed class Bunny : Critter
{
    public static readonly string[] Names =
        { "Clover", "Hazel", "Thumper", "Mochi", "Pip", "Acorn", "Biscuit", "Willow", "Pepper", "Hopper", "Juniper", "Snowdrop" };

    private const double HopSeconds = 0.4;
    private static readonly Color Body = Anim.Hex(0x8F6E4D);
    private static readonly Color Belly = Anim.Hex(0xD2B898);
    private static readonly Color Inner = Anim.Hex(0xEDAAAA);
    private static readonly Color Tail = Anim.Hex(0xFAF7F0);
    private static readonly Color Ink = Anim.Hex(0x1A1208);
    private static readonly Color Nose = Anim.Hex(0xD9788A);

    private readonly CompositionContainerShape _lift, _bob, _headPose, _headMotion, _ears;
    private readonly CompositionSpriteShape _nose;

    protected override float ReactRadius => 90f;
    protected override Vector2 HeadAnchor => Mood is Mood.Sleep or Mood.Doze ? new Vector2(8f, -19f) : new Vector2(8f, -29f);
    protected override float ZzzScale => 0.8f;

    public Bunny(Herd herd, Rng rng, string name, float x, float speed)
        : base(herd, rng, "bunny", name, x, speed, 40f, 38f, 9f, 27f)
    {
        var foot = Anim.Darken(Body, 0.15f);
        Art.Shapes.Add(Oval(-3.5f, -1.7f, 4.4f, 1.7f, foot));
        Art.Shapes.Add(Oval(4.8f, -1.5f, 1.9f, 1.5f, foot));

        _lift = Group(P(0, -2f));
        _bob = Group(P(0, -2f));
        _lift.Shapes.Add(_bob);
        Art.Shapes.Add(_lift);
        _bob.Shapes.Add(Oval(-1f, -7.8f, 8.2f, 6.4f, Body));
        _bob.Shapes.Add(Oval(-3.8f, -6.2f, 4.6f, 4.4f, Anim.Darken(Body, 0.07f)));
        _bob.Shapes.Add(Oval(2.2f, -5.4f, 4.4f, 3.2f, Belly));
        _bob.Shapes.Add(Oval(-9.2f, -9f, 2.7f, 2.7f, Tail));

        _headPose = Group(P(5f, -11f));
        _headMotion = Group(P(5f, -11f));
        _headPose.Shapes.Add(_headMotion);
        _bob.Shapes.Add(_headPose);
        _ears = Group(P(6f, -17.5f));
        _ears.Shapes.Add(Rotated(Oval(4.4f, -22.2f, 1.8f, 5.4f, Anim.Darken(Body, 0.12f)), P(4.8f, -17.5f), -14f));
        _ears.Shapes.Add(Rotated(Oval(6.8f, -22.6f, 1.95f, 5.6f, Body), P(6.6f, -17.5f), -5f));
        _ears.Shapes.Add(Rotated(Oval(6.9f, -22.3f, 0.85f, 3.9f, Inner), P(6.6f, -17.5f), -5f));
        _headMotion.Shapes.Add(_ears);
        _headMotion.Shapes.Add(Oval(7.2f, -14.4f, 4.5f, 4.5f, Body));
        _headMotion.Shapes.Add(Oval(9.5f, -12.6f, 2.4f, 1.8f, Belly));
        Eyes = Group(P(8.7f, -15.4f));
        Eyes.Shapes.Add(Oval(8.7f, -15.4f, 1f, 1.05f, Ink));
        Eyes.Shapes.Add(Oval(9.05f, -15.75f, 0.33f, 0.33f, Anim.Hex(0xFFFFFF)));
        _headMotion.Shapes.Add(Eyes);
        _nose = Oval(11.5f, -13.5f, 0.8f, 0.7f, Nose);
        _headMotion.Shapes.Add(_nose);
    }

    protected override void Pose(Mood mood, Mood previous)
    {
        bool lying = mood is Mood.Sleep or Mood.Doze;
        bool hopping = mood is Mood.Walk or Mood.Flee;

        To(_lift, "Offset.Y", lying ? 1.5f : 0f, 0.4);
        To(_bob, "Scale.Y", lying ? 0.86f : 1f, 0.4);
        if (mood == Mood.Idle) Loop(_ears, "RotationAngleInDegrees", Rng.Range(2.2f, 3f), 0f, -6f, 0.5f, 5f, 1f, -6f);
        else To(_ears, "RotationAngleInDegrees", lying ? -72f : hopping ? -16f : 0f, 0.3);

        float head = mood switch { Mood.Graze => 32f, Mood.Sleep or Mood.Doze => 10f, _ => 0f };
        To(_headPose, "RotationAngleInDegrees", head, 0.3);
        if (mood == Mood.Graze) Loop(_headMotion, "RotationAngleInDegrees", 0.35, 0f, -4f, 0.5f, 3f, 1f, -4f);
        else To(_headMotion, "RotationAngleInDegrees", 0f, 0.2);
        if (mood is Mood.Idle or Mood.Graze) Loop(_nose, "Offset.Y", 0.9, 0f, 0f, 0.25f, -0.55f, 0.5f, 0f, 1f, 0f);
        else To(_nose, "Offset.Y", 0f, 0.1);

        if (lying) SetEyes(false, 0.3);
        else StartBlinking();
    }

    protected override void NextMood()
    {
        switch (Mood)
        {
            case Mood.Walk:
                float r = Rng.Next();
                if (r < 0.05f) Enter(Mood.Sleep, Rng.Range(6f, 12f));
                else if (r < 0.55f) Enter(Mood.Graze, Rng.Range(2.5f, 4.5f));
                else if (r < 0.85f) Enter(Mood.Idle, Rng.Range(2f, 4f));
                else Bout();
                break;
            case Mood.Flee:
                Enter(Mood.Idle, Rng.Range(1.5f, 3f));
                break;
            default:
                Bout();
                break;
        }
    }

    private void Bout()
    {
        float dir = Rng.Chance(0.3f) ? -Facing : Facing;
        int hops = Rng.Int(3, 7);
        double gap = Rng.Range(0.05f, 0.2f);
        if (!TryBout(dir, Speed, 8f, hops, gap, Mood.Walk) && !TryBout(-dir, Speed, 8f, hops, gap, Mood.Walk))
            Enter(Mood.Idle, Rng.Range(1f, 2f));
    }

    /// <summary>Plans a run of hops in one keyframe track (hop, pause, hop...) and the matching arcs.</summary>
    private bool TryBout(float dir, float speed, float height, int hops, double gap, Mood mood)
    {
        float x = X;
        float step = speed * (float)HopSeconds;
        var legs = new List<(double, float)>();
        double t = 0;
        int done = 0;
        for (int k = 0; k < hops; k++)
        {
            float next = ClampX(x + dir * step);
            if (MathF.Abs(next - x) < 2f) break;
            x = next;
            t += HopSeconds;
            legs.Add((t, x));
            if (gap > 0)
            {
                t += gap;
                legs.Add((t, x));
            }
            done++;
        }
        if (done == 0) return false;
        Face(dir, 0.15);
        Travel(legs.ToArray());
        Enter(mood, t);
        HopArc(HopSeconds, height, done, gap);
        return true;
    }

    protected override void React(Vector2 click, bool direct)
    {
        if (Mood == Mood.Flee) return;
        float away = MathF.Abs(X - click.X) < 2f ? -Facing : (X > click.X ? 1f : -1f);
        PopAbove(Glyphs.Bang(9f), 9f);
        int hops = Full ? 7 : 3;
        if (!TryBout(away, Speed * 2f, 14f, hops, 0.03, Mood.Flee)) TryBout(-away, Speed * 2f, 14f, hops, 0.03, Mood.Flee);
        Herd.Grass.Push(X, 30f, 0.12f * away);
    }
}
