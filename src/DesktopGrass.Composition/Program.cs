// Program.cs - entry point: single instance, DPI awareness, DispatcherQueue + Compositor, run loop.
using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using DesktopGrass.Composition.Interop;
using Windows.UI.Composition;

namespace DesktopGrass.Composition;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Options options = Options.Parse(args);
        string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopGrass.Composition");
        Directory.CreateDirectory(dataDir);
        if (options.LogToFile) Log.FilePath = Path.Combine(dataDir, "log.txt");

        using var mutex = new Mutex(true, @"Local\DesktopGrass.Composition.SingleInstance", out bool firstInstance);
        if (!firstInstance && !options.AllowMultiple) return 0;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var queueController = CompositionInterop.CreateDispatcherQueueForCurrentThread();
        var compositor = new Compositor();
        Anim.Init(compositor);
        Log.Write($"compositor ready (pid {Environment.ProcessId})");

        using (var app = new GrassApp(compositor, options, dataDir))
        {
            app.Start();
            Application.Run();
        }

        compositor.Dispose();
        GC.KeepAlive(queueController);
        return 0;
    }
}
