// City.cs - the City scene, New York meets Chicago: a skyline mixing the Empire State, Chrysler and One
// World Trade Center with the Willis Tower, John Hancock Center and Marina City; rooftop water towers;
// the elevated "L" and its trains; a Ferris wheel; and the Bean in the park.
//
// The skyline is static art: it is generated as a display list and rasterized once with Direct2D into
// pixel-snapped tiles (CompositionDrawingSurfaces), like the grass. Motion stays in composition: the
// wheel's sprite turns, an L train is a textured sprite sliding along the track, and a few window sprites
// switch on and off. Those run only in Full motion.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using DesktopGrass.Composition.Interop;
using Windows.UI;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal sealed class CityScape : IDisposable
{
    private const float TileWidth = 512f;
    /// <summary>How far above the ground the skyline may reach (the tallest spire is ~97 DIP).</summary>
    private const float SkyHeight = 102f;
    /// <summary>Top of the L's deck, above the ground line.</summary>
    private const float DeckHeight = 33f;
    private const float WheelRadius = 19f;
    private const float WheelHub = 64f;
    private const float CarLength = 22f, CarGap = 1.4f, TrainHeight = 9f;

    private enum Op : byte { Rect, Oval, Line, Ring, Poly }
    private enum Tone : byte { Solid, Far, Mid, Dark, Near }
    private enum Landmark { EmpireState, Chrysler, OneWtc, Willis, Hancock, MarinaCity, Tower }

    private readonly record struct Piece(Op Op, Tone Tone, Color Color, float A, float B, float C, float D, float E, float[]? Points, float Left, float Right);

    // Each row of buildings is a soft vertical gradient: hazy far towers, landmarks, dark landmarks, near blocks.
    private static readonly Color[] ToneTop = { default, Anim.Hex(0xB9C4D1), Anim.Hex(0x909DAF), Anim.Hex(0x6E7889), Anim.Hex(0x627083) };
    private static readonly Color[] ToneBottom = { default, Anim.Hex(0xA6B2C1), Anim.Hex(0x7E8B9D), Anim.Hex(0x5C6676), Anim.Hex(0x515C6C) };
    private static readonly Color Lit = Anim.Hex(0xFFE4A3);
    private static readonly Color FarLit = Anim.Hex(0xF6E7C4, 200);
    private static readonly Color Shade = Anim.Hex(0x000000, 30);
    private static readonly Color Shine = Anim.Hex(0xFFFFFF, 34);
    private static readonly Color Steel = Anim.Hex(0xCDD5DE);
    private static readonly Color SteelEdge = Anim.Hex(0x98A4B1);
    private static readonly Color Antenna = Anim.Hex(0xE6EBF0);
    private static readonly Color Beacon = Anim.Hex(0xE5484D);
    private static readonly Color Track = Anim.Hex(0x3D4A46);
    private static readonly Color Rail = Anim.Hex(0x707E78);
    private static readonly Color Wood = Anim.Hex(0x7A6052), WoodDark = Anim.Hex(0x5B4639);
    private static readonly Color WheelSteel = Anim.Hex(0x7F8C9B);
    private static readonly Color[] Gondolas =
        { Anim.Hex(0xE8505B), Anim.Hex(0xF2C14E), Anim.Hex(0x4C8FE0), Anim.Hex(0x5BBF8A), Anim.Hex(0xF28CB1), Anim.Hex(0x9B6BD3) };

    private readonly Strip _strip;
    private readonly Rng _rng;
    private readonly float _ground, _top, _deckTop;
    private readonly List<Piece> _pieces = new();
    private readonly List<Vector4> _darkWindows = new();
    private readonly List<(Canvas Canvas, CompositionSurfaceBrush Brush)> _surfaces = new();
    private readonly Dictionary<int, (CompositionSurfaceBrush Brush, float Length)> _trains = new();
    private float[] _centers = Array.Empty<float>();
    private float _wheelX, _beanX;
    private SpriteVisual? _wheel;
    private bool _turning, _disposed;

    /// <summary>Builds the skyline (and wheel) into <paramref name="skyline"/> and the Bean into <paramref name="park"/>.</summary>
    public CityScape(Strip strip, Rng rng, ContainerVisual skyline, ContainerVisual park)
    {
        var sw = Stopwatch.StartNew();
        _strip = strip;
        _rng = rng;
        _ground = strip.GroundY;
        _top = _ground - SkyHeight;
        _deckTop = _ground - DeckHeight;
        var landmarks = Generate();
        int pieces = _pieces.Count;
        int tiles = Rasterize(skyline);
        _wheel = BuildWheel(skyline);
        BuildBean(park);
        _pieces.Clear();
        Log.Write($"city: {string.Join(", ", landmarks)}; {pieces} pieces in {tiles} tiles, {sw.ElapsedMilliseconds} ms");
    }

    // ---------------------------------------------------------------- layout

    private List<Landmark> Generate()
    {
        float w = _strip.Width;
        int clusters = Math.Max(1, (int)MathF.Round(w / 700f));
        _centers = new float[clusters];
        for (int i = 0; i < clusters; i++) _centers[i] = w * (i + 0.5f) / clusters + _rng.Range(-40f, 40f);

        // Each downtown pairs a New York landmark with a Chicago one; each appears at most once per monitor.
        var nyc = Shuffled(Landmark.EmpireState, Landmark.Chrysler, Landmark.OneWtc);
        var chi = Shuffled(Landmark.Willis, Landmark.Hancock, Landmark.MarinaCity);
        var placed = new List<(Landmark Kind, float X)>();
        for (int i = 0; i < clusters; i++)
        {
            float gap = _rng.Range(30f, 44f);
            var a = i < nyc.Length ? nyc[i] : Landmark.Tower;
            var b = i < chi.Length ? chi[i] : Landmark.Tower;
            if (_rng.Chance(0.5f)) (a, b) = (b, a);
            placed.Add((a, _centers[i] - gap));
            placed.Add((b, _centers[i] + gap));
        }

        _wheelX = Math.Clamp(clusters >= 2 ? (_centers[0] + _centers[1]) / 2 : w * 0.8f, 40f, w - 40f);
        _beanX = Math.Clamp(clusters >= 3 ? (_centers[1] + _centers[2]) / 2 : clusters == 2 ? _centers[1] + 230f : w * 0.25f, 40f, w - 40f);

        // Back to front: hazy far towers, the landmarks, the wheel's legs, the near blocks, then the L.
        for (float x = -12f; x < w + 12f;)
        {
            float bw = _rng.Range(16f, 38f);
            float h = Lerp(15f, 46f, Prox(x + bw / 2)) * _rng.Range(0.75f, 1.2f);
            FarBuilding(x, bw, h);
            x += bw - 0.5f;
        }
        foreach (var (kind, x) in placed) DrawLandmark(kind, x);
        WheelLegs();
        for (float x = -10f; x < w + 10f;)
        {
            if (_rng.Chance(0.13f))
            {
                x += _rng.Range(4f, 14f);
                continue;
            }
            float bw = _rng.Range(14f, 30f);
            float cx = x + bw / 2;
            float h = Lerp(11f, 29f, Prox(cx)) * _rng.Range(0.8f, 1.2f);
            // Blocks in front of a landmark or the wheel stay low so those read.
            foreach (var (_, lx) in placed)
            {
                if (MathF.Abs(cx - lx) < bw / 2 + 18f) h = MathF.Min(h, 19f);
            }
            if (MathF.Abs(cx - _wheelX) < bw / 2 + 12f) h = MathF.Min(h, 16f);
            NearBuilding(x, bw, h);
            x += bw - 0.5f;
        }
        ElevatedTrack(w);

        var kinds = new List<Landmark>();
        foreach (var (kind, _) in placed) kinds.Add(kind);
        return kinds;
    }

    /// <summary>How "downtown" a spot is: 1 at a cluster center, falling off over a few hundred DIPs.</summary>
    private float Prox(float x)
    {
        float best = 0;
        foreach (float c in _centers) best = MathF.Max(best, MathF.Exp(-Sq((x - c) / 240f)));
        return best;
    }

    private Landmark[] Shuffled(params Landmark[] items)
    {
        for (int i = items.Length - 1; i > 0; i--)
        {
            int j = _rng.Int(0, i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
        return items;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Sq(float v) => v * v;

    // ---------------------------------------------------------------- generic buildings

    private void FarBuilding(float x, float bw, float h)
    {
        float top = _ground - h;
        Rect(x, top, bw, h, Tone.Far);
        float r = _rng.Next();
        if (r < 0.25f)
        {
            float sw = bw * _rng.Range(0.45f, 0.7f), sh = h * _rng.Range(0.1f, 0.2f);
            Rect(x + (bw - sw) / 2, top - sh, sw, sh, Tone.Far);
        }
        else if (r < 0.42f)
        {
            float ax = x + bw * _rng.Range(0.3f, 0.7f), ah = _rng.Range(5f, 12f);
            Rect(ax - 0.4f, top - ah, 0.8f, ah, Tone.Far);
        }
        Windows(x + 2f, x + bw - 2f, top + 2.5f, _ground - 4f, 0.07f, far: true);
    }

    private void NearBuilding(float x, float bw, float h)
    {
        float top = _ground - h;
        Rect(x, top, bw, h, Tone.Near);
        Rect(x, top, 1f, h, Shine);
        float r = _rng.Next();
        if (r < 0.3f && bw >= 13f)
        {
            WaterTower(x + _rng.Range(2.5f, bw - 7.5f), top);
        }
        else if (r < 0.5f)
        {
            // Prewar cornice.
            Rect(x - 0.6f, top - 0.9f, bw + 1.2f, 0.9f, Tone.Near);
            Rect(x - 0.6f, top - 0.9f, bw + 1.2f, 0.9f, Shade);
        }
        else if (r < 0.65f)
        {
            float sw = bw * _rng.Range(0.5f, 0.75f), sh = _rng.Range(3f, 6f);
            Rect(x + (bw - sw) / 2, top - sh, sw, sh, Tone.Near);
            Rect(x + (bw - sw) / 2, top - sh, 1f, sh, Shine);
        }
        else if (r < 0.75f)
        {
            Rect(x + _rng.Range(2f, bw - 6f), top - 2.4f, 4f, 2.4f, Tone.Near);
        }
        Windows(x + 2f, x + bw - 2f, top + 3f, _ground - 5f, _rng.Range(0.08f, 0.28f), far: false);
    }

    /// <summary>A grid of windows, some lit. Unlit near windows above the grass become candidates for lights switching on.</summary>
    private void Windows(float left, float right, float top, float bottom, float p, bool far)
    {
        float pitchX = far ? 3f : 3.2f, pitchY = far ? 4f : 4.2f;
        float ww = far ? 1.1f : 1.5f, wh = far ? 1.5f : 2.1f;
        if (right - left < ww) return;
        int cols = (int)((right - left - ww) / pitchX) + 1;
        float x0 = left + (right - left - ((cols - 1) * pitchX + ww)) / 2;
        for (float y = top; y + wh <= bottom; y += pitchY)
        {
            for (int c = 0; c < cols; c++)
            {
                float wx = x0 + c * pitchX;
                if (_rng.Chance(p)) Rect(wx, y, ww, wh, far ? FarLit : Lit);
                else if (!far && y + wh < _ground - 22f && (y > _deckTop + 3f || y + wh < _deckTop - 1f)) _darkWindows.Add(new Vector4(wx, y, ww, wh));
            }
        }
    }

    /// <summary>The classic New York rooftop water tank on its little stilts.</summary>
    private void WaterTower(float x, float roof)
    {
        const float tw = 5f, legs = 2.4f, th = 4.4f;
        float tankTop = roof - legs - th;
        Line(x + 0.9f, roof, x + 1.2f, roof - legs, 0.55f, WoodDark);
        Line(x + tw - 0.9f, roof, x + tw - 1.2f, roof - legs, 0.55f, WoodDark);
        Rect(x, tankTop, tw, th, Wood);
        Rect(x, tankTop + 1.3f, tw, 0.35f, WoodDark);
        Rect(x, tankTop + 3.1f, tw, 0.35f, WoodDark);
        Poly(WoodDark, x - 0.4f, tankTop + 0.2f, x + tw + 0.4f, tankTop + 0.2f, x + tw / 2, tankTop - 2.3f);
    }

    /// <summary>Chicago's elevated "L": a steel girder deck on bents, with the running rail on top.</summary>
    private void ElevatedTrack(float w)
    {
        float top = _deckTop, b = _ground;
        for (float x = 18f; x < w; x += 46f)
        {
            Rect(x - 0.9f, top + 2.6f, 1.8f, b - top - 2.6f, Track);
            Line(x, top + 7f, x - 4.5f, top + 2.6f, 0.8f, Track);
            Line(x, top + 7f, x + 4.5f, top + 2.6f, 0.8f, Track);
        }
        Rect(-10f, top, w + 20f, 2.6f, Track);
        Rect(-10f, top + 0.5f, w + 20f, 0.35f, Rail);
        Rect(-10f, top - 0.6f, w + 20f, 0.6f, Rail);
    }

    /// <summary>The A-frame under the Ferris wheel (the turning wheel is its own sprite).</summary>
    private void WheelLegs()
    {
        float b = _ground, x = _wheelX, hub = b - WheelHub;
        Line(x, hub, x - 10f, b, 1.5f, WheelSteel);
        Line(x, hub, x + 10f, b, 1.5f, WheelSteel);
        Line(x - 5.9f, b - 26f, x + 5.9f, b - 26f, 0.8f, WheelSteel);
        Rect(x - 13f, b - 3f, 26f, 3f, WheelSteel);
    }

    // ---------------------------------------------------------------- display list

    private void Rect(float x, float y, float w, float h, Tone tone) => Add(Op.Rect, tone, default, x, y, w, h, 0, null, x, x + w);

    private void Rect(float x, float y, float w, float h, Color color) => Add(Op.Rect, Tone.Solid, color, x, y, w, h, 0, null, x, x + w);

    private void Oval(float cx, float cy, float rx, float ry, Tone tone) => Add(Op.Oval, tone, default, cx, cy, rx, ry, 0, null, cx - rx, cx + rx);

    private void Oval(float cx, float cy, float rx, float ry, Color color) => Add(Op.Oval, Tone.Solid, color, cx, cy, rx, ry, 0, null, cx - rx, cx + rx);

    private void Ring(float cx, float cy, float rx, float ry, float width, Color color) =>
        Add(Op.Ring, Tone.Solid, color, cx, cy, rx, ry, width, null, cx - rx - width, cx + rx + width);

    private void Line(float x1, float y1, float x2, float y2, float width, Color color) =>
        Add(Op.Line, Tone.Solid, color, x1, y1, x2, y2, width, null, MathF.Min(x1, x2) - width, MathF.Max(x1, x2) + width);

    private void Poly(Tone tone, params float[] xy) => AddPoly(tone, default, xy);

    private void Poly(Color color, params float[] xy) => AddPoly(Tone.Solid, color, xy);

    private void AddPoly(Tone tone, Color color, float[] xy)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < xy.Length; i += 2)
        {
            lo = MathF.Min(lo, xy[i]);
            hi = MathF.Max(hi, xy[i]);
        }
        Add(Op.Poly, tone, color, 0, 0, 0, 0, 0, xy, lo, hi);
    }

    private void Add(Op op, Tone tone, Color color, float a, float b, float c, float d, float e, float[]? points, float left, float right) =>
        _pieces.Add(new Piece(op, tone, color, a, b, c, d, e, points, left, right));

    // ---------------------------------------------------------------- landmarks (x is the center; heights are DIP above ground)

    private void DrawLandmark(Landmark kind, float x)
    {
        switch (kind)
        {
            case Landmark.EmpireState: EmpireState(x); break;
            case Landmark.Chrysler: Chrysler(x); break;
            case Landmark.OneWtc: OneWtc(x); break;
            case Landmark.Willis: Willis(x); break;
            case Landmark.Hancock: Hancock(x); break;
            case Landmark.MarinaCity: MarinaCity(x); break;
            default: Tower(x); break;
        }
    }

    /// <summary>Setbacks climbing to the mooring mast and antenna, with a red beacon on top.</summary>
    private void EmpireState(float x)
    {
        float b = _ground;
        Rect(x - 13f, b - 12f, 26f, 12f, Tone.Mid);
        Rect(x - 9f, b - 50f, 18f, 38f, Tone.Mid);
        Rect(x - 9f, b - 50f, 2.4f, 38f, Shade);
        Rect(x + 6.6f, b - 50f, 2.4f, 38f, Shade);
        Windows(x - 6f, x + 6f, b - 48f, b - 14f, 0.12f, far: false);
        Rect(x - 7f, b - 57f, 14f, 7f, Tone.Mid);
        Windows(x - 5f, x + 5f, b - 56f, b - 51f, 0.2f, far: false);
        Rect(x - 5f, b - 62f, 10f, 5f, Tone.Mid);
        Rect(x - 3.6f, b - 66f, 7.2f, 4f, Tone.Mid);
        Rect(x - 2.4f, b - 70f, 4.8f, 4f, Tone.Mid);
        Rect(x - 1.3f, b - 76f, 2.6f, 6f, Tone.Mid);
        Rect(x - 0.45f, b - 86f, 0.9f, 10f, Antenna);
        Oval(x, b - 86.2f, 0.85f, 0.85f, Beacon);
    }

    /// <summary>A slim shaft under the terraced steel crown with its sunburst windows and needle.</summary>
    private void Chrysler(float x)
    {
        float b = _ground;
        Rect(x - 10f, b - 14f, 20f, 14f, Tone.Mid);
        Rect(x - 6.5f, b - 48f, 13f, 34f, Tone.Mid);
        Rect(x - 6.5f, b - 48f, 1.8f, 34f, Shade);
        Windows(x - 4f, x + 5f, b - 46f, b - 16f, 0.12f, far: false);
        for (int i = 0; i < 5; i++)
        {
            float rx = 6.5f - i * 1.2f, cy = b - 48f - i * 2.9f;
            Oval(x, cy, rx, 3.2f, Steel);
            Ring(x, cy, rx, 3.2f, 0.35f, SteelEdge);
        }
        for (int i = 0; i < 4; i++)
        {
            float cy = b - 48f - i * 2.9f;
            Poly(Lit, x - 0.7f, cy - 0.9f, x + 0.7f, cy - 0.9f, x, cy - 2.5f);
            if (i >= 2) continue;
            float side = 6.5f - i * 1.2f;
            Poly(Lit, x - side * 0.6f - 0.55f, cy - 0.4f, x - side * 0.6f + 0.55f, cy - 0.4f, x - side * 0.58f, cy - 1.7f);
            Poly(Lit, x + side * 0.6f - 0.55f, cy - 0.4f, x + side * 0.6f + 0.55f, cy - 0.4f, x + side * 0.58f, cy - 1.7f);
        }
        Poly(Steel, x - 0.8f, b - 61f, x + 0.8f, b - 61f, x, b - 74f);
    }

    /// <summary>A tapering shaft whose chamfered faces catch the light, topped by the tall spire.</summary>
    private void OneWtc(float x)
    {
        float b = _ground;
        Rect(x - 9f, b - 13f, 18f, 13f, Tone.Mid);
        Poly(Tone.Mid, x - 9f, b - 13f, x + 9f, b - 13f, x + 4.6f, b - 74f, x - 4.6f, b - 74f);
        Poly(Shine, x - 9f, b - 13f, x + 9f, b - 13f, x, b - 74f);
        Windows(x - 3f, x + 3f, b - 60f, b - 16f, 0.1f, far: false);
        Rect(x - 4.6f, b - 76f, 9.2f, 2f, Tone.Mid);
        Rect(x - 1.4f, b - 78f, 2.8f, 2f, Steel);
        Rect(x - 0.4f, b - 97f, 0.8f, 19f, Antenna);
        Oval(x, b - 97.2f, 0.75f, 0.75f, Beacon);
    }

    /// <summary>Bundled tubes stepping down at different heights, with the two white-tipped antennas.</summary>
    private void Willis(float x)
    {
        float b = _ground;
        (float Left, float Height)[] tubes = { (x - 9f, 50f), (x - 3f, 80f), (x + 3f, 64f) };
        foreach (var (left, h) in tubes)
        {
            Rect(left, b - h, 6f, h, Tone.Dark);
            Windows(left + 1.2f, left + 4.8f, b - h + 3f, b - 6f, 0.1f, far: false);
            foreach (float belt in new[] { 22f, 44f, 62f, 76f })
            {
                if (belt < h - 2f) Rect(left, b - belt, 6f, 0.8f, Shade);
            }
        }
        Rect(x - 3f, b - 80f, 0.8f, 30f, Shine);
        Rect(x - 1.9f, b - 95f, 0.8f, 15f, Tone.Dark);
        Rect(x + 1.1f, b - 93f, 0.8f, 13f, Tone.Dark);
        Rect(x - 1.9f, b - 95f, 0.8f, 2.5f, Antenna);
        Rect(x + 1.1f, b - 93f, 0.8f, 2.5f, Antenna);
    }

    /// <summary>The tapered dark tower with stacked X-braces and twin antennas.</summary>
    private void Hancock(float x)
    {
        float b = _ground;
        const float h = 62f, w0 = 11f, w1 = 6.5f;
        float Half(float y) => w0 - (w0 - w1) * y / h;
        Poly(Tone.Dark, x - w0, b, x + w0, b, x + w1, b - h, x - w1, b - h);
        Windows(x - 3f, x + 3f, b - h + 4f, b - 6f, 0.08f, far: false);
        var brace = Anim.Hex(0x8A94A3, 150);
        for (int i = 0; i < 5; i++)
        {
            float y0 = h * i / 5f, y1 = h * (i + 1) / 5f;
            Line(x - Half(y0), b - y0, x + Half(y1), b - y1, 0.7f, brace);
            Line(x + Half(y0), b - y0, x - Half(y1), b - y1, 0.7f, brace);
            Line(x - Half(y1), b - y1, x + Half(y1), b - y1, 0.5f, brace);
        }
        Rect(x - w1, b - h - 2f, 2 * w1, 2f, Tone.Dark);
        Rect(x - 3.6f, b - h - 20f, 0.8f, 18f, Tone.Dark);
        Rect(x + 2.8f, b - h - 18f, 0.8f, 16f, Tone.Dark);
        Rect(x - 3.6f, b - h - 20f, 0.8f, 2f, Antenna);
        Rect(x + 2.8f, b - h - 18f, 0.8f, 2f, Antenna);
    }

    /// <summary>The twin "corncob" towers: scalloped balconies over a spiral parking base.</summary>
    private void MarinaCity(float x)
    {
        float b = _ground;
        var rail = Anim.Hex(0xE3E8EE, 150);
        foreach (float cx in new[] { x - 8.5f, x + 8.5f })
        {
            const float r = 5.6f, h = 34f;
            Rect(cx - r, b - h, 2 * r, h, Tone.Mid);
            Oval(cx, b - h, r, 1.6f, Tone.Mid);
            for (float y = b - 12f; y > b - h + 1f; y -= 2.1f)
            {
                Oval(cx - r, y, 1f, 0.95f, Tone.Mid);
                Oval(cx + r, y, 1f, 0.95f, Tone.Mid);
                Rect(cx - r - 0.6f, y - 0.25f, 2 * r + 1.2f, 0.4f, rail);
            }
            Rect(cx - r, b - 12f, 2 * r, 12f, Shade);
            for (float y = b - 11f; y < b - 1f; y += 1.8f) Rect(cx - r, y, 2 * r, 0.35f, Shine);
        }
    }

    /// <summary>A generic tall tower for downtowns beyond the six landmarks.</summary>
    private void Tower(float x)
    {
        float b = _ground, h = _rng.Range(54f, 70f), hw = _rng.Range(6f, 9f);
        Rect(x - hw, b - h, 2 * hw, h, Tone.Mid);
        Rect(x - hw, b - h, 1.4f, h, Shine);
        Windows(x - hw + 1.5f, x + hw - 1.5f, b - h + 3f, b - 6f, 0.14f, far: false);
        if (_rng.Chance(0.5f))
        {
            Rect(x - hw * 0.7f, b - h - 4f, hw * 1.4f, 4f, Tone.Mid);
            Rect(x - hw * 0.4f, b - h - 7f, hw * 0.8f, 3f, Tone.Mid);
            Rect(x - 0.4f, b - h - 14f, 0.8f, 7f, Antenna);
        }
        else
        {
            Rect(x - hw * 0.5f, b - h - 2f, hw, 2f, Tone.Mid);
        }
    }

    // ---------------------------------------------------------------- rasterizing

    /// <summary>Draws the display list into pixel-snapped tiles; returns how many.</summary>
    private int Rasterize(ContainerVisual layer)
    {
        float k = _strip.Scale, w = _strip.Width;
        float y0 = MathF.Floor(_top * k) / k;
        int heightPx = (int)MathF.Ceiling((_ground + 1f - y0) * k);
        int tiles = 0;
        for (float left = 0; left < w; left += TileWidth)
        {
            float x0 = MathF.Floor(left * k) / k, right = MathF.Min(w, left + TileWidth);
            var canvas = new Canvas((int)MathF.Ceiling((right - x0) * k), heightPx);
            var toPixels = Matrix3x2.CreateTranslation(-x0, -y0) * Matrix3x2.CreateScale(k);
            float lo = x0 - 1f, hi = x0 + canvas.PixelWidth / k + 1f;
            canvas.Draw(p =>
            {
                p.Transform(toPixels);
                foreach (var piece in _pieces)
                {
                    if (piece.Right >= lo && piece.Left <= hi) Draw(p, piece);
                }
            });
            AddSprite(layer, canvas, x0, y0);
            tiles++;
        }
        return tiles;
    }

    private void Draw(Painter p, in Piece piece)
    {
        IntPtr brush = piece.Tone == Tone.Solid
            ? p.Solid(piece.Color)
            : p.Linear(ToneTop[(int)piece.Tone], ToneBottom[(int)piece.Tone], new Vector2(0, _top), new Vector2(0, _ground));
        switch (piece.Op)
        {
            case Op.Rect: p.Rect(piece.A, piece.B, piece.C, piece.D, brush); break;
            case Op.Oval: p.Ellipse(piece.A, piece.B, piece.C, piece.D, brush); break;
            case Op.Line: p.Line(piece.A, piece.B, piece.C, piece.D, piece.E, brush); break;
            case Op.Ring: p.StrokeEllipse(piece.A, piece.B, piece.C, piece.D, piece.E, brush); break;
            case Op.Poly:
                IntPtr geometry = new PathBuilder(winding: true).Polygon(piece.Points!).BuildD2D();
                try
                {
                    p.Fill(geometry, brush);
                }
                finally
                {
                    Marshal.Release(geometry);
                }
                break;
        }
    }

    private SpriteVisual AddSprite(ContainerVisual layer, Canvas canvas, float x, float y)
    {
        float k = _strip.Scale;
        var brush = Anim.C.CreateSurfaceBrush(canvas.Surface);
        brush.Stretch = CompositionStretch.Fill;
        _surfaces.Add((canvas, brush));
        var sprite = Anim.C.CreateSpriteVisual();
        sprite.Brush = brush;
        sprite.Size = new Vector2(canvas.PixelWidth / k, canvas.PixelHeight / k);
        sprite.Offset = new Vector3(x, y, 0);
        layer.Children.InsertAtTop(sprite);
        return sprite;
    }

    // ---------------------------------------------------------------- the wheel and the Bean

    /// <summary>
    /// A Ferris wheel (Navy Pier's or Coney Island's) as one textured sprite that turns about its hub. The
    /// gondolas are round, so they look right at any angle.
    /// </summary>
    private SpriteVisual BuildWheel(ContainerVisual layer)
    {
        float k = _strip.Scale, size = 2 * WheelRadius + 6f, c = size / 2;
        var canvas = new Canvas((int)MathF.Ceiling(size * k), (int)MathF.Ceiling(size * k));
        canvas.Draw(p =>
        {
            p.Transform(Matrix3x2.CreateScale(k));
            var rim = p.Solid(Anim.Hex(0xE8EDF2));
            var spoke = p.Solid(Anim.Hex(0xC9D2DB));
            for (int i = 0; i < 16; i++)
            {
                float a = i * MathF.Tau / 16;
                p.Line(c, c, c + WheelRadius * MathF.Cos(a), c + WheelRadius * MathF.Sin(a), 0.45f, spoke);
            }
            p.StrokeEllipse(c, c, WheelRadius * 0.62f, WheelRadius * 0.62f, 0.6f, spoke);
            p.StrokeEllipse(c, c, WheelRadius, WheelRadius, 1.3f, rim);
            for (int i = 0; i < 12; i++)
            {
                float a = i * MathF.Tau / 12 + 0.13f;
                p.Ellipse(c + WheelRadius * MathF.Cos(a), c + WheelRadius * MathF.Sin(a), 1.9f, 1.9f, p.Solid(Gondolas[i % Gondolas.Length]));
            }
            p.Ellipse(c, c, 2.3f, 2.3f, p.Solid(WheelSteel));
            p.Ellipse(c, c, 1f, 1f, rim);
        });
        var sprite = AddSprite(layer, canvas, _wheelX - c, _ground - WheelHub - c);
        sprite.CenterPoint = new Vector3(c, c, 0);
        sprite.RotationAngleInDegrees = _rng.Range(0f, 30f);
        return sprite;
    }

    /// <summary>Turns the wheel slowly (Full motion) or stops it where it is.</summary>
    public void SetWheelTurning(bool on)
    {
        if (_wheel == null || _disposed || on == _turning) return;
        _turning = on;
        if (!on)
        {
            _wheel.StopAnimation("RotationAngleInDegrees");
            return;
        }
        var a = Anim.C.CreateScalarKeyFrameAnimation();
        a.InsertExpressionKeyFrame(0f, "this.StartingValue");
        a.InsertExpressionKeyFrame(1f, "this.StartingValue + 360", Anim.Linear);
        a.Duration = Anim.Sec(90);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        _wheel.StartAnimation("RotationAngleInDegrees", a);
    }

    /// <summary>Cloud Gate, polished steel reflecting sky, skyline and lawn, with its arch underneath.</summary>
    private void BuildBean(ContainerVisual park)
    {
        const float bw = 30f, bh = 13f, hw = bw / 2;
        float k = _strip.Scale;
        float left = _beanX - hw - 3f, top = _ground - bh - 2f;
        float x0 = MathF.Floor(left * k) / k, y0 = MathF.Floor(top * k) / k;
        var canvas = new Canvas((int)MathF.Ceiling((left + bw + 6f - x0) * k), (int)MathF.Ceiling((_ground + 2.5f - y0) * k));
        float cx = _beanX - x0, by = _ground + 1f - y0;
        canvas.Draw(p =>
        {
            p.Transform(Matrix3x2.CreateScale(hw + 2f, 2.2f) * Matrix3x2.CreateTranslation(cx, by - 1.2f) * Matrix3x2.CreateScale(k));
            p.Ellipse(0, 0, 1, 1, p.UnitShadow(Anim.Hex(0x10160A, 80)));
            p.Transform(Matrix3x2.CreateScale(k));
            IntPtr bean = new PathBuilder(winding: true)
                .MoveTo(cx - hw + 2.2f, by - 1.6f)
                .CubicTo(cx - hw - 1.2f, by - 3.5f, cx - hw - 0.4f, by - bh + 1f, cx - hw + 6f, by - bh + 0.2f)
                .CubicTo(cx - 4f, by - bh - 0.8f, cx + 4f, by - bh - 0.8f, cx + hw - 6f, by - bh + 0.2f)
                .CubicTo(cx + hw + 0.4f, by - bh + 1f, cx + hw + 1.2f, by - 3.5f, cx + hw - 2.2f, by - 1.6f)
                .LineTo(cx + 6f, by - 1.6f)
                .CubicTo(cx + 4.5f, by - 1.6f, cx + 3.2f, by - 5.6f, cx, by - 5.6f)
                .CubicTo(cx - 3.2f, by - 5.6f, cx - 4.5f, by - 1.6f, cx - 6f, by - 1.6f)
                .Close()
                .BuildD2D();
            try
            {
                p.FillGradient(bean, new Vector2(0, by - bh - 0.5f), new Vector2(0, by - 1.6f),
                    (0f, Anim.Hex(0xF5F8FB)), (0.2f, Anim.Hex(0xBBD0E4)), (0.42f, Anim.Hex(0x8D9BAC)), (0.56f, Anim.Hex(0x56606F)),
                    (0.7f, Anim.Hex(0x94A889)), (0.88f, Anim.Hex(0xD9E0E6)), (1f, Anim.Hex(0xA7B1BC)));
            }
            finally
            {
                Marshal.Release(bean);
            }
            p.Transform(Matrix3x2.CreateRotation(-0.25f) * Matrix3x2.CreateTranslation(cx - hw * 0.45f, by - bh * 0.72f) * Matrix3x2.CreateScale(k));
            p.Ellipse(0, 0, 3.2f, 0.9f, p.Solid(Anim.Hex(0xFFFFFF, 190)));
        });
        AddSprite(park, canvas, x0, y0);
    }

    // ---------------------------------------------------------------- life: window lights and the L

    /// <summary>A few dark windows whose lights switch on and off now and then (Full motion).</summary>
    public void AddTwinkles(ContainerVisual layer)
    {
        if (_disposed || _darkWindows.Count == 0) return;
        int n = Math.Clamp((int)(_strip.Width / 90f), 10, 40);
        var brush = Shapes.Solid(Lit);
        for (int i = 0; i < n; i++)
        {
            var win = _darkWindows[_rng.Int(0, _darkWindows.Count)];
            var v = Anim.C.CreateSpriteVisual();
            v.Brush = brush;
            v.Size = new Vector2(win.Z, win.W);
            v.Offset = new Vector3(win.X, win.Y, 0);
            v.Opacity = 0;
            float on = _rng.Range(0.05f, 0.6f), off = MathF.Min(0.97f, on + _rng.Range(0.2f, 0.55f));
            var a = Anim.C.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(0f, 0f);
            a.InsertKeyFrame(on, 0f, Anim.Linear);
            a.InsertKeyFrame(on + 0.01f, 1f, Anim.Linear);
            a.InsertKeyFrame(off, 1f, Anim.Linear);
            a.InsertKeyFrame(off + 0.01f, 0f, Anim.Linear);
            a.InsertKeyFrame(1f, 0f, Anim.Linear);
            a.Duration = Anim.Sec(_rng.Range(16f, 45f));
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            v.StartAnimation("Opacity", a);
            layer.Children.InsertAtTop(v);
        }
    }

    /// <summary>Sends an L train of 4-6 stainless cars along the elevated track, in either direction.</summary>
    public void SendTrain(ContainerVisual layer)
    {
        if (_disposed) return;
        int cars = _rng.Int(4, 7);
        var (brush, length) = TrainArt(cars);
        float dir = _rng.Sign(), w = _strip.Width;
        var v = Anim.C.CreateSpriteVisual();
        v.Brush = brush;
        v.Size = new Vector2(length, TrainHeight);
        v.CenterPoint = new Vector3(length / 2, TrainHeight / 2, 0);
        if (dir < 0) v.Scale = new Vector3(-1, 1, 1);
        float x0 = dir > 0 ? -length - 30f : w + 30f, x1 = dir > 0 ? w + 30f : -length - 30f;
        v.Offset = new Vector3(x0, _deckTop - 0.6f - TrainHeight, 0);
        layer.Children.InsertAtTop(v);
        double seconds = MathF.Abs(x1 - x0) / _rng.Range(130f, 180f);
        var batch = Anim.C.CreateScopedBatch(CompositionBatchTypes.Animation);
        v.StartAnimation("Offset.X", Anim.Scalar(seconds, Anim.Linear, 1f, x1));
        batch.End();
        batch.Completed += (_, _) =>
        {
            try
            {
                Shapes.Discard(v);
            }
            catch (Exception)
            {
                // The strip was torn down while the train was running.
            }
        };
        Log.Write($"L train: {cars} cars heading {(dir > 0 ? "east" : "west")}");
    }

    private (CompositionSurfaceBrush Brush, float Length) TrainArt(int cars)
    {
        if (_trains.TryGetValue(cars, out var cached)) return cached;
        float k = _strip.Scale;
        float length = cars * CarLength + (cars - 1) * CarGap;
        var canvas = new Canvas((int)MathF.Ceiling(length * k), (int)MathF.Ceiling(TrainHeight * k));
        canvas.Draw(p =>
        {
            p.Transform(Matrix3x2.CreateScale(k));
            var body = p.Linear(Anim.Hex(0xF1F3F5), Anim.Hex(0xB3BBC4), new Vector2(0, 0.6f), new Vector2(0, 7f));
            var dark = p.Solid(Anim.Hex(0x2B323B));
            var glass = p.Solid(Anim.Hex(0xF7E3AE));
            var blue = p.Solid(Anim.Hex(0x2F6FB3));
            var red = p.Solid(Anim.Hex(0xD9443F));
            for (int i = 0; i < cars; i++)
            {
                float x = i * (CarLength + CarGap);
                p.RoundRect(x, 0.6f, CarLength, 6.4f, 1.2f, body);
                p.RoundRect(x + 1.5f, 1.7f, CarLength - 3f, 2.2f, 0.5f, dark);
                for (int j = 0; j < 5; j++) p.Rect(x + 2.3f + j * 3.75f, 1.95f, 2.5f, 1.7f, glass);
                p.Rect(x + 0.8f, 4.6f, CarLength - 1.6f, 0.45f, blue);
                p.Rect(x + 0.8f, 5.15f, CarLength - 1.6f, 0.35f, red);
                p.RoundRect(x + 2.5f, 7f, 5f, 1.4f, 0.6f, dark);
                p.RoundRect(x + CarLength - 7.5f, 7f, 5f, 1.4f, 0.6f, dark);
                if (i < cars - 1) p.Rect(x + CarLength, 4.3f, CarGap, 0.9f, dark);
            }
            p.Ellipse(length - 0.9f, 5f, 0.6f, 0.6f, p.Solid(Anim.Hex(0xFFF4C2)));
            p.Ellipse(0.9f, 5f, 0.5f, 0.5f, p.Solid(Beacon));
        });
        var brush = Anim.C.CreateSurfaceBrush(canvas.Surface);
        brush.Stretch = CompositionStretch.Fill;
        _surfaces.Add((canvas, brush));
        _trains[cars] = (brush, length);
        return (brush, length);
    }

    /// <summary>Frees the textures. Call after the layers holding the sprites have been discarded.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (canvas, brush) in _surfaces)
        {
            brush.Dispose();
            canvas.Dispose();
        }
        _surfaces.Clear();
        _trains.Clear();
        _wheel = null;
    }
}
