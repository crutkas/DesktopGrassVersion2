// Grass.cs - one strip's grass: blades batched into swaying tiles, flowers, mushrooms, gusts and cuts.
//
// Blades are data. Each ~30 DIP segment of a depth layer is a "group": a SpriteVisual showing a tile that
// Direct2D rasterizes (anti-aliased) with all of the group's blades and flowers. Groups are collected into
// ~140 DIP "buckets". Motion is a shear about the ground line, which doesn't depend on x, so it composes
// cleanly at two levels and never touches the pixels:
//   - ambient sway: one expression per bucket container, driven by the shared Wind phases. Attached
//     only in Full motion.
//   - gusts: a damped spring per group (v/p/t on its property set, timed by GustClock), attached only
//     while that group is springing.
// The app redraws a tile only when its blades change (a cut, regrowth, a flower popping, a new palette),
// so at rest in Calm mode nothing is animating at all and DWM can idle.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using DesktopGrass.Composition.Interop;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal sealed record GrassConfig(float SegmentWidth = 30f, float FrontSegmentWidth = 48f, float Density = 1f, float BucketWidth = 140f);

internal sealed class GrassField : IDisposable
{
    /// <summary>Ground line inside every grass group (group-local DIPs); also the shear pivot height.</summary>
    public const float G = 52f;
    private const float Pad = 14f;
    private const float MaxTheta = 1.1f;
    private const float GustRadius = 150f;
    private const float GustBand = 110f;
    private const float CutRadius = 15f;
    private const float CutBand = 40f;
    private const float MaxFlowerChance = 0.04f;
    private const float MaxMushroomChance = 0.045f;
    private const int FrontLayer = 3;
    private const float GradientSpan = 34f;
    /// <summary>Tile redraw rate while blades are being cut, regrown or popped.</summary>
    private const double FrameSeconds = 1.0 / 40;
    /// <summary>How long a new palette takes to sweep across the strip.</summary>
    private const double WaveSeconds = 1.4;

    // Layers 0..2 are behind critters (back, mid, near); layer 3 is the short tufts in front of them.
    private static readonly float[] LayerGain = { 0.85f, 1f, 1.1f, 1.15f };
    private static readonly float[] LayerSway = { 0.8f, 1f, 1.1f, 0.7f };

    private const string AmbientTheta = "Clamp((a1*Sin(W.P1-p1)+a2*Sin(W.P2+p2)+aw*Sin(W.Wave-xk)+W.Lean)*W.Amp,-M,M)";

    /// <summary>Bucket shear about the ground line (H = strip ground y), ambient angle inlined.</summary>
    private const string BucketExpression =
        "Matrix4x4(1,0,0,0, -Sin(" + AmbientTheta + "),Cos(" + AmbientTheta + "),0,0, 0,0,1,0, " +
        "H*Sin(" + AmbientTheta + "),H*(1-Cos(" + AmbientTheta + ")),0,1)";

    private const string ThetaExpression =
        "Clamp((s.v+(s.p-s.v)*(1-Pow(Ka,Max(0,K.T-s.t))))*Pow(Kb,Max(0,K.T-s.t))*Cos(Kw*Max(0,K.T-s.t)),-M,M)";

    private const string MatrixExpression =
        "Matrix4x4(1,0,0,0, -Sin(s.Th),Cos(s.Th),0,0, 0,0,1,0, H*Sin(s.Th),H*(1-Cos(s.Th)),0,1)";

    /// <summary>Blade and petal geometries (Direct2D factory resources shared by every strip).</summary>
    private static readonly Dictionary<int, IntPtr> s_geometry = new();
    private static CompositionPath[]? s_slivers;

    private enum Ease : byte { Linear, Out, In, InOut, Pop }

    /// <summary>A value the app animates; it only drives tile redraws, so 40 Hz is plenty.</summary>
    private struct Tween
    {
        public float From, To;
        public double Start, Duration;
        public Ease Ease;

        public Tween(float value)
        {
            From = To = value;
            Start = Duration = 0;
            Ease = Ease.Linear;
        }

        public static Tween Run(float from, float to, double start, double seconds, Ease ease) =>
            new() { From = from, To = to, Start = start, Duration = seconds, Ease = ease };

        public readonly double End => Start + Duration;

        public readonly float Value(double now)
        {
            if (Duration <= 0 || now >= End) return To;
            if (now <= Start) return From;
            float t = (float)((now - Start) / Duration);
            return From + (To - From) * Shape(t, Ease);
        }

        private static float Shape(float t, Ease e) => e switch
        {
            Ease.Out => 1 - (1 - t) * (1 - t) * (1 - t),
            Ease.In => t * t * t,
            Ease.InOut => t < 0.5f ? 4 * t * t * t : 1 - MathF.Pow(-2 * t + 2, 3) / 2,
            Ease.Pop => 1 + 2.70158f * MathF.Pow(t - 1, 3) + 1.70158f * MathF.Pow(t - 1, 2),
            _ => t,
        };
    }

    private sealed class Bucket
    {
        public required ContainerVisual Visual;
        public float CenterX;
        public int Layer;
        public float P1, P2;
    }

