// MouseHook.cs - global low-level mouse hook on its own thread. The callback only records the latest
// cursor position (moves) or queues the click, then wakes the UI thread once; all work happens there.
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using DesktopGrass.Composition.Interop;

namespace DesktopGrass.Composition;

internal readonly record struct MouseClick(int X, int Y);

internal sealed unsafe class MouseHook : IDisposable
{
    private static MouseHook? s_instance;

    private readonly IntPtr _notifyHwnd;
    private readonly int _notifyMessage;
    private readonly ConcurrentQueue<MouseClick> _clicks = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private long _position;
    private int _moveSeq;
    private int _posted;

    public bool Installed { get; private set; }

    public MouseHook(IntPtr notifyHwnd, int notifyMessage)
    {
        _notifyHwnd = notifyHwnd;
        _notifyMessage = notifyMessage;
        s_instance = this;
        _thread = new Thread(Run) { IsBackground = true, Name = "MouseHook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(2000);
    }

    private void Run()
    {
        _threadId = Win32.GetCurrentThreadId();
        var proc = (IntPtr)(delegate* unmanaged<int, IntPtr, IntPtr, IntPtr>)&HookProc;
        IntPtr hook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, proc, Win32.GetModuleHandle(null), 0);
        Installed = hook != IntPtr.Zero;
        _ready.Set();
        if (!Installed)
        {
            Log.Write($"mouse hook failed (error {Marshal.GetLastSystemError()})");
            return;
        }
        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(msg);
            Win32.DispatchMessage(msg);
        }
        Win32.UnhookWindowsHookEx(hook);
    }

    [UnmanagedCallersOnly]
    private static IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        var self = s_instance;
        if (code >= 0 && self != null)
        {
            var info = (Win32.MSLLHOOKSTRUCT*)lParam;
            switch ((int)wParam)
            {
                case Win32.WM_MOUSEMOVE:
                    Interlocked.Exchange(ref self._position, ((long)info->pt.X << 32) | (uint)info->pt.Y);
                    Interlocked.Increment(ref self._moveSeq);
                    self.Wake();
                    break;
                case Win32.WM_LBUTTONDOWN:
                    self._clicks.Enqueue(new MouseClick(info->pt.X, info->pt.Y));
                    self.Wake();
                    break;
            }
        }
        return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void Wake()
    {
        if (Interlocked.Exchange(ref _posted, 1) == 0) Win32.PostMessage(_notifyHwnd, _notifyMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>UI thread: call when the wake message arrives, before reading clicks and position.</summary>
    public void Acknowledge() => Interlocked.Exchange(ref _posted, 0);

    public int MoveSeq => Volatile.Read(ref _moveSeq);

    public (int X, int Y) Position
    {
        get
        {
            long p = Interlocked.Read(ref _position);
            return ((int)(p >> 32), (int)p);
        }
    }

    public bool TryDequeueClick(out MouseClick click) => _clicks.TryDequeue(out click);

    public void Dispose()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        if (s_instance == this) s_instance = null;
        _ready.Dispose();
    }
}
