// Scheduler.cs - one-shot timers on the UI thread, multiplexed onto a single WinForms timer
// so that a quiet scene costs nothing between events.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DesktopGrass.Composition;

internal static class Clock
{
    private static readonly long s_start = Stopwatch.GetTimestamp();

    /// <summary>Seconds since process start.</summary>
    public static double Now => (Stopwatch.GetTimestamp() - s_start) / (double)Stopwatch.Frequency;
}

internal sealed class TimerToken
{
    internal Action? Action;
    internal double Due;

    public bool Pending => Action != null;

    public void Cancel() => Action = null;
}

internal sealed class Scheduler : IDisposable
{
    private readonly PriorityQueue<TimerToken, double> _queue = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private double _armedFor = double.MaxValue;
    private bool _disposed;

    public Scheduler()
    {
        _timer.Tick += OnTick;
    }

    public TimerToken After(double seconds, Action action)
    {
        var token = new TimerToken { Action = action, Due = Clock.Now + Math.Max(0, seconds) };
        _queue.Enqueue(token, token.Due);
        Arm();
        return token;
    }

    private void Arm()
    {
        if (_disposed) return;
        while (_queue.TryPeek(out var head, out _) && !head.Pending) _queue.Dequeue();
        if (!_queue.TryPeek(out var next, out double due))
        {
            _timer.Stop();
            _armedFor = double.MaxValue;
            return;
        }
        if (_timer.Enabled && Math.Abs(due - _armedFor) < 0.001) return;
        _timer.Stop();
        _armedFor = due;
        _timer.Interval = Math.Max(1, (int)Math.Ceiling((due - Clock.Now) * 1000));
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        _armedFor = double.MaxValue;
        double now = Clock.Now + 0.004;
        int guard = 0;
        while (_queue.TryPeek(out var token, out double due) && due <= now && guard++ < 512)
        {
            _queue.Dequeue();
            var action = token.Action;
            token.Action = null;
            if (action == null) continue;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Write($"scheduled action failed: {ex}");
            }
        }
        Arm();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        _queue.Clear();
    }
}

internal static class Log
{
    private static readonly object s_lock = new();
    public static string? FilePath { get; set; }

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Debug.WriteLine(line);
        if (FilePath == null) return;
        lock (s_lock)
        {
            try { System.IO.File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
        }
    }
}