    private sealed class Group
    {
        public required SpriteVisual Visual;
        public required CompositionPropertySet Props;
        public required Canvas Canvas;
        public required CompositionSurfaceBrush Brush;
        public float Left, CenterX;
        /// <summary>Where the group's origin sits inside its tile (DIPs), after snapping the tile to whole pixels.</summary>
        public float Dx;
        public int Layer;
        public float V, P, T;
        public bool Active;
        public readonly List<Blade> Blades = new();
        public Palette? Palette;
        public bool Stale;
        public double DrawnAt = double.NegativeInfinity;
    }

    private sealed class Flower
    {
        public int Color, Petals;
        public float Rank, Radius, Angle;
        /// <summary>Head center relative to the blade base point, at full stem height.</summary>
        public Vector2 Head;
        public bool Visible = true;
        public Tween Scale = new(1f);
    }

    private sealed class Blade
    {
        public required Group Group;
        public float X, LocalX, Length;
        public int Depth, Hue, Serial, Geometry;
        public bool Cut;
        public Tween Scale = new(1f);
        public Flower? Flower;
    }

    private sealed class Mushroom
    {
        public required SpriteVisual Visual;
        public required Canvas Canvas;
        public required CompositionSurfaceBrush Brush;
        public IntPtr Cap;
        /// <summary>The mushroom's base point inside its tile (DIPs).</summary>
        public Vector2 Base;
        public float X, CapRadius, CapHeight, StemHeight, StemWidth, Height, Rank;
        public int Color;
        public bool Visible = true;
        public Palette? Palette;
    }

    private readonly Strip _strip;
    private readonly GustClock _clock;
    private readonly Scheduler _scheduler;
    private readonly Rng _rng;
    private readonly GrassConfig _config;
    private readonly List<Group> _groups = new();
    private readonly List<Blade> _blades = new();
    private readonly List<Mushroom> _mushrooms = new();
    private readonly List<Group> _active = new();
    private readonly List<Bucket> _buckets = new();
    private readonly Dictionary<long, Bucket> _bucketIndex = new();
    private readonly HashSet<Group> _animating = new();
    private readonly List<Group> _settled = new();
    private readonly ContainerVisual[] _layers = new ContainerVisual[4];
    private readonly ContainerVisual _mushroomLayer;
    private readonly SpriteVisual _turf;
    private readonly ExpressionAnimation _theta, _matrix, _sway;
    /// <summary>Where group-local y = 0 sits inside every tile (DIPs), after pixel snapping.</summary>
    private readonly float _tileDy;
    private Palette _palette;
    private TimerToken? _ambientToken, _frameToken;
    private int _wave;
    private bool _ambientOn, _disposed;

    public int BladeCount => _blades.Count;
    public int GroupCount => _groups.Count;
    public int MushroomCount => _mushrooms.Count;
    public int ActiveGroups => _active.Count;

    public GrassField(Strip strip, Wind wind, GustClock clock, Palette palette, Scheduler scheduler, Rng rng, GrassConfig config, CompositionBrush turf)
    {
        var sw = Stopwatch.StartNew();
        _strip = strip;
        _clock = clock;
        _palette = palette;
        _scheduler = scheduler;
        _rng = rng;
        _config = config;
        var c = Anim.C;

        float k = strip.Scale;
        float top = strip.GroundY - G;
        _tileDy = top - MathF.Floor(top * k) / k;

        _theta = c.CreateExpressionAnimation(ThetaExpression);
        _theta.SetReferenceParameter("K", clock.Set);
        _theta.SetScalarParameter("M", MaxTheta);
        _theta.SetScalarParameter("Ka", MathF.Exp(-GustClock.Rise));
        _theta.SetScalarParameter("Kb", MathF.Exp(-GustClock.Decay));
        _theta.SetScalarParameter("Kw", GustClock.Omega);
        _matrix = c.CreateExpressionAnimation(MatrixExpression);
        _matrix.SetScalarParameter("H", G + _tileDy); // the ground line inside a tile
        _sway = c.CreateExpressionAnimation(BucketExpression);
        _sway.SetReferenceParameter("W", wind.Set);
        _sway.SetScalarParameter("M", MaxTheta);
        _sway.SetScalarParameter("H", strip.GroundY);

        _turf = c.CreateSpriteVisual();
        _turf.Size = new Vector2(strip.Width, 5);
        _turf.Offset = new Vector3(0, strip.GroundY - 5, 0);
        _turf.Brush = turf;
        strip.BackGrass.Children.InsertAtTop(_turf);
        _layers[0] = Layer(strip.BackGrass);
        _mushroomLayer = c.CreateContainerVisual();
        strip.BackGrass.Children.InsertAtTop(_mushroomLayer);
        _layers[1] = Layer(strip.BackGrass);
        _layers[2] = Layer(strip.BackGrass);
        _layers[FrontLayer] = Layer(strip.FrontGrass);

        Generate();
        _clock.Stopped += OnClockStopped;
        long generated = sw.ElapsedMilliseconds;
        ApplyPalette(palette, animate: false);
        Log.Write($"grass: {_blades.Count} blades in {_groups.Count} tiles, {_buckets.Count} sway buckets, {_mushrooms.Count} mushrooms; " +
            $"built in {generated} ms, rasterized in {sw.ElapsedMilliseconds - generated} ms");
    }

