// Snapshot.cs - grabs the composed screen under a strip (used by --snapshot for docs and tests).
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace DesktopGrass.Composition;

internal static class Snapshot
{
    public static void Capture(int x, int y, int width, int height, string path)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        bmp.Save(full, ImageFormat.Png);
    }
}
