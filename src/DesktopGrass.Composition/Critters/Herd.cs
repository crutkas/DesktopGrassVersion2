// Herd.cs - one strip's ground critters: spawning, input routing and sheep greetings.
//
// Greetings are predicted rather than polled: whenever a sheep changes course, the herd samples both
// sheep's planned x(t) tracks and schedules one check for the moment they will meet.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal sealed class Herd : IDisposable
{
    private const float GreetDistance = 40f;

    public readonly Strip Strip;
    public readonly GrassField Grass;
    public readonly Scheduler Scheduler;
    public readonly ContainerVisual Layer;
    private readonly Rng _rng;
    private readonly List<Critter> _critters = new();
    private readonly HashSet<Critter> _dirty = new();
    private bool _planQueued, _retiring, _disposed;

    public MotionMode Motion { get; private set; }
    public float GroundY => Strip.GroundY;
    public float MinX => 6f;
    public float MaxX => Strip.Width - 6f;
    public IReadOnlyList<Critter> Critters => _critters;
    /// <summary>The town cast (pigeons, raccoons and a bodega cat) rather than the countryside one.</summary>
    public bool City { get; }

    public Herd(Strip strip, GrassField grass, Scheduler scheduler, Rng rng, MotionMode motion, SceneKind scene)
    {
        Strip = strip;
        Grass = grass;
        Scheduler = scheduler;
        _rng = rng;
        Motion = motion;
        City = scene == SceneKind.City;
        Layer = Anim.C.CreateContainerVisual();
        Layer.Opacity = 0;
        strip.Critters.Children.InsertAtTop(Layer);

        Spawn();
        foreach (var c in _critters) Layer.Children.InsertAtTop(c.Root);
        Layer.StartAnimation("Opacity", Anim.Scalar(0.9, Anim.EaseInOut, 1f, 1f));
        foreach (var c in _critters) c.Start();
        Log.Write($"critters: {string.Join(", ", _critters.Select(c => $"{c.Name} the {c.Species} at {c.X:F0}"))}");
    }

    private void Spawn()
    {
        float w = Strip.Width;
        var kinds = new List<string>();
        if (City)
        {
            if (w >= 600) kinds.Add("cat");
            for (int i = 0, flocks = w >= 3000 ? 2 : 1; i < flocks; i++) kinds.Add("pigeons");
        }
        else
        {
            int sheep = w >= 3000 ? 3 : w >= 900 ? 2 : 1;
            for (int i = 0; i < sheep; i++) kinds.Add("sheep");
            if (w >= 600) kinds.Add("cat");
            if (w >= 1000) kinds.Add("bunny");
            if (w >= 3000 || (w >= 1400 && _rng.Chance(0.55f))) kinds.Add("hedgehog");
        }
        int raccoons = w >= 1200 ? _rng.Int(2, 4) : w >= 600 ? 1 : 0;
        for (int i = 0; i < raccoons; i++) kinds.Add("raccoon");

        // Spread them out: each critter (or flock) gets its own slot of the strip, in random order.
        int n = kinds.Count;
        int[] slots = Enumerable.Range(0, n).OrderBy(_ => _rng.Next()).ToArray();
        float slotWidth = (w - 160f) / n;
        var used = new HashSet<string> { Raccoon.Jimothy };
        bool haveJimothy = false;
        for (int i = 0; i < n; i++)
        {
            float x = 80f + slotWidth * (slots[i] + _rng.Range(0.25f, 0.75f));
            var r = new Rng(_rng.NextU64());
            Critter c;
            switch (kinds[i])
            {
                case "sheep": c = new Sheep(this, r, Pick(Sheep.Names, used), x, _rng.Range(14f, 26f)); break;
                case "cat":
                    c = new Cat(this, r, Pick(City ? Cat.CityNames : Cat.Names, used), x, _rng.Range(10f, 22f), _rng.Int(0, Cat.CoatCount));
                    break;
                case "bunny": c = new Bunny(this, r, Pick(Bunny.Names, used), x, _rng.Range(22f, 38f)); break;
                case "raccoon":
                    // Every raccoon gang has exactly one Jimothy.
                    c = new Raccoon(this, r, haveJimothy ? Pick(Raccoon.Names, used) : Raccoon.Jimothy, x, _rng.Range(8f, 13f));
                    haveJimothy = true;
                    break;
                case "pigeons":
                    var flock = new Pigeon.Flock { Home = x };
                    for (int k = 0, size = _rng.Int(3, 6); k < size; k++)
                    {
                        float px = Math.Clamp(x + _rng.Range(-36f, 36f), MinX + 12f, MaxX - 12f);
                        _critters.Add(new Pigeon(this, new Rng(_rng.NextU64()), Pick(Pigeon.Names, used), px, _rng.Range(9f, 15f), flock));
                    }
                    continue;
                default: c = new Hedgehog(this, r, Pick(Hedgehog.Names, used), x, _rng.Range(4f, 8f)); break;
            }
            _critters.Add(c);
        }
    }

    private string Pick(string[] pool, HashSet<string> used)
    {
        for (int k = 0; k < 16; k++)
        {
            string name = _rng.Pick(pool);
            if (used.Add(name)) return name;
        }
        return _rng.Pick(pool);
    }

    public void SetMotion(MotionMode mode)
    {
        if (mode == Motion) return;
        Motion = mode;
        foreach (var c in _critters) c.SetMotion(mode);
    }

    public void Hover(Vector2? cursor)
    {
        foreach (var c in _critters) c.Hover(cursor);
    }

    /// <summary>Lets nearby critters react; returns true if the click landed on one of them.</summary>
    public bool Click(Vector2 p)
    {
        bool hit = false;
        foreach (var c in _critters) hit |= c.Click(p);
        return hit;
    }

    public void Pop(CompositionShape glyph, float size, Vector2 at, Vector2 rise, double seconds = 1.6) =>
        Fx.Drift(Strip.Fx, glyph, size, at, rise, seconds, scaleFrom: 0.4f, scaleTo: 1.1f);

    // ---------------------------------------------------------------- greetings

    public void Changed(Critter c)
    {
        if (_disposed || _retiring || c is not Sheep || Motion != MotionMode.Full) return;
        _dirty.Add(c);
        if (_planQueued) return;
        _planQueued = true;
        // Deferred so the critter's new track is in place before we predict from it.
        Scheduler.After(0, () =>
        {
            _planQueued = false;
            var batch = _dirty.ToArray();
            _dirty.Clear();
            foreach (var d in batch) Plan(d);
        });
    }

    private void Plan(Critter a)
    {
        if (_disposed || !a.CanGreet) return;
        double now = Clock.Now;
        foreach (var b in _critters)
        {
            if (b == a || b is not Sheep) continue;
            for (double dt = 0.25; dt <= 16; dt += 0.25)
            {
                if (MathF.Abs(a.XAt(now + dt) - b.XAt(now + dt)) > GreetDistance) continue;
                var (pa, pb) = (a, b);
                Scheduler.After(dt, () => TryGreet(pa, pb));
                break;
            }
        }
    }

    private void TryGreet(Critter a, Critter b)
    {
        if (_disposed || _retiring || !a.CanGreet || !b.CanGreet || MathF.Abs(a.X - b.X) > GreetDistance + 8f) return;
        double seconds = _rng.Range(1.6f, 2.8f);
        a.Greet(b, seconds);
        b.Greet(a, seconds);
        var mid = new Vector2((a.X + b.X) / 2, GroundY - MathF.Max(a.BodyHeight, b.BodyHeight) - 2f);
        Pop(Glyphs.Heart(11f), 11f, mid, new Vector2(0, -18f), 2.0);
        Log.Write($"{a.Name} and {b.Name} say hello");
    }

    // ---------------------------------------------------------------- lifetime

    /// <summary>Fades the herd out (scene change or critters turned off), then disposes it.</summary>
    public void Retire()
    {
        if (_disposed || _retiring) return;
        _retiring = true;
        foreach (var c in _critters) c.Stop();
        var batch = Anim.C.CreateScopedBatch(CompositionBatchTypes.Animation);
        Layer.StartAnimation("Opacity", Anim.Scalar(0.8, Anim.EaseInOut, 1f, 0f));
        batch.End();
        batch.Completed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var c in _critters) c.Dispose();
        _critters.Clear();
        try
        {
            Shapes.Discard(Layer);
        }
        catch (Exception)
        {
            // The strip was torn down first.
        }
    }
}