    private ContainerVisual Layer(ContainerVisual parent)
    {
        var v = Anim.C.CreateContainerVisual();
        v.Size = new Vector2(_strip.Width, _strip.Height);
        v.CenterPoint = new Vector3(0, _strip.GroundY, 0);
        parent.Children.InsertAtTop(v);
        return v;
    }

    // ---------------------------------------------------------------- generation

    private void Generate()
    {
        var groups = new Dictionary<long, Group>();
        float density = Math.Clamp(_config.Density, 0.2f, 3f);
        float x = -3f;
        while (true)
        {
            x += _rng.Range(4f, 8f) / (2.53125f * density);
            if (x > _strip.Width + 3f) break;
            float r = _rng.Next();
            int layer = r < 0.36f ? 0 : r < 0.7f ? 1 : 2;
            float length = layer switch
            {
                0 => _rng.Range(12f, 30f),
                1 => _rng.Range(8f, 27f),
                _ => _rng.Range(6f, 20f),
            };
            AddBlade(groups, layer, x, length, _rng.Int(0, 6), _rng.Chance(MaxFlowerChance));
            if (_rng.Chance(MaxMushroomChance / density)) AddMushroom(x + _rng.Range(-1f, 1f));
        }

        x = _rng.Range(0f, 4f);
        while (x < _strip.Width)
        {
            AddBlade(groups, FrontLayer, x, _rng.Range(4f, 11f), _rng.Int(0, 6), flower: false);
            x += _rng.Range(3f, 9f) / density;
        }

        _blades.Sort((a, b) => a.X.CompareTo(b.X));
        _groups.Sort((a, b) => a.CenterX.CompareTo(b.CenterX));
    }

    private Group GroupFor(Dictionary<long, Group> groups, int layer, float x)
    {
        float segW = layer == FrontLayer ? _config.FrontSegmentWidth : _config.SegmentWidth;
        // Stagger segment boundaries between layers so the batching grid never lines up.
        float origin = layer == FrontLayer ? 0f : layer * segW / 3f;
        int index = (int)MathF.Floor((x - origin) / segW);
        long key = ((long)layer << 32) | (uint)index;
        if (groups.TryGetValue(key, out var g)) return g;

        // The tile is snapped to whole device pixels so the texture maps 1:1 onto the screen.
        float k = _strip.Scale;
        float left = origin + index * segW - Pad;
        float x0 = MathF.Floor(left * k) / k, y0 = _strip.GroundY - G - _tileDy;
        float dx = left - x0;
        int widthPx = (int)MathF.Ceiling((segW + 2 * Pad + dx) * k), heightPx = (int)MathF.Ceiling((G + 1 + _tileDy) * k);
        var canvas = new Canvas(widthPx, heightPx);
        var brush = Anim.C.CreateSurfaceBrush(canvas.Surface);
        brush.Stretch = CompositionStretch.Fill;
        var visual = Anim.C.CreateSpriteVisual();
        visual.Brush = brush;
        visual.Size = new Vector2(widthPx / k, heightPx / k);
        visual.Offset = new Vector3(x0, y0, 0);

        float centerX = origin + (index + 0.5f) * segW;
        g = new Group
        {
            Visual = visual,
            Props = visual.Properties,
            Canvas = canvas,
            Brush = brush,
            Left = left,
            CenterX = centerX,
            Dx = dx,
            Layer = layer,
        };
        g.Props.InsertScalar("v", 0);
        g.Props.InsertScalar("p", 0);
        g.Props.InsertScalar("t", 0);
        g.Props.InsertScalar("Th", 0);
        groups[key] = g;
        _groups.Add(g);
        BucketFor(layer, centerX).Visual.Children.InsertAtTop(visual);
        return g;
    }

    /// <summary>The ambient-sway container for a layer position (bucket grids are staggered per layer).</summary>
    private Bucket BucketFor(int layer, float x)
    {
        float width = Math.Max(40f, _config.BucketWidth);
        float origin = -layer * width * 0.37f;
        int index = (int)MathF.Floor((x - origin) / width);
        long key = ((long)layer << 32) | (uint)index;
        if (_bucketIndex.TryGetValue(key, out var b)) return b;
        var v = Anim.C.CreateContainerVisual();
        _layers[layer].Children.InsertAtTop(v);
        float center = origin + (index + 0.5f) * width;
        b = new Bucket
        {
            Visual = v,
            CenterX = center,
            Layer = layer,
            P1 = center / 140f + layer * 0.9f + _rng.Range(0f, 0.8f),
            P2 = center / 53f + _rng.Range(0f, MathF.Tau),
        };
        _bucketIndex[key] = b;
        _buckets.Add(b);
        return b;
    }

