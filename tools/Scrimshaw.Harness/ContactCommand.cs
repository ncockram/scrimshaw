using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Scrimshaw.Harness;

/// <summary>
/// Tiles numbered thumbnails of a frame sequence into one image, for labelling recordings by eye.
/// <code>contact &lt;framesDir&gt; &lt;out.png&gt; [--from 1] [--to 9999] [--step 1] [--cols 8] [--width 240]</code>
/// </summary>
internal static partial class ContactCommand
{
    public static int Run(CommandLine cl)
    {
        string dir = cl.Arg(0, "framesDir"), output = Path.GetFullPath(cl.Arg(1, "out.png"));
        int from = cl.Int("from", 1), to = cl.Int("to", 9999), step = Math.Max(1, cl.Int("step", 1));
        int cols = cl.Int("cols", 8), width = cl.Int("width", 240);

        var frames = Directory.EnumerateFiles(dir)
            .Select(f => (Path: f, M: FrameName().Match(Path.GetFileName(f))))
            .Where(x => x.M.Success)
            .Select(x => (x.Path, N: int.Parse(x.M.Groups[1].Value, CultureInfo.InvariantCulture)))
            .Where(x => x.N >= from && x.N <= to && (x.N - from) % step == 0)
            .OrderBy(x => x.N)
            .ToList();
        if (frames.Count == 0)
        {
            Console.Error.WriteLine("no frames in range");
            return 2;
        }

        BitmapFrame first = Load(frames[0].Path, width);
        int tileH = first.PixelHeight, label = 18, rows = (frames.Count + cols - 1) / cols;
        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, cols * (width + 4), rows * (tileH + label + 4)));
            for (int i = 0; i < frames.Count; i++)
            {
                double x = i % cols * (width + 4), y = i / cols * (tileH + label + 4);
                dc.DrawImage(Load(frames[i].Path, width), new Rect(x, y + label, width, tileH));
                dc.DrawRectangle(null, new Pen(Brushes.Gray, 1), new Rect(x, y + label, width, tileH));
                var text = new FormattedText(frames[i].N.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 13, Brushes.Firebrick, 1.0);
                dc.DrawText(text, new Point(x + 2, y + 1));
            }
        }
        var rtb = new RenderTargetBitmap(cols * (width + 4), rows * (tileH + label + 4), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (FileStream fs = File.Create(output)) enc.Save(fs);
        Console.WriteLine($"{frames.Count} frames -> {output}");
        return 0;
    }

    private static BitmapFrame Load(string path, int width)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.DecodePixelWidth = width;
        img.UriSource = new Uri(Path.GetFullPath(path));
        img.EndInit();
        img.Freeze();
        return BitmapFrame.Create(img);
    }

    [GeneratedRegex(@"^frame_(\d+)\.(png|jpg)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrameName();
}
