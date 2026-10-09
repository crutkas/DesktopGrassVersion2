// Wind.cs - global wind driven entirely by composition animations (DWM evaluates it; the app
// process does no per-frame work), plus the shared, animatable grass brushes.
using System;
using Windows.UI;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal enum MotionMode { Full, Calm }

/// <summary>
/// W.P1 / W.P2 / W.Wave are looping phases, W.Amp scales ambient sway (0 in Calm mode) and
/// W.Lean is a slowly drifting prevailing-wind lean.
/// </summary>
internal sealed class Wind
{
    public readonly CompositionPropertySet Set;
    private readonly Scheduler _scheduler;
    private TimerToken? _stopToken;

    public MotionMode Mode { get; private set; } = MotionMode.Calm;

    public Wind(Compositor compositor, Scheduler scheduler)
    {
        _scheduler = scheduler;
        Set = compositor.CreatePropertySet();
        Set.InsertScalar("P1", 0);
        Set.InsertScalar("P2", 0);
        Set.InsertScalar("Wave", 0);
        Set.InsertScalar("Amp", 0);
        Set.InsertScalar("Lean", 0);
    }

    public void SetMode(MotionMode mode, bool animate)
    {
        Mode = mode;
        _stopToken?.Cancel();
        if (mode == MotionMode.Full)
        {
            Set.StartAnimation("P1", Anim.ScalarLoop(5.0, Anim.Linear, 0f, 0f, 1f, MathF.Tau));
            Set.StartAnimation("P2", Anim.ScalarLoop(3.7, Anim.Linear, 0f, 0f, 1f, MathF.Tau));
            Set.StartAnimation("Wave", Anim.ScalarLoop(9.0, Anim.Linear, 0f, 0f, 1f, MathF.Tau));
            Set.StartAnimation("Lean", Anim.ScalarLoop(27.0, Anim.EaseInOut, 0f, 0.03f, 0.3f, 0.09f, 0.55f, 0.02f, 0.8f, 0.11f, 1f, 0.03f));
            Set.StartAnimation("Amp", Anim.Scalar(animate ? 1.5 : 0.01, Anim.EaseInOut, 1f, 1f));
        }
        else
        {
            Set.StartAnimation("Amp", Anim.Scalar(animate ? 1.2 : 0.01, Anim.EaseInOut, 1f, 0f));
            _stopToken = _scheduler.After(animate ? 1.4 : 0.05, () =>
            {
                foreach (var p in new[] { "P1", "P2", "Wave", "Lean", "Amp" }) Set.StopAnimation(p);
                Set.InsertScalar("Amp", 0);
                Set.InsertScalar("Lean", 0);
            });
        }
    }
}

/// <summary>
/// Shared clock for the gust springs. K.T runs (a linear animation on DWM) only while something is
/// springing; the app mirrors it as Clock.Now - start, so a gust impulse is three InsertScalar calls
/// on the group's property set and never restarts an animation.
/// </summary>
internal sealed class GustClock
{
    // Spring shape: rises toward the push with rate Rise, decays with Decay and rings at Omega.
    public const float Rise = 28f;
    public const float Decay = 2.0f;
    public const float Omega = 5.0f;
    public const double IdleStopSeconds = 3.4;
    private const double MaxRunSeconds = 1800;

    public readonly CompositionPropertySet Set;
    private readonly Scheduler _scheduler;
    private double _start, _lastTouch;
    private bool _running;
    private TimerToken? _token;

    /// <summary>Raised just before the clock stops so fields can zero their (fully decayed) springs.</summary>
    public event Action? Stopped;

    public bool Running => _running;

    public GustClock(Compositor compositor, Scheduler scheduler)
    {
        _scheduler = scheduler;
        Set = compositor.CreatePropertySet();
        Set.InsertScalar("T", 0);
    }

    /// <summary>Makes sure the clock runs for at least IdleStopSeconds more; returns the current T.</summary>
    public float Touch()
    {
        double now = Clock.Now;
        if (_running && now - _start > MaxRunSeconds) Stop();
        if (!_running)
        {
            Set.StartAnimation("T", Anim.Scalar(100000, Anim.Linear, 0f, 0f, 1f, 100000f));
            _start = now;
            _running = true;
        }
        _lastTouch = now;
        if (_token == null || !_token.Pending) _token = _scheduler.After(IdleStopSeconds, Check);
        return (float)(now - _start);
    }

    private void Check()
    {
        double idle = Clock.Now - _lastTouch;
        if (idle >= IdleStopSeconds - 0.01) Stop();
        else _token = _scheduler.After(IdleStopSeconds - idle, Check);
    }

    public void Stop()
    {
        _token?.Cancel();
        _token = null;
        if (!_running) return;
        _running = false;
        Stopped?.Invoke();
        Set.StopAnimation("T");
    }

    /// <summary>C# mirror of the spring term in the grass expression (d = seconds since the push).</summary>
    public static float Value(float v, float p, float d)
    {
        if (d <= 0) return v;
        return (v + (p - v) * (1 - MathF.Exp(-Rise * d))) * MathF.Exp(-Decay * d) * MathF.Cos(Omega * d);
    }
}

/// <summary>
/// Grass colors for a palette (the tiles are rasterized with these), plus the one shared composition brush:
/// the turf band, whose colors crossfade on a scene change.
/// </summary>
internal sealed class GrassBrushes
{
    /// <summary>Soft band along the ground line that closes gaps between blade bases.</summary>
    public readonly CompositionLinearGradientBrush Turf;
    private readonly CompositionColorGradientStop _turfTop, _turfBottom;

    public Palette Palette { get; private set; }

    public GrassBrushes(Compositor c, Palette palette)
    {
        Palette = palette;
        var turf = TurfColor(palette);
        Turf = c.CreateLinearGradientBrush();
        Turf.MappingMode = CompositionMappingMode.Relative;
        Turf.StartPoint = new System.Numerics.Vector2(0.5f, 0f);
        Turf.EndPoint = new System.Numerics.Vector2(0.5f, 1f);
        _turfTop = c.CreateColorGradientStop(0f, Anim.WithAlpha(turf, 0));
        _turfBottom = c.CreateColorGradientStop(1f, turf);
        Turf.ColorStops.Add(_turfTop);
        Turf.ColorStops.Add(_turfBottom);
    }

    private static Color TurfColor(Palette p) => Colors(p, 1, 2).Base;

    /// <summary>Base (ground) and tip colors of a blade of the given depth (0 back .. 2 front) and hue.</summary>
    public static (Color Base, Color Tip) Colors(Palette p, int depth, int hue)
    {
        Color tip = p.Blades[hue];
        float shade = p.BaseShade;
        if (depth == 0)
        {
            tip = Anim.Darken(tip, 0.2f);
            shade = Math.Min(0.85f, shade + 0.1f);
        }
        else if (depth == 2)
        {
            tip = Anim.Lighten(tip, 0.06f);
        }
        return (Anim.Darken(tip, shade), tip);
    }

    public void SetPalette(Palette palette, bool animate)
    {
        Palette = palette;
        var turf = TurfColor(palette);
        SetColor(_turfTop, Anim.WithAlpha(turf, 0), animate);
        SetColor(_turfBottom, turf, animate);
    }

    private static void SetColor(CompositionColorGradientStop stop, Color color, bool animate)
    {
        if (animate)
        {
            stop.StartAnimation("Color", Anim.Color(1.6, color));
            return;
        }
        stop.StopAnimation("Color");
        stop.Color = color;
    }
}