    /// <summary>Ambient sway on or off. Turning it off waits for the wind's fade-out, then detaches every expression.</summary>
    public void SetAmbient(bool on, double offDelay)
    {
        if (_disposed) return;
        _ambientToken?.Cancel();
        _ambientToken = null;
        if (on)
        {
            if (_ambientOn) return;
            _ambientOn = true;
            foreach (var b in _buckets)
            {
                float sway = LayerSway[b.Layer];
                _sway.SetScalarParameter("a1", 0.055f * sway);
                _sway.SetScalarParameter("a2", 0.022f * sway);
                _sway.SetScalarParameter("aw", 0.085f * sway);
                _sway.SetScalarParameter("p1", b.P1);
                _sway.SetScalarParameter("p2", b.P2);
                _sway.SetScalarParameter("xk", b.CenterX / 260f);
                b.Visual.StartAnimation("TransformMatrix", _sway);
            }
            return;
        }
        if (!_ambientOn) return;
        _ambientToken = _scheduler.After(offDelay, () =>
        {
            if (_disposed) return;
            _ambientOn = false;
            foreach (var b in _buckets)
            {
                b.Visual.StopAnimation("TransformMatrix");
                b.Visual.TransformMatrix = Matrix4x4.Identity;
            }
        });
    }

    private void AddBlade(Dictionary<long, Group> groups, int layer, float x, float length, int hue, bool flower)
    {
        var g = GroupFor(groups, layer, x);
        var blade = new Blade { Group = g, X = x, LocalX = x - g.Left, Depth = Math.Min(layer, 2), Hue = hue };
        if (!flower)
        {
            blade.Length = length;
            blade.Geometry = BladePath((int)MathF.Round(length), _rng.Int(-3, 4), _rng.Int(1, 4));
        }
        else
        {
            int stemLength = (int)MathF.Round(MathF.Min(45f, length * _rng.Range(1.2f, 1.5f)));
            int curve = _rng.Int(-1, 2);
            float radius = _rng.Range(2.2f, 3.4f);
            blade.Length = stemLength;
            blade.Geometry = BladePath(stemLength, curve, 0);
            blade.Flower = new Flower
            {
                Color = _rng.Int(0, 6),
                Angle = _rng.Range(0f, 72f) * MathF.PI / 180f,
                Rank = _rng.Next(),
                Petals = PetalsPath(radius),
                Radius = radius,
                Head = new Vector2(curve * 0.065f * stemLength, G - stemLength),
            };
        }
        g.Blades.Add(blade);
        _blades.Add(blade);
    }

    /// <summary>Tapered blade with its base centered at (0, G); cached by quantized length/curve/width.</summary>
    private static int BladePath(int length, int curve, int width)
    {
        int key = length * 100 + (curve + 3) * 10 + width;
        if (s_geometry.ContainsKey(key)) return key;
        float l = length;
        float w = (width == 0 ? 0.62f : 0.7f + 0.35f * width) * (0.85f + 0.3f * MathF.Min(l, 30f) / 30f);
        float c = curve * 0.065f * l;
        s_geometry[key] = new PathBuilder()
            .MoveTo(-w, G)
            .CubicTo(-0.9f * w, G - 0.45f * l, 0.55f * c - 0.25f * w, G - 0.85f * l, c, G - l)
            .CubicTo(0.55f * c + 0.25f * w, G - 0.85f * l, 0.9f * w, G - 0.45f * l, w, G)
            .Close()
            .BuildD2D();
        return key;
    }

    /// <summary>Five round petals around the origin.</summary>
    private static int PetalsPath(float radius)
    {
        int bucket = (int)MathF.Round(radius * 5);
        int key = -1 - bucket;
        if (s_geometry.ContainsKey(key)) return key;
        float r = bucket / 5f;
        var pb = new PathBuilder(winding: true);
        for (int k = 0; k < 5; k++)
        {
            float a = k * MathF.Tau / 5 - MathF.PI / 2;
            pb.Circle(MathF.Cos(a) * 0.62f * r, MathF.Sin(a) * 0.62f * r, 0.5f * r);
        }
        s_geometry[key] = pb.BuildD2D();
        return key;
    }

    private void AddMushroom(float x)
    {
        float capR = _rng.Range(3.2f, 6f);
        float capH = _rng.Range(2.6f, 4.8f);
        float stemH = _rng.Range(3.5f, 8f);
        float stemW = _rng.Range(2f, 3.6f);
        int color = _rng.Int(0, 6);

        // Tile: the cap and its shadow with a little room, snapped to whole pixels like the grass.
        float k = _strip.Scale;
        float halfW = capR * 1.1f + 2f;
        float left = x - halfW, top = _strip.GroundY - stemH - capH - 2f, bottom = _strip.GroundY + 2.5f;
        float x0 = MathF.Floor(left * k) / k, y0 = MathF.Floor(top * k) / k;
        int widthPx = (int)MathF.Ceiling((left + 2 * halfW - x0) * k), heightPx = (int)MathF.Ceiling((bottom - y0) * k);
        var canvas = new Canvas(widthPx, heightPx);
        var brush = Anim.C.CreateSurfaceBrush(canvas.Surface);
        brush.Stretch = CompositionStretch.Fill;
        var visual = Anim.C.CreateSpriteVisual();
        visual.Brush = brush;
        visual.Size = new Vector2(widthPx / k, heightPx / k);
        visual.Offset = new Vector3(x0, y0, 0);
        var basePoint = new Vector2(x - x0, _strip.GroundY - y0);
        visual.CenterPoint = new Vector3(basePoint, 0);
        _mushroomLayer.Children.InsertAtTop(visual);

        _mushrooms.Add(new Mushroom
        {
            Visual = visual,
            Canvas = canvas,
            Brush = brush,
            Cap = new PathBuilder().MoveTo(-capR, -stemH).ArcTo(capR, -stemH, capR, capH, clockwise: true).Close().BuildD2D(),
            Base = basePoint,
            X = x,
            CapRadius = capR,
            CapHeight = capH,
            StemHeight = stemH,
            StemWidth = stemW,
            Height = stemH + capH,
            Rank = _rng.Next(),
            Color = color,
        });
    }

