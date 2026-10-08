// Grass.cs - one strip's grass: blades batched into swaying groups, flowers, mushrooms, gusts and cuts.
//
// Every blade is a filled, tapered CompositionSpriteShape with a shared gradient brush. Blades are batched
// into ShapeVisual "groups" (one per ~30 DIP segment per depth layer). Motion is a shear about the ground
// line, which doesn't depend on x, so it composes cleanly at two levels:
//   - ambient sway: one expression per "bucket" container (~140 DIP of one layer), driven by the shared
//     Wind phases. Attached only in Full motion.
//   - gusts: a damped spring per group (v/p/t on its property set, timed by GustClock), attached only
//     while that group is springing.
// So at rest in Calm mode nothing is animating at all, and DWM can idle.
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
    /// <summary>Room around each cached bucket so gust-bent blades aren't cropped (tallest flower at max bend ≈ 40 DIP past its base).</summary>
    private const float SurfaceMargin = 32f;

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

    private static readonly Dictionary<int, CompositionPath> s_paths = new();
    private static CompositionPath[]? s_slivers;

    private sealed class Bucket
    {
        /// <summary>Holds the bucket's groups; never in the live tree, only rendered into Surface.</summary>
        public required ContainerVisual Source;
        public float CenterX;
        public int Layer;
        public float P1, P2;
        public float Left = float.MaxValue, Right = float.MinValue;
        public SpriteVisual? Sprite;
        public CompositionVisualSurface? Surface;
        public CompositionSurfaceBrush? Brush;
    }

    private sealed class Group
    {
        public required ShapeVisual Visual;
        public required CompositionPropertySet Props;
        public float Left, CenterX;
        public int Layer;
        public float V, P, T;
        public bool Active;
    }

    private sealed class Flower
    {
        public required CompositionContainerShape Head;
        public int Color;
        public float Rank, Radius;
        public Vector2 Tip;
        public bool Visible = true;
    }

    private sealed class Blade
    {
        public required CompositionShape Shape;
        public float X, Length;
        public int Depth, Hue, Serial;
        public bool Cut;
        public Flower? Flower;
    }

    private sealed class Mushroom
    {
        public required CompositionContainerShape Shape;
        public float X, CapRadius, Height, Rank;
        public bool Visible = true;
    }

    private readonly Strip _strip;
    private readonly GustClock _clock;
    private readonly GrassBrushes _brushes;
    private readonly Scheduler _scheduler;
    private readonly Rng _rng;
    private readonly GrassConfig _config;
    private readonly List<Group> _groups = new();
    private readonly List<Blade> _blades = new();
    private readonly List<Mushroom> _mushrooms = new();
    private readonly List<Group> _active = new();
    private readonly List<Bucket> _buckets = new();
    private readonly Dictionary<long, Bucket> _bucketIndex = new();
    private readonly ContainerVisual[] _layers = new ContainerVisual[4];
    private readonly ShapeVisual _mushroomVisual;
    private readonly ContainerVisual _mushroomSource;
    private readonly SpriteVisual _mushroomSprite;
    private readonly CompositionVisualSurface _mushroomSurface;
    private readonly CompositionSurfaceBrush _mushroomBrush;
    private readonly SpriteVisual _turf;
    private readonly ExpressionAnimation _theta, _matrix, _sway;
    private TimerToken? _ambientToken;
    private bool _ambientOn, _disposed;

    public int BladeCount => _blades.Count;
    public int GroupCount => _groups.Count;
    public int MushroomCount => _mushrooms.Count;
    public int ActiveGroups => _active.Count;

    public GrassField(Strip strip, Wind wind, GustClock clock, GrassBrushes brushes, Scheduler scheduler, Rng rng, GrassConfig config)
    {
        var sw = Stopwatch.StartNew();
        _strip = strip;
        _clock = clock;
        _brushes = brushes;
        _scheduler = scheduler;
        _rng = rng;
        _config = config;
        var c = Anim.C;

        _theta = c.CreateExpressionAnimation(ThetaExpression);
        _theta.SetReferenceParameter("K", clock.Set);
        _theta.SetScalarParameter("M", MaxTheta);
        _theta.SetScalarParameter("Ka", MathF.Exp(-GustClock.Rise));
        _theta.SetScalarParameter("Kb", MathF.Exp(-GustClock.Decay));
        _theta.SetScalarParameter("Kw", GustClock.Omega);
        _matrix = c.CreateExpressionAnimation(MatrixExpression);
        _matrix.SetScalarParameter("H", G);
        _sway = c.CreateExpressionAnimation(BucketExpression);
        _sway.SetReferenceParameter("W", wind.Set);
        _sway.SetScalarParameter("M", MaxTheta);
        _sway.SetScalarParameter("H", G + 2f); // the ground line inside a bucket sprite

        _turf = c.CreateSpriteVisual();
        _turf.Size = new Vector2(strip.Width, 5);
        _turf.Offset = new Vector3(0, strip.GroundY - 5, 0);
        _turf.Brush = brushes.Turf;
        strip.BackGrass.Children.InsertAtTop(_turf);
        _layers[0] = Layer(strip.BackGrass);
        var mushroomSlot = c.CreateContainerVisual();
        strip.BackGrass.Children.InsertAtTop(mushroomSlot);
        _mushroomVisual = Shapes.Visual(strip.Width, G + 1, new Vector2(0, strip.GroundY - G));
        _mushroomSource = SourceRoot();
        _mushroomSource.Children.InsertAtTop(_mushroomVisual);
        _layers[1] = Layer(strip.BackGrass);
        _layers[2] = Layer(strip.BackGrass);
        _layers[FrontLayer] = Layer(strip.FrontGrass);

        Generate();
        FinishBuckets();
        (_mushroomSprite, _mushroomSurface, _mushroomBrush) = Cached(_mushroomSource, 0, strip.GroundY - G - 2f, strip.Width, G + 4f);
        mushroomSlot.Children.InsertAtTop(_mushroomSprite);
        _clock.Stopped += OnClockStopped;
        ApplyPalette(brushes.Palette, animate: false);
        Log.Write($"grass: {_blades.Count} blades, {_groups.Count} groups, {_buckets.Count} sway buckets, {_mushrooms.Count} mushrooms, {s_paths.Count} cached paths in {sw.ElapsedMilliseconds} ms");
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
        foreach (var g in _groups) StartGroup(g);
    }

    private Group GroupFor(Dictionary<long, Group> groups, int layer, float x)
    {
        float segW = layer == FrontLayer ? _config.FrontSegmentWidth : _config.SegmentWidth;
        // Stagger segment boundaries between layers so the batching grid never lines up.
        float origin = layer == FrontLayer ? 0f : layer * segW / 3f;
        int index = (int)MathF.Floor((x - origin) / segW);
        long key = ((long)layer << 32) | (uint)index;
        if (groups.TryGetValue(key, out var g)) return g;

        float left = origin + index * segW - Pad;
        float centerX = origin + (index + 0.5f) * segW;
        var visual = Shapes.Visual(segW + 2 * Pad, G + 1, new Vector2(left, _strip.GroundY - G));
        g = new Group { Visual = visual, Props = visual.Properties, Left = left, CenterX = centerX, Layer = layer };
        groups[key] = g;
        _groups.Add(g);
        var bucket = BucketFor(layer, centerX);
        bucket.Source.Children.InsertAtTop(visual);
        bucket.Left = MathF.Min(bucket.Left, left);
        bucket.Right = MathF.Max(bucket.Right, left + segW + 2 * Pad);
        return g;
    }

    /// <summary>The ambient-sway bucket for a layer position (bucket grids are staggered per layer).</summary>
    private Bucket BucketFor(int layer, float x)
    {
        float width = Math.Max(40f, _config.BucketWidth);
        float origin = -layer * width * 0.37f;
        int index = (int)MathF.Floor((x - origin) / width);
        long key = ((long)layer << 32) | (uint)index;
        if (_bucketIndex.TryGetValue(key, out var b)) return b;
        float center = origin + (index + 0.5f) * width;
        b = new Bucket
        {
            Source = SourceRoot(),
            CenterX = center,
            Layer = layer,
            P1 = center / 140f + layer * 0.9f + _rng.Range(0f, 0.8f),
            P2 = center / 53f + _rng.Range(0f, MathF.Tau),
        };
        _bucketIndex[key] = b;
        _buckets.Add(b);
        return b;
    }

    /// <summary>
    /// Shows each bucket through a cached texture. DWM re-rasterizes ShapeVisual vector content whenever it
    /// falls in a dirty region (anything moving nearby), so static grass must live in a VisualSurface:
    /// it re-renders only when its own blades change, and ambient sway just shears the texture.
    /// </summary>
    private void FinishBuckets()
    {
        float top = _strip.GroundY - G - 2f, height = G + 4f;
        foreach (var b in _buckets)
        {
            float left = MathF.Floor(b.Left - SurfaceMargin), width = MathF.Ceiling(b.Right + SurfaceMargin) - left;
            (b.Sprite, b.Surface, b.Brush) = Cached(b.Source, left, top, width, height);
            _layers[b.Layer].Children.InsertAtTop(b.Sprite);
        }
    }

    /// <summary>
    /// An unparented root that a VisualSurface renders. The surface works in the root's local DIPs and DWM
    /// picks the raster resolution itself (pre-scaling the content made no difference), so no scaler is needed.
    /// </summary>
    private static ContainerVisual SourceRoot() => Anim.C.CreateContainerVisual();

    private static (SpriteVisual, CompositionVisualSurface, CompositionSurfaceBrush) Cached(Visual source, float left, float top, float width, float height)
    {
        var c = Anim.C;
        var surface = c.CreateVisualSurface();
        surface.SourceVisual = source;
        surface.SourceOffset = new Vector2(left, top);
        surface.SourceSize = new Vector2(width, height);
        var brush = c.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.Fill;
        var sprite = c.CreateSpriteVisual();
        sprite.Brush = brush;
        sprite.Size = new Vector2(width, height);
        sprite.Offset = new Vector3(left, top, 0);
        return (sprite, surface, brush);
    }

    private void StartGroup(Group g)
    {
        var p = g.Props;
        p.InsertScalar("v", 0);
        p.InsertScalar("p", 0);
        p.InsertScalar("t", 0);
        p.InsertScalar("Th", 0);
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
                b.Sprite!.StartAnimation("TransformMatrix", _sway);
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
                b.Sprite!.StopAnimation("TransformMatrix");
                b.Sprite.TransformMatrix = Matrix4x4.Identity;
            }
        });
    }

    private void AddBlade(Dictionary<long, Group> groups, int layer, float x, float length, int hue, bool flower)
    {
        var c = Anim.C;
        var g = GroupFor(groups, layer, x);
        float localX = x - g.Left;
        int depth = Math.Min(layer, 2);
        var brush = _brushes.Blade[depth, hue];
        Blade blade;
        if (!flower)
        {
            var shape = c.CreateSpriteShape(c.CreatePathGeometry(BladePath((int)MathF.Round(length), _rng.Int(-3, 4), _rng.Int(1, 4))));
            shape.FillBrush = brush;
            shape.Offset = new Vector2(localX, 0);
            shape.CenterPoint = new Vector2(0, G);
            blade = new Blade { Shape = shape, X = x, Length = length, Depth = depth, Hue = hue };
        }
        else
        {
            int stemLength = (int)MathF.Round(MathF.Min(45f, length * _rng.Range(1.2f, 1.5f)));
            int curve = _rng.Int(-1, 2);
            var container = c.CreateContainerShape();
            container.Offset = new Vector2(localX, 0);
            container.CenterPoint = new Vector2(0, G);
            var stem = c.CreateSpriteShape(c.CreatePathGeometry(BladePath(stemLength, curve, 0)));
            stem.FillBrush = brush;
            container.Shapes.Add(stem);

            float radius = _rng.Range(2.2f, 3.4f);
            int color = _rng.Int(0, 6);
            var head = c.CreateContainerShape();
            head.Offset = new Vector2(curve * 0.065f * stemLength, G - stemLength);
            head.RotationAngleInDegrees = _rng.Range(0f, 72f);
            var petals = c.CreateSpriteShape(c.CreatePathGeometry(PetalsPath(radius)));
            petals.FillBrush = _brushes.Petal[color];
            head.Shapes.Add(petals);
            head.Shapes.Add(Shapes.Circle(0, 0, radius * 0.36f, _brushes.FlowerCenter));
            container.Shapes.Add(head);

            blade = new Blade { Shape = container, X = x, Length = stemLength, Depth = depth, Hue = hue };
            blade.Flower = new Flower { Head = head, Color = color, Rank = _rng.Next(), Radius = radius, Tip = head.Offset };
        }
        g.Visual.Shapes.Add(blade.Shape);
        _blades.Add(blade);
    }

    /// <summary>Tapered blade with its base centered at (0, G); cached by quantized length/curve/width.</summary>
    private static CompositionPath BladePath(int length, int curve, int width)
    {
        int key = length * 100 + (curve + 3) * 10 + width;
        if (s_paths.TryGetValue(key, out var path)) return path;
        float l = length;
        float w = (width == 0 ? 0.62f : 0.7f + 0.35f * width) * (0.85f + 0.3f * MathF.Min(l, 30f) / 30f);
        float c = curve * 0.065f * l;
        path = new PathBuilder()
            .MoveTo(-w, G)
            .CubicTo(-0.9f * w, G - 0.45f * l, 0.55f * c - 0.25f * w, G - 0.85f * l, c, G - l)
            .CubicTo(0.55f * c + 0.25f * w, G - 0.85f * l, 0.9f * w, G - 0.45f * l, w, G)
            .Close()
            .Build();
        s_paths[key] = path;
        return path;
    }

    private static CompositionPath PetalsPath(float radius)
    {
        int bucket = (int)MathF.Round(radius * 5);
        int key = -1 - bucket;
        if (s_paths.TryGetValue(key, out var path)) return path;
        float r = bucket / 5f;
        var pb = new PathBuilder(winding: true);
        for (int k = 0; k < 5; k++)
        {
            float a = k * MathF.Tau / 5 - MathF.PI / 2;
            pb.Circle(MathF.Cos(a) * 0.62f * r, MathF.Sin(a) * 0.62f * r, 0.5f * r);
        }
        path = pb.Build();
        s_paths[key] = path;
        return path;
    }

    private void AddMushroom(float x)
    {
        float capR = _rng.Range(3.2f, 6f);
        float capH = _rng.Range(2.6f, 4.8f);
        float stemH = _rng.Range(3.5f, 8f);
        float stemW = _rng.Range(2f, 3.6f);
        int cap = _rng.Int(0, 6);

        var m = Anim.C.CreateContainerShape();
        m.Offset = new Vector2(x, G);
        m.Shapes.Add(Shapes.ContactShadow(0, 0, capR * 1.1f, 1.5f, 60));
        m.Shapes.Add(Shapes.RoundRect(-stemW / 2, -stemH - 0.6f, stemW, stemH + 0.6f, stemW * 0.45f, _brushes.Stem));
        m.Shapes.Add(Shapes.Ellipse(0, -stemH, capR * 0.82f, 0.9f, Shapes.Solid(0x000000, 45)));
        m.Shapes.Add(Shapes.Path(new PathBuilder().MoveTo(-capR, -stemH).ArcTo(capR, -stemH, capR, capH, clockwise: true).Close(), _brushes.Cap[cap]));
        m.Shapes.Add(Shapes.Ellipse(-capR * 0.35f, -stemH - capH * 0.62f, capR * 0.3f, capH * 0.15f, Shapes.Solid(0xFFFFFF, 80)));
        if (cap == 0 || cap == 4)
        {
            var spot = Shapes.Solid(0xFFFFFF, 225);
            m.Shapes.Add(Shapes.Circle(-capR * 0.45f, -stemH - capH * 0.4f, capR * 0.13f, spot));
            m.Shapes.Add(Shapes.Circle(capR * 0.38f, -stemH - capH * 0.55f, capR * 0.15f, spot));
            m.Shapes.Add(Shapes.Circle(capR * 0.02f, -stemH - capH * 0.82f, capR * 0.11f, spot));
        }
        _mushroomVisual.Shapes.Add(m);
        _mushrooms.Add(new Mushroom { Shape = m, X = x, CapRadius = capR, Height = stemH + capH, Rank = _rng.Next() });
    }

    // ---------------------------------------------------------------- scenes

    public void ApplyPalette(Palette palette, bool animate)
    {
        if (_disposed) return;
        float flowerCut = palette.FlowerChance / MaxFlowerChance;
        foreach (var b in _blades)
        {
            if (b.Flower is not { } f) continue;
            bool visible = f.Rank < flowerCut;
            if (visible == f.Visible) continue;
            f.Visible = visible;
            if (b.Cut) continue; // the head is already gone; regrowth pops it back if still visible
            SetPresence(f.Head, visible, animate);
        }

        float mushroomCut = palette.MushroomChance / MaxMushroomChance;
        foreach (var m in _mushrooms)
        {
            bool visible = m.Rank < mushroomCut;
            if (visible == m.Visible) continue;
            m.Visible = visible;
            SetPresence(m.Shape, visible, animate);
        }

        for (int i = 0; i < _layers.Length; i++)
        {
            if (animate) _layers[i].StartAnimation("Scale.Y", Anim.Scalar(1.6, Anim.EaseInOut, 1f, palette.GrassHeight));
            else _layers[i].Scale = new Vector3(1, palette.GrassHeight, 1);
        }
    }

    private void SetPresence(CompositionShape shape, bool visible, bool animate)
    {
        if (!animate)
        {
            shape.StopAnimation("Scale");
            shape.Scale = visible ? Vector2.One : Vector2.Zero;
            return;
        }
        if (visible) Pop(shape, _rng.Range(0.2f, 1.4f));
        else shape.StartAnimation("Scale", Anim.Vec2(0.35, Anim.EaseIn, (1f, Vector2.Zero)));
    }

    private static void Pop(CompositionShape shape, double delay)
    {
        var a = Anim.C.CreateVector2KeyFrameAnimation();
        a.Duration = Anim.Sec(0.45);
        a.InsertKeyFrame(0f, Vector2.Zero, Anim.Linear);
        a.InsertKeyFrame(0.6f, new Vector2(1.18f), Anim.EaseOut);
        a.InsertKeyFrame(1f, Vector2.One, Anim.EaseInOut);
        a.DelayTime = TimeSpan.FromSeconds(delay);
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        shape.StartAnimation("Scale", a);
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
        b.Cut = true;
        b.Serial++;
        float stubble = Math.Clamp(MathF.Max(_rng.Range(0.06f, 0.16f), 2.5f / b.Length), 0.05f, 0.45f);
        b.Shape.StartAnimation("Scale.Y", Anim.Scalar(0.2, Anim.EaseOut, 1f, stubble));

        if (b.Flower is { Visible: true } f)
        {
            f.Head.StartAnimation("Scale", Anim.Vec2(0.15, Anim.EaseIn, (1f, Vector2.Zero)));
            var head = new Vector2(b.X + f.Tip.X, _strip.GroundY - G + f.Tip.Y);
            var color = Shapes.Solid(_brushes.Palette.Flowers[f.Color]);
            for (int k = 0; k < 5; k++)
            {
                float size = f.Radius * 1.4f + 2f;
                var petal = Shapes.Ellipse(size / 2, size / 2, f.Radius * 0.5f, f.Radius * 0.36f, color);
                Fx.Toss(_strip.Fx, petal, size, head, _rng.Range(-28f, 28f), _rng.Range(6f, 16f),
                    _strip.GroundY - head.Y + 14f, _rng.Range(-400f, 400f), _rng.Range(1.0f, 1.6f));
            }
        }

        if (clippings < 10 && _rng.Chance(0.65f))
        {
            clippings++;
            s_slivers ??= new[] { Sliver(3.2f, 0.5f), Sliver(4.2f, -0.6f), Sliver(5f, 0.9f) };
            var sliver = Shapes.Path(_rng.Pick(s_slivers), Shapes.Solid(_brushes.TipColor(b.Depth, b.Hue)));
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
        foreach (var (b, serial) in cut)
        {
            if (b.Serial != serial || !b.Cut) continue;
            b.Cut = false;
            double delay = _rng.Range(0f, 4f), seconds = _rng.Range(2f, 4f);
            var grow = Anim.Scalar(seconds, Anim.EaseInOut, 1f, 1f);
            grow.DelayTime = TimeSpan.FromSeconds(delay);
            b.Shape.StartAnimation("Scale.Y", grow);
            if (b.Flower is { Visible: true } f) Pop(f.Head, delay + seconds * 0.9);
        }
    }

    private void Boing(Mushroom m)
    {
        var a = Anim.C.CreateVector2KeyFrameAnimation();
        a.Duration = Anim.Sec(0.85);
        a.InsertKeyFrame(0.12f, new Vector2(1.3f, 0.66f), Anim.EaseOut);
        a.InsertKeyFrame(0.32f, new Vector2(0.84f, 1.2f), Anim.EaseInOut);
        a.InsertKeyFrame(0.52f, new Vector2(1.1f, 0.93f), Anim.EaseInOut);
        a.InsertKeyFrame(0.74f, new Vector2(0.97f, 1.04f), Anim.EaseInOut);
        a.InsertKeyFrame(1f, Vector2.One, Anim.EaseInOut);
        m.Shape.StartAnimation("Scale", a);

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
        _clock.Stopped -= OnClockStopped;
        foreach (var g in _active)
        {
            g.Props.StopAnimation("Th");
            g.Visual.StopAnimation("TransformMatrix");
        }
        foreach (var b in _buckets) b.Sprite?.StopAnimation("TransformMatrix");
        _strip.BackGrass.Children.RemoveAll();
        _strip.FrontGrass.Children.RemoveAll();
        foreach (var g in _groups) g.Visual.Dispose();
        foreach (var b in _buckets)
        {
            b.Sprite?.Dispose();
            b.Brush?.Dispose();
            b.Surface?.Dispose();
            b.Source.Children.RemoveAll();
            b.Source.Dispose();
        }
        _mushroomSprite.Dispose();
        _mushroomBrush.Dispose();
        _mushroomSurface.Dispose();
        _mushroomSource.Children.RemoveAll();
        _mushroomSource.Dispose();
        _groups.Clear();
        _buckets.Clear();
        _bucketIndex.Clear();
        _blades.Clear();
        _mushrooms.Clear();
        _active.Clear();
        _theta.Dispose();
        _matrix.Dispose();
        _sway.Dispose();
    }
}
