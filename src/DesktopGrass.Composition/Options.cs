// Options.cs - command-line switches (mostly for testing and measurement).
using System;
using System.Globalization;
using System.Linq;

namespace DesktopGrass.Composition;

internal sealed class Options
{
    public string? Scene;
    public string? Motion;
    public double ExitAfter = -1;
    public string? SnapshotPath;
    public double[] SnapshotTimes = { 3 };
    public int SnapshotStrip;
    public ulong? Seed;
    public bool LogToFile;
    public bool AllowMultiple;
    public bool NoCritters;
    public bool NoSky;
    public bool BirdsNow;
    public bool TrainNow;
    public bool NoSettings;
    public bool NoHook;
    public bool Demo;
    public float? SegmentWidth;
    public float? FrontSegmentWidth;
    public float? Density;
    public float? BucketWidth;
    public bool NoSway;
    public bool NoGrass;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            float NextFloat() => float.Parse(Next(), CultureInfo.InvariantCulture);
            switch (a)
            {
                case "--scene": o.Scene = Next().ToLowerInvariant(); break;
                case "--motion": o.Motion = Next().ToLowerInvariant(); break;
                case "--exit-after": o.ExitAfter = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--snapshot": o.SnapshotPath = Next(); break;
                case "--snapshot-after":
                    o.SnapshotTimes = Next().Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    break;
                case "--snapshot-strip": o.SnapshotStrip = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seed": o.Seed = ulong.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--log": o.LogToFile = true; break;
                case "--multi": o.AllowMultiple = true; break;
                case "--no-critters": o.NoCritters = true; break;
                case "--no-sky": o.NoSky = true; break;
                case "--birds-now": o.BirdsNow = true; break;
                case "--train-now": o.TrainNow = true; break;
                case "--no-settings": o.NoSettings = true; break;
                case "--no-hook": o.NoHook = true; break;
                case "--demo": o.Demo = true; break;
                case "--seg": o.SegmentWidth = NextFloat(); break;
                case "--front-seg": o.FrontSegmentWidth = NextFloat(); break;
                case "--density": o.Density = NextFloat(); break;
                case "--bucket": o.BucketWidth = NextFloat(); break;
                case "--no-sway": o.NoSway = true; break;
                case "--no-grass": o.NoGrass = true; break;
            }
        }
        return o;
    }
}