    // ---------------------------------------------------------------- drawing

    private void Draw(Group g, double now)
    {
        g.Stale = false;
        g.DrawnAt = now;
        var palette = g.Palette ?? _palette;
        var toPixels = Matrix3x2.CreateTranslation(g.Dx, _tileDy) * Matrix3x2.CreateScale(_strip.Scale);
        g.Canvas.Draw(p =>
        {
            foreach (var b in g.Blades) DrawBlade(p, b, palette, toPixels, now);
        });
    }

    private static void DrawBlade(Painter p, Blade b, Palette palette, Matrix3x2 toPixels, double now)
    {
        float s = b.Scale.Value(now);
        var placed = Matrix3x2.CreateTranslation(b.LocalX, 0) * toPixels;
        var (baseColor, tipColor) = GrassBrushes.Colors(palette, b.Depth, b.Hue);
        p.Transform(Matrix3x2.CreateScale(1, s, new Vector2(0, G)) * placed);
        p.Fill(s_geometry[b.Geometry], p.Linear(baseColor, tipColor, new Vector2(0, G), new Vector2(0, G - GradientSpan)));

        if (b.Flower is not { } f) return;
        float hs = f.Scale.Value(now);
        if (hs <= 0.01f) return;
        // The head rides on the (possibly shortened) stem tip.
        var head = new Vector2(f.Head.X, G + (f.Head.Y - G) * s);
        p.Transform(Matrix3x2.CreateScale(hs) * Matrix3x2.CreateRotation(f.Angle) * Matrix3x2.CreateTranslation(head) * placed);
        p.Fill(s_geometry[f.Petals], p.Solid(palette.Flowers[f.Color]));
        float r = f.Radius * 0.36f;
        p.Ellipse(0, 0, r, r, p.Solid(palette.FlowerCenter));
    }

    private void Draw(Mushroom m)
    {
        var palette = m.Palette ?? _palette;
        var toPixels = Matrix3x2.CreateTranslation(m.Base) * Matrix3x2.CreateScale(_strip.Scale);
        m.Canvas.Draw(p =>
        {
            float capR = m.CapRadius, capH = m.CapHeight, stemH = m.StemHeight, stemW = m.StemWidth;
            p.Transform(Matrix3x2.CreateScale(capR * 1.1f, 1.5f) * toPixels);
            p.Ellipse(0, 0, 1, 1, p.UnitShadow(Anim.Hex(0x10160A, 60)));
            p.Transform(toPixels);
            p.RoundRect(-stemW / 2, -stemH - 0.6f, stemW, stemH + 0.6f, stemW * 0.45f, p.Solid(palette.Stem));
            p.Ellipse(0, -stemH, capR * 0.82f, 0.9f, p.Solid(Anim.Hex(0x000000, 45)));
            p.Fill(m.Cap, p.Solid(palette.Caps[m.Color]));
            p.Ellipse(-capR * 0.35f, -stemH - capH * 0.62f, capR * 0.3f, capH * 0.15f, p.Solid(Anim.Hex(0xFFFFFF, 80)));
            if (m.Color is 0 or 4)
            {
                var spot = p.Solid(Anim.Hex(0xFFFFFF, 225));
                p.Ellipse(-capR * 0.45f, -stemH - capH * 0.4f, capR * 0.13f, capR * 0.13f, spot);
                p.Ellipse(capR * 0.38f, -stemH - capH * 0.55f, capR * 0.15f, capR * 0.15f, spot);
                p.Ellipse(capR * 0.02f, -stemH - capH * 0.82f, capR * 0.11f, capR * 0.11f, spot);
            }
        });
    }

    /// <summary>Keeps redrawing <paramref name="g"/> until its blades and flowers stop changing.</summary>
    private void Animate(Group g)
    {
        if (_disposed) return;
        g.Stale = true;
        _animating.Add(g);
        if (_frameToken is not { Pending: true }) _frameToken = _scheduler.After(0, Frame);
    }

    private void Frame()
    {
        if (_disposed) return;
        double now = Clock.Now;
        _settled.Clear();
        foreach (var g in _animating)
        {
            var (changed, pending) = Progress(g, now);
            if (changed) Draw(g, now);
            if (!pending) _settled.Add(g);
        }
        foreach (var g in _settled) _animating.Remove(g);
        if (_animating.Count > 0) _frameToken = _scheduler.After(FrameSeconds, Frame);
    }

    /// <summary>Whether the group looks different than when it was last drawn, and whether it will change again.</summary>
    private static (bool Changed, bool Pending) Progress(Group g, double now)
    {
        bool changed = g.Stale, pending = false;
        foreach (var b in g.Blades)
        {
            Check(b.Scale);
            if (b.Flower is { } f) Check(f.Scale);
        }
        return (changed, pending);

        void Check(in Tween t)
        {
            if (t.Duration <= 0) return;
            if (t.Start <= now && t.End > g.DrawnAt) changed = true;
            if (t.End > now) pending = true;
        }
    }

