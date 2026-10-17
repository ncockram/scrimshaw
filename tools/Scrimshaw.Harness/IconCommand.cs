using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Scrimshaw.Harness;

/// <summary>
/// Draws the app icon (a score page with staff lines and notes, framed by orange box corners: what Scrimshaw
/// does) and writes a multi-size .ico plus a 256 px preview PNG.
/// <code>icon &lt;out.ico&gt;</code>
/// </summary>
internal static class IconCommand
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    public static int Run(CommandLine cl)
    {
        string output = Path.GetFullPath(cl.Arg(0, "out.ico"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var images = Sizes.Select(s => (Size: s, Png: Png(Render(s)))).ToList();

        using (var fs = File.Create(output))
        using (var w = new BinaryWriter(fs))
        {
            // ICONDIR, then one ICONDIRENTRY per image, then the PNG data (PNG-in-ICO, Windows Vista+).
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)images.Count);
            int offset = 6 + 16 * images.Count;
            foreach (var (size, png) in images)
            {
                w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(png.Length);
                w.Write(offset);
                offset += png.Length;
            }
            foreach (var (_, png) in images) w.Write(png);
        }
        File.WriteAllBytes(Path.ChangeExtension(output, ".png"), images[^1].Png);
        Console.WriteLine($"{output} ({string.Join(", ", Sizes)} px)");
        return 0;
    }

    private static RenderTargetBitmap Render(int size)
    {
        double k = size / 256.0;
        bool small = size <= 24;
        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(k, k));
            var tile = new LinearGradientBrush(Color.FromRgb(0x26, 0x4D, 0x78), Color.FromRgb(0x16, 0x2E, 0x4A), 90);
            dc.DrawRoundedRectangle(tile, null, new Rect(0, 0, 256, 256), 52, 52);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xF7, 0xF1, 0xE3)), null, new Rect(36, 30, 184, 196), 14, 14);

            // Staff lines: fewer and thicker when tiny, so they stay distinct pixels.
            var ink = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            int lines = small ? 3 : 5;
            double spacing = small ? 26 : 15, thickness = Math.Max(small ? 14 : 5.5, 1 / k), top = small ? 92 : 90;
            for (int i = 0; i < lines; i++)
                dc.DrawRectangle(ink, null, new Rect(58, top + i * spacing - thickness / 2, 140, thickness));

            if (!small)
            {
                foreach (var (x, y, up) in new[] { (86.0, 127.0, true), (122.0, 105.0, true), (158.0, 120.0, false) })
                {
                    dc.PushTransform(new RotateTransform(-20, x, y));
                    dc.DrawEllipse(ink, null, new Point(x, y), 11.5, 8.5);
                    dc.Pop();
                    double sx = up ? x + 10 : x - 10;
                    dc.DrawRectangle(ink, null, new Rect(sx - 2.5, up ? y - 46 : y, 5, 46));
                }
            }

            // Orange bounding-box corners.
            var orange = new SolidColorBrush(Color.FromRgb(0xF0, 0x8A, 0x24));
            double b = small ? 16 : 10, len = small ? 44 : 34;
            var box = new Rect(46, 62, 164, 120);
            foreach (var (cx, cy, dx, dy) in new[] { (box.Left, box.Top, 1, 1), (box.Right, box.Top, -1, 1), (box.Left, box.Bottom, 1, -1), (box.Right, box.Bottom, -1, -1) })
            {
                dc.DrawRectangle(orange, null, new Rect(dx > 0 ? cx : cx - len, dy > 0 ? cy : cy - b, len, b));
                dc.DrawRectangle(orange, null, new Rect(dx > 0 ? cx : cx - b, dy > 0 ? cy : cy - len, b, len));
            }
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static byte[] Png(BitmapSource image)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
