using System.Diagnostics;
using System.Globalization;
using Scrimshaw.Core.Detection;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Imaging;

namespace Scrimshaw.Harness;

/// <summary>
/// NFR-3 check without a 4K monitor: times the per-capture work (change detection, on the capture thread)
/// and per-save work (PNG/JPEG encode, on the writer thread) for synthetic frames of a given size.
/// <code>bench [--width 3840] [--height 2160] [--runs 20] [--interval 0.5]</code>
/// </summary>
internal static class BenchCommand
{
    public static int Run(CommandLine cl)
    {
        int w = cl.Int("width", 3840), h = cl.Int("height", 2160), runs = cl.Int("runs", 20);
        double intervalMs = cl.Double("interval", 0.5) * 1000;
        RawImage[] frames = [Score(w, h, 1), Score(w, h, 2)];
        var detector = new ChangeDetector(new ChangeDetectionSettings());
        var codec = new WicImageCodec();
        string tmp = Path.Combine(Path.GetTempPath(), "scrimshaw-bench");
        Directory.CreateDirectory(tmp);

        // Warm up JIT and WIC.
        detector.Process(frames[0]);
        codec.Encode(frames[0], Path.Combine(tmp, "warm.png"), ImageFormat.Png, 0);

        double detect = Time(runs, i => detector.Process(frames[i % 2]));
        double png = Time(Math.Max(3, runs / 4), i => codec.Encode(frames[i % 2], Path.Combine(tmp, "b.png"), ImageFormat.Png, 0));
        double jpg = Time(Math.Max(3, runs / 4), i => codec.Encode(frames[i % 2], Path.Combine(tmp, "b.jpg"), ImageFormat.Jpeg, 90));
        long pngBytes = new FileInfo(Path.Combine(tmp, "b.png")).Length, jpgBytes = new FileInfo(Path.Combine(tmp, "b.jpg")).Length;
        Directory.Delete(tmp, recursive: true);

        // Capture thread: detection must fit well inside the interval. Writer: one save per page, so it only has
        // to keep up with page turns; even saving every tick, encode time below the interval means no backlog.
        bool ok = detect < intervalMs * 0.25 && png < intervalMs;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"""
            frame {w}x{h}, interval {intervalMs:0} ms
              change detection per capture: {detect,7:0.0} ms  (budget {intervalMs * 0.25:0} ms)
              PNG encode per saved frame:   {png,7:0.0} ms  ({pngBytes / 1024.0 / 1024:0.0} MB)
              JPEG q90 encode per frame:    {jpg,7:0.0} ms  ({jpgBytes / 1024.0 / 1024:0.0} MB)
              => {(ok ? "PASS" : "FAIL")}: keeps up even if every capture were a new page
            """));
        return ok ? 0 : 1;
    }

    private static double Time(int runs, Action<int> work)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < runs; i++) work(i);
        return sw.Elapsed.TotalMilliseconds / runs;
    }

    /// <summary>White page with dark marks; enough structure for realistic PNG compression.</summary>
    private static RawImage Score(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var px = new byte[w * h * 4];
        Array.Fill(px, (byte)255);
        for (int n = 0; n < w * h / 900; n++)
        {
            int x0 = rng.Next(w - 20), y0 = rng.Next(h - 20), bw = rng.Next(2, 18), bh = rng.Next(2, 18);
            for (int y = y0; y < y0 + bh; y++)
                for (int x = x0; x < x0 + bw; x++)
                {
                    int o = (y * w + x) * 4;
                    px[o] = px[o + 1] = px[o + 2] = 0;
                }
        }
        return new RawImage(w, h, px);
    }
}