    // ---------------------------------------------------------------- scenes

    public void ApplyPalette(Palette palette, bool animate)
    {
        if (_disposed) return;
        _palette = palette;
        float flowerCut = palette.FlowerChance / MaxFlowerChance;
        float mushroomCut = palette.MushroomChance / MaxMushroomChance;
        double now = Clock.Now;

        if (!animate)
        {
            _wave++;
            foreach (var g in _groups)
            {
                g.Palette = palette;
                foreach (var b in g.Blades)
                {
                    if (b.Flower is not { } f) continue;
                    f.Visible = f.Rank < flowerCut;
                    f.Scale = new Tween(f.Visible && !b.Cut ? 1f : 0f);
                }
                Draw(g, now);
            }
            foreach (var m in _mushrooms)
            {
                m.Palette = palette;
                m.Visible = m.Rank < mushroomCut;
                SetPresence(m.Visual, m.Visible, animate: false);
                Draw(m);
            }
            foreach (var layer in _layers) layer.Scale = new Vector3(1, palette.GrassHeight, 1);
            return;
        }

        // Sweep the new palette across the strip, left to right.
        int wave = ++_wave;
        foreach (var g in _groups)
        {
            double at = WaveSeconds * Math.Clamp(g.CenterX / _strip.Width, 0f, 1f) + _rng.Range(0f, 0.08f);
            _scheduler.After(at, () =>
            {
                if (!_disposed && wave == _wave) Repaint(g, palette, flowerCut);
            });
        }
        foreach (var m in _mushrooms)
        {
            double at = WaveSeconds * Math.Clamp(m.X / _strip.Width, 0f, 1f);
            _scheduler.After(at, () =>
            {
                if (_disposed || wave != _wave) return;
                m.Palette = palette;
                Draw(m);
                bool visible = m.Rank < mushroomCut;
                if (visible == m.Visible) return;
                m.Visible = visible;
                SetPresence(m.Visual, visible, animate: true);
            });
        }
        foreach (var layer in _layers) layer.StartAnimation("Scale.Y", Anim.Scalar(1.6, Anim.EaseInOut, 1f, palette.GrassHeight));
    }

    private void Repaint(Group g, Palette palette, float flowerCut)
    {
        g.Palette = palette;
        double now = Clock.Now;
        foreach (var b in g.Blades)
        {
            if (b.Flower is not { } f) continue;
            bool visible = f.Rank < flowerCut;
            if (visible == f.Visible) continue;
            f.Visible = visible;
            if (b.Cut) continue; // the head is already gone; regrowth pops it back if still visible
            f.Scale = visible
                ? Tween.Run(0f, 1f, now + _rng.Range(0f, 0.4f), 0.45, Ease.Pop)
                : Tween.Run(f.Scale.Value(now), 0f, now, 0.3, Ease.In);
        }
        Animate(g);
    }

    private void SetPresence(Visual v, bool visible, bool animate)
    {
        var hidden = new Vector3(0, 0, 1);
        if (!animate)
        {
            v.StopAnimation("Scale");
            v.Scale = visible ? Vector3.One : hidden;
            return;
        }
        if (!visible)
        {
            v.StartAnimation("Scale", Anim.Vec3(0.35, Anim.EaseIn, (1f, hidden)));
            return;
        }
        var a = Anim.C.CreateVector3KeyFrameAnimation();
        a.Duration = Anim.Sec(0.45);
        a.InsertKeyFrame(0f, hidden, Anim.Linear);
        a.InsertKeyFrame(0.6f, new Vector3(1.18f, 1.18f, 1), Anim.EaseOut);
        a.InsertKeyFrame(1f, Vector3.One, Anim.EaseInOut);
        a.DelayTime = TimeSpan.FromSeconds(_rng.Range(0f, 0.3f));
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Scale", a);
    }

    // ---------------------------------------------------------------- gusts

    /// <summary>Cursor at (x, y) strip DIPs moved by (dx, dy) since the last call.</summary>
    public void Gust(float x, float y, float dx, float dy)
    {
        if (_disposed) return;
        float hy = _strip.GroundY - y;
        if (hy > GustBand || hy < -16f) return;
        float vertical = hy <= 30f ? 1f : Sq(1f - (hy - 30f) / (GustBand - 30f));
        dx = Math.Clamp(dx, -120f, 120f);
        float part = hy < 40f ? MathF.Min(MathF.Abs(dy), 40f) : 0f;
        float t = float.NaN;
        for (int i = FirstGroupNear(x - GustRadius); i < _groups.Count; i++)
        {
            var g = _groups[i];
            float offset = g.CenterX - x;
            if (offset >= GustRadius) break;
            float dist = MathF.Abs(offset);
            float q = 1f - Sq(dist / GustRadius);
            float delta = dx * 0.006f * q * q * vertical;
            // Moving up or down low in the grass parts the blades around the cursor.
            if (part > 0 && dist < 40f) delta += MathF.Sign(offset) * part * 0.012f * (1f - dist / 40f);
            if (MathF.Abs(delta) < 0.002f) continue;
            if (float.IsNaN(t)) t = _clock.Touch();
            Kick(g, delta * LayerGain[g.Layer], t);
        }
    }

