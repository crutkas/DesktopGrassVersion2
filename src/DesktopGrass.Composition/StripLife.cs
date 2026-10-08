// StripLife.cs - everything alive on one strip besides the grass: the herd, the sky and the scene's
// weather and props. Applies scene, motion and the user's Critters/Sky toggles.
using System;
using System.Numerics;

namespace DesktopGrass.Composition;

internal sealed class StripLife : IDisposable
{
    private readonly Strip _strip;
    private readonly GrassField _grass;
    private readonly Scheduler _scheduler;
    private readonly Rng _rng;
    private readonly Sky _sky;
    private readonly Ambience _ambience;
    private Herd? _herd;
    private bool _disposed;

    public StripLife(Strip strip, GrassField grass, Scheduler scheduler, Rng rng)
    {
        _strip = strip;
        _grass = grass;
        _scheduler = scheduler;
        _rng = rng;
        _sky = new Sky(strip, scheduler, new Rng(rng.NextU64()));
        _ambience = new Ambience(strip, scheduler, new Rng(rng.NextU64()));
    }

    public Herd? Herd => _herd;

    /// <summary>Scenes with ground critters (the others have their own residents: fish, tumbleweeds).</summary>
    public static bool HasCritters(SceneKind scene) => scene is SceneKind.Meadow or SceneKind.Autumn or SceneKind.Winter;

    public void Apply(SceneKind scene, MotionMode motion, bool critters, bool sky)
    {
        if (_disposed) return;
        bool wantHerd = critters && HasCritters(scene);
        if (wantHerd && _herd == null) _herd = new Herd(_strip, _grass, _scheduler, new Rng(_rng.NextU64()), motion);
        else if (!wantHerd && _herd != null)
        {
            _herd.Retire();
            _herd = null;
        }
        else _herd?.SetMotion(motion);
        _sky.Apply(scene, motion, sky);
        _ambience.Apply(scene, motion);
    }

    public void Hover(Vector2? cursor) => _herd?.Hover(cursor);

    /// <summary>Returns true when the click landed on a critter (so it shouldn't also cut the grass).</summary>
    public bool Click(Vector2 p) => _herd?.Click(p) ?? false;

    public void SendBirds() => _sky.Flock();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _herd?.Dispose();
        _herd = null;
        _sky.Dispose();
        _ambience.Dispose();
    }
}
