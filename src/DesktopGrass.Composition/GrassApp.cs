// GrassApp.cs - owns the strips and global state, routes input and runs the ambient timers.
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using DesktopGrass.Composition.Interop;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal sealed class GrassApp : IDisposable
{
    /// <summary>Cursor moves are coalesced to at most this rate (the springs make it look continuous).</summary>
    private const double MoveInterval = 1.0 / 40;

    private readonly Compositor _compositor;
    private readonly Options _options;
    private readonly string _settingsPath;
    private readonly Settings _settings;
    private readonly Scheduler _scheduler = new();
    private readonly List<Strip> _strips = new();
    private readonly List<GrassField> _fields = new();
    private readonly ulong _seed;
    private readonly Rng _rng;
    private readonly GrassConfig _grassConfig;
    private readonly Wind _wind;
    private readonly GustClock _gusts;
    private GrassBrushes? _brushes;
    private AppWindow? _window;
    private MouseHook? _hook;
    private Tray? _tray;
    private TimerToken? _rebuild;
    private int _generation;
    private bool _motionApplied, _locked, _batterySaver;

    private int _seenMoveSeq;
    private double _lastMoveAt;
    private TimerToken? _moveFlush;
    private int _lastX, _lastY;
    private bool _hasLastPosition;
    private int _statWakes, _statMoves, _statGusts, _statClicks;

    public Palette Palette { get; private set; } = Palette.Meadow;
    public MotionMode Motion { get; private set; }
    public bool Paused { get; private set; }

    private MotionMode EffectiveMotion => Paused || _locked || _batterySaver ? MotionMode.Calm : Motion;

    public GrassApp(Compositor compositor, Options options, string dataDir)
    {
        _compositor = compositor;
        _options = options;
        _settingsPath = Path.Combine(dataDir, "state.json");
        _settings = options.NoSettings ? new Settings() : Settings.Load(_settingsPath);
        _seed = options.Seed ?? (ulong)DateTime.UtcNow.Ticks;
        _rng = new Rng(_seed ^ 0x5DEECE66DUL);
        _grassConfig = new GrassConfig(options.SegmentWidth ?? 30f, options.FrontSegmentWidth ?? 48f, options.Density ?? 1f);
        _wind = new Wind(compositor, _scheduler);
        _gusts = new GustClock(compositor, _scheduler);
    }

    public void Start()
    {
        string scene = _options.Scene ?? _settings.Scene;
        if (!Palette.TryParse(scene, out var kind)) Log.Write($"unknown scene '{scene}', using Meadow");
        Palette = Palette.From(kind);
        Motion = ParseMotion(_options.Motion ?? _settings.Motion) ?? DefaultMotion();
        _brushes = new GrassBrushes(_compositor, Palette, GrassField.G);

        BuildStrips();
        ApplyMotion(animate: false);

        _window = new AppWindow();
        _window.MouseReady += OnMouseReady;
        _window.DisplayChanged += ScheduleRebuild;
        _window.FullscreenChanged += OnFullscreenChanged;
        _window.SessionLockChanged += locked => { _locked = locked; ApplyMotion(animate: false); };
        _window.BatterySaverChanged += on => { _batterySaver = on; ApplyMotion(animate: true); };
        _window.AnimationSettingChanged += () =>
        {
            if (_options.Motion != null || _settings.Motion != null) return;
            Motion = DefaultMotion();
            ApplyMotion(animate: true);
        };
        StripWindow.DpiChanged += OnStripDpiChanged;

        if (!_options.NoHook)
        {
            _hook = new MouseHook(_window.Handle, AppWindow.WM_MOUSE_READY);
            Log.Write(_hook.Installed ? "mouse hook installed" : "mouse hook NOT installed");
        }
        _tray = new Tray(this);

        if (_options.Demo) RunDemo();
        ScheduleSnapshots();
        if (_options.ExitAfter > 0) _scheduler.After(_options.ExitAfter, Quit);
        Log.Write($"started: scene {Palette.Name}, motion {Motion}, seed {_seed}");
    }

    private static MotionMode? ParseMotion(string? value) =>
        Enum.TryParse<MotionMode>(value, ignoreCase: true, out var mode) ? mode : null;

    private static MotionMode DefaultMotion() => Win32.ClientAreaAnimationEnabled() ? MotionMode.Full : MotionMode.Calm;

    // ---------------------------------------------------------------- strips

    private void BuildStrips()
    {
        var monitors = Monitors.Enumerate();
        for (int i = 0; i < monitors.Count; i++)
        {
            var strip = new Strip(_compositor, monitors[i]);
            _strips.Add(strip);
            Log.Write($"strip {i}: monitor {monitors[i].Bounds} work {monitors[i].Work} dpi {monitors[i].Dpi} -> {strip.Width:F0}x{strip.Height:F0} DIP");
            _fields.Add(new GrassField(strip, _wind, _gusts, _brushes!, _scheduler, new Rng(_seed + (ulong)i * 0x9E3779B9UL), _grassConfig));
            SchedulePuff(i, _generation);
        }
        UpdateVisibility();
    }

    private void TearDownStrips()
    {
        _generation++;
        _gusts.Stop();
        foreach (var f in _fields) f.Dispose();
        _fields.Clear();
        foreach (var s in _strips) s.Dispose();
        _strips.Clear();
    }

    private void ScheduleRebuild(string reason)
    {
        _rebuild?.Cancel();
        _rebuild = _scheduler.After(0.4, () =>
        {
            Log.Write($"rebuilding strips ({reason})");
            TearDownStrips();
            BuildStrips();
        });
    }

    private void OnStripDpiChanged() => ScheduleRebuild("strip dpi change");

    private void UpdateVisibility()
    {
        foreach (var s in _strips)
        {
            bool show = !Paused && !s.CoveredByFullscreen;
            if (show && !s.Window.Visible) s.Show();
            else if (!show && s.Window.Visible) s.Hide();
        }
    }

    private void OnFullscreenChanged(bool opened)
    {
        // Hide the strip on any monitor the foreground window fully covers. A "closed" notification
        // re-evaluates everything, since it doesn't say which monitor was released.
        if (!opened) foreach (var s in _strips) s.CoveredByFullscreen = false;
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != Win32.GetShellWindow() && fg != Win32.GetDesktopWindow() &&
            Win32.GetWindowRect(fg, out var r) != 0 && Win32.ClassName(fg) is not ("Progman" or "WorkerW"))
        {
            foreach (var s in _strips)
            {
                var b = s.Monitor.Bounds;
                if (r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom) s.CoveredByFullscreen = true;
            }
        }
        Log.Write($"fullscreen app {(opened ? "opened" : "closed")}");
        UpdateVisibility();
    }

    // ---------------------------------------------------------------- ambient motion

    private void ApplyMotion(bool animate)
    {
        var mode = EffectiveMotion;
        if (_motionApplied && _wind.Mode == mode) return;
        _motionApplied = true;
        _wind.SetMode(mode, animate);
        Log.Write($"motion -> {mode}");
    }

    private void SchedulePuff(int index, int generation)
    {
        _scheduler.After(_rng.Range(5f, 15f), () =>
        {
            if (generation != _generation) return;
            if (EffectiveMotion == MotionMode.Full && _strips[index].Window.Visible) _fields[index].Puff(_rng);
            SchedulePuff(index, generation);
        });
    }

    // ---------------------------------------------------------------- input

    private void OnMouseReady()
    {
        var hook = _hook;
        if (hook == null) return;
        _statWakes++;
        hook.Acknowledge();
        while (hook.TryDequeueClick(out var click)) OnClick(click.X, click.Y);
        FlushMoves();
    }

    private void FlushMoves()
    {
        var hook = _hook;
        if (hook == null) return;
        int seq = hook.MoveSeq;
        if (seq == _seenMoveSeq) return;
        double now = Clock.Now;
        double wait = _lastMoveAt + MoveInterval - now;
        if (wait > 0.001)
        {
            if (_moveFlush is not { Pending: true }) _moveFlush = _scheduler.After(wait, FlushMoves);
            return;
        }
        _seenMoveSeq = seq;
        _lastMoveAt = now;
        _statMoves++;
        var (x, y) = hook.Position;
        if (_hasLastPosition) OnMove(x, y, x - _lastX, y - _lastY);
        _lastX = x;
        _lastY = y;
        _hasLastPosition = true;
    }

    private void OnMove(int x, int y, int dxPx, int dyPx)
    {
        if (Paused || (dxPx == 0 && dyPx == 0)) return;
        int i = StripAt(x, y);
        if (i < 0) return;
        var s = _strips[i];
        var p = s.ToLocal(x, y);
        _statGusts++;
        _fields[i].Gust(p.X, p.Y, dxPx / s.Scale, dyPx / s.Scale);
    }

    private void OnClick(int x, int y)
    {
        if (Paused) return;
        _statClicks++;
        int i = StripAt(x, y);
        if (i < 0) return;
        var p = _strips[i].ToLocal(x, y);
        _fields[i].Click(p.X, p.Y);
    }

    private int StripAt(int x, int y)
    {
        for (int i = 0; i < _strips.Count; i++)
        {
            var s = _strips[i];
            if (s.Window.Visible && s.ContainsScreenX(x) && y >= s.PxTop && y <= s.PxTop + s.PxHeight + 16 * s.Scale) return i;
        }
        return -1;
    }

    // ---------------------------------------------------------------- tray commands

    public void SetScene(SceneKind kind)
    {
        if (Palette.Kind == kind) return;
        Palette = Palette.From(kind);
        _brushes!.SetPalette(Palette, animate: true);
        foreach (var f in _fields) f.ApplyPalette(Palette, animate: true);
        _settings.Scene = Palette.Name;
        SaveSettings();
        Log.Write($"scene -> {Palette.Name}");
    }

    public void SetMotion(MotionMode mode)
    {
        Motion = mode;
        _settings.Motion = mode.ToString();
        SaveSettings();
        ApplyMotion(animate: true);
    }

    public void SetPaused(bool paused)
    {
        if (Paused == paused) return;
        Paused = paused;
        if (paused) _gusts.Stop();
        ApplyMotion(animate: false);
        UpdateVisibility();
        Log.Write(paused ? "paused" : "resumed");
    }

    public void GustNow()
    {
        for (int i = 0; i < _fields.Count; i++)
        {
            if (_strips[i].Window.Visible) _fields[i].Puff(_rng, 1.8f);
        }
    }

    public void Quit() => Application.ExitThread();

    private void SaveSettings()
    {
        if (!_options.NoSettings) _settings.Save(_settingsPath);
    }

    // ---------------------------------------------------------------- testing aids

    /// <summary>Scripted interaction on the first strip so snapshots can show gusts, a cut and a boing.</summary>
    private void RunDemo()
    {
        var field = _fields[0];
        var strip = _strips[0];
        float y = strip.GroundY - 14f;
        float x0 = 160f, x1 = MathF.Min(strip.Width - 260f, 1500f);
        const int steps = 36;
        for (int k = 1; k <= steps; k++)
        {
            float x = x0 + (x1 - x0) * k / steps;
            float dx = (x1 - x0) / steps;
            _scheduler.After(1.0 + k / 30.0, () => field.Gust(x, y, dx, 0));
        }
        _scheduler.After(2.4, () => Log.Write($"demo cut hit: {field.Click(x1 + 100f, strip.GroundY - 8f)}"));
        if (field.TryFindMushroom(x1 + 200f, out float mx))
            _scheduler.After(2.6, () => Log.Write($"demo boing at {mx:F0} hit: {field.Click(mx, strip.GroundY - 3f)}"));

        // Exercise every tray command once.
        _scheduler.After(3.6, GustNow);
        _scheduler.After(4.2, () => SetScene(Palette.Kind == SceneKind.Autumn ? SceneKind.Meadow : SceneKind.Autumn));
        _scheduler.After(4.8, () => SetMotion(MotionMode.Calm));
        _scheduler.After(5.2, () => SetMotion(MotionMode.Full));
        _scheduler.After(5.4, () => SetPaused(true));
        _scheduler.After(5.6, () => SetPaused(false));
    }

    private void ScheduleSnapshots()
    {
        if (_options.SnapshotPath is not { } basePath) return;
        var times = _options.SnapshotTimes;
        for (int k = 0; k < times.Length; k++)
        {
            string path = times.Length == 1
                ? basePath
                : Path.Combine(Path.GetDirectoryName(basePath) ?? "", $"{Path.GetFileNameWithoutExtension(basePath)}-{k + 1}{Path.GetExtension(basePath)}");
            _scheduler.After(times[k], () =>
            {
                if (_strips.Count == 0) return;
                var s = _strips[Math.Clamp(_options.SnapshotStrip, 0, _strips.Count - 1)];
                Snapshot.Capture(s.PxLeft, s.PxTop, s.PxWidth, s.PxHeight, path);
                Log.Write($"snapshot saved: {path}");
            });
        }
    }

    public void Dispose()
    {
        StripWindow.DpiChanged -= OnStripDpiChanged;
        if (_hook != null) Log.Write($"input: {_hook.MoveSeq} hook moves, {_statWakes} wakes, {_statMoves} processed, {_statGusts} gusts, {_statClicks} clicks");
        _hook?.Dispose();
        _tray?.Dispose();
        _window?.Dispose();
        TearDownStrips();
        _scheduler.Dispose();
    }
}