    /// <summary>Symmetric push centered at x (used by ambient puffs and critter hops).</summary>
    public void Push(float x, float radius, float strength)
    {
        if (_disposed) return;
        float t = float.NaN;
        for (int i = FirstGroupNear(x - radius); i < _groups.Count; i++)
        {
            var g = _groups[i];
            float offset = g.CenterX - x;
            if (offset >= radius) break;
            float q = 1f - Sq(offset / radius);
            if (q <= 0) continue;
            if (float.IsNaN(t)) t = _clock.Touch();
            Kick(g, strength * q * q * LayerGain[g.Layer], t);
        }
    }

    /// <summary>An ambient puff of wind that travels across part of the strip.</summary>
    public void Puff(Rng rng, float strength = 1f)
    {
        float dir = rng.Sign();
        float x = rng.Range(0f, _strip.Width);
        float step = dir * rng.Range(260f, 420f) * 0.08f;
        PuffStep(x, step, dir * rng.Range(0.05f, 0.09f) * strength, rng.Int(12, 22));
    }

    /// <summary>X of the visible mushroom nearest to <paramref name="nearX"/> (used by the demo script).</summary>
    public bool TryFindMushroom(float nearX, out float x)
    {
        x = 0;
        float best = float.MaxValue;
        foreach (var m in _mushrooms)
        {
            float d = MathF.Abs(m.X - nearX);
            if (m.Visible && d < best)
            {
                best = d;
                x = m.X;
            }
        }
        return best < float.MaxValue;
    }

    private void PuffStep(float x, float step, float strength, int remaining)
    {
        if (_disposed || remaining <= 0) return;
        Push(x, 75f, strength);
        _scheduler.After(0.08, () => PuffStep(x + step, step, strength, remaining - 1));
    }

    private void Kick(Group g, float delta, float t)
    {
        float current = g.Active ? GustClock.Value(g.V, g.P, t - g.T) : 0f;
        g.V = current;
        g.P = Math.Clamp(current + delta, -MaxTheta, MaxTheta);
        g.T = t;
        g.Props.InsertScalar("v", g.V);
        g.Props.InsertScalar("p", g.P);
        g.Props.InsertScalar("t", t);
        if (!g.Active)
        {
            g.Active = true;
            _active.Add(g);
            _theta.SetReferenceParameter("s", g.Props);
            g.Props.StartAnimation("Th", _theta);
            _matrix.SetReferenceParameter("s", g.Props);
            g.Visual.StartAnimation("TransformMatrix", _matrix);
        }
    }

    private void OnClockStopped()
    {
        // Every spring has decayed by now; detach so resting grass costs DWM nothing.
        foreach (var g in _active)
        {
            g.V = g.P = 0;
            g.Active = false;
            g.Props.StopAnimation("Th");
            g.Visual.StopAnimation("TransformMatrix");
            g.Visual.TransformMatrix = Matrix4x4.Identity;
            g.Props.InsertScalar("v", 0);
            g.Props.InsertScalar("p", 0);
            g.Props.InsertScalar("Th", 0);
        }
        _active.Clear();
    }

    private int FirstGroupNear(float x)
    {
        int lo = 0, hi = _groups.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_groups[mid].CenterX <= x) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // ---------------------------------------------------------------- clicks: cuts, flowers, mushrooms

    /// <summary>Handles a click at (x, y) strip DIPs. Returns true if it hit grass or a mushroom.</summary>
    public bool Click(float x, float y)
    {
        if (_disposed) return false;
        float hy = _strip.GroundY - y;
        if (hy < -16f || hy > 50f) return false;

        foreach (var m in _mushrooms)
        {
            if (m.Visible && MathF.Abs(m.X - x) <= m.CapRadius + 3f && hy <= m.Height + 4f)
            {
                Boing(m);
                return true;
            }
        }
        if (hy > CutBand) return false;

        var cut = new List<(Blade Blade, int Serial)>();
        int clippings = 0;
        int lo = 0, hi = _blades.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_blades[mid].X < x - CutRadius) lo = mid + 1;
            else hi = mid;
        }
        for (int i = lo; i < _blades.Count && _blades[i].X <= x + CutRadius; i++)
        {
            var b = _blades[i];
            if (b.Cut) continue;
            Cut(b, ref clippings);
            cut.Add((b, b.Serial));
        }
        if (cut.Count == 0) return false;
        _scheduler.After(_rng.Range(30f, 90f), () => Regrow(cut));
        return true;
    }

    private void Cut(Blade b, ref int clippings)
    {
        double now = Clock.Now;
        b.Cut = true;
        b.Serial++;
        float stubble = Math.Clamp(MathF.Max(_rng.Range(0.06f, 0.16f), 2.5f / b.Length), 0.05f, 0.45f);
        b.Scale = Tween.Run(b.Scale.Value(now), stubble, now, 0.2, Ease.Out);

        if (b.Flower is { } f && f.Scale.Value(now) > 0.01f)
        {
            f.Scale = Tween.Run(f.Scale.Value(now), 0f, now, 0.15, Ease.In);
            var head = new Vector2(b.X + f.Head.X, _strip.GroundY - G + f.Head.Y);
            var color = Shapes.Solid(_palette.Flowers[f.Color]);
            for (int k = 0; k < 5; k++)
            {
                float size = f.Radius * 1.4f + 2f;
                var petal = Shapes.Ellipse(size / 2, size / 2, f.Radius * 0.5f, f.Radius * 0.36f, color);
                Fx.Toss(_strip.Fx, petal, size, head, _rng.Range(-28f, 28f), _rng.Range(6f, 16f),
                    _strip.GroundY - head.Y + 14f, _rng.Range(-400f, 400f), _rng.Range(1.0f, 1.6f));
            }
        }
        Animate(b.Group);

        if (clippings < 10 && _rng.Chance(0.65f))
        {
            clippings++;
            s_slivers ??= new[] { Sliver(3.2f, 0.5f), Sliver(4.2f, -0.6f), Sliver(5f, 0.9f) };
            var tip = GrassBrushes.Colors(_palette, b.Depth, b.Hue).Tip;
            var sliver = Shapes.Path(_rng.Pick(s_slivers), Shapes.Solid(tip));
            float cutY = _strip.GroundY - b.Length * stubble;
            var start = new Vector2(b.X, cutY - b.Length * 0.25f);
            Fx.Toss(_strip.Fx, sliver, 12f, start, _rng.Range(-34f, 34f), _rng.Range(6f, 18f),
                _strip.GroundY - start.Y + _rng.Range(4f, 14f), _rng.Range(-560f, 560f), _rng.Range(0.9f, 1.4f));
        }
    }

    private static CompositionPath Sliver(float half, float lean) =>
        new PathBuilder()
            .MoveTo(6f - 0.9f, 6f + half)
            .QuadTo(6f - 0.7f + lean * 0.3f, 6f, 6f + lean, 6f - half)
            .QuadTo(6f + 0.7f + lean * 0.3f, 6f, 6f + 0.9f, 6f + half)
            .Close()
            .Build();

    private void Regrow(List<(Blade Blade, int Serial)> cut)
    {
        if (_disposed) return;
        double now = Clock.Now;
        foreach (var (b, serial) in cut)
        {
            if (b.Serial != serial || !b.Cut) continue;
            b.Cut = false;
            double delay = _rng.Range(0f, 4f), seconds = _rng.Range(2f, 4f);
            b.Scale = Tween.Run(b.Scale.Value(now), 1f, now + delay, seconds, Ease.InOut);
            if (b.Flower is { Visible: true } f) f.Scale = Tween.Run(0f, 1f, now + delay + seconds * 0.9, 0.45, Ease.Pop);
            Animate(b.Group);
        }
    }

    private void Boing(Mushroom m)
    {
        var a = Anim.C.CreateVector3KeyFrameAnimation();
        a.Duration = Anim.Sec(0.85);
        a.InsertKeyFrame(0.12f, new Vector3(1.3f, 0.66f, 1), Anim.EaseOut);
        a.InsertKeyFrame(0.32f, new Vector3(0.84f, 1.2f, 1), Anim.EaseInOut);
        a.InsertKeyFrame(0.52f, new Vector3(1.1f, 0.93f, 1), Anim.EaseInOut);
        a.InsertKeyFrame(0.74f, new Vector3(0.97f, 1.04f, 1), Anim.EaseInOut);
        a.InsertKeyFrame(1f, Vector3.One, Anim.EaseInOut);
        m.Visual.StartAnimation("Scale", a);

        var spore = Shapes.Solid(0xFFF8E1, 220);
        var top = new Vector2(m.X, _strip.GroundY - m.Height);
        for (int k = 0; k < 4; k++)
        {
            float r = _rng.Range(0.7f, 1.3f);
            Fx.Drift(_strip.Fx, Shapes.Circle(3, 3, r, spore), 6, top + new Vector2(_rng.Range(-2f, 2f), 0),
                new Vector2(_rng.Range(-10f, 10f), -_rng.Range(10f, 24f)), _rng.Range(1.0f, 1.7f), delay: k * 0.05);
        }
    }

    private static float Sq(float v) => v * v;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ambientToken?.Cancel();
        _frameToken?.Cancel();
        _clock.Stopped -= OnClockStopped;
        foreach (var g in _active)
        {
            g.Props.StopAnimation("Th");
            g.Visual.StopAnimation("TransformMatrix");
        }
        foreach (var b in _buckets) b.Visual.StopAnimation("TransformMatrix");
        // Close everything this field put in the strip's layers, then the tiles behind them.
        var owned = new List<Visual>(_strip.BackGrass.Children);
        owned.AddRange(_strip.FrontGrass.Children);
        _strip.BackGrass.Children.RemoveAll();
        _strip.FrontGrass.Children.RemoveAll();
        foreach (var v in owned) Shapes.DisposeTree(v);
        foreach (var g in _groups)
        {
            g.Brush.Dispose();
            g.Canvas.Dispose();
        }
        foreach (var m in _mushrooms)
        {
            m.Brush.Dispose();
            m.Canvas.Dispose();
            if (m.Cap != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(m.Cap);
        }
        _groups.Clear();
        _buckets.Clear();
        _bucketIndex.Clear();
        _blades.Clear();
        _mushrooms.Clear();
        _active.Clear();
        _animating.Clear();
        _theta.Dispose();
        _matrix.Dispose();
        _sway.Dispose();
    }
}
