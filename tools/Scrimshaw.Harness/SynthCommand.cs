using System.Globalization;
using System.Text.Json;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Imaging;

namespace Scrimshaw.Harness;

/// <summary>
/// Writes a labelled synthetic "recording" (one image per capture tick) for <see cref="ReplayCommand"/>:
/// generated score pages turned in a given style, with per-frame noise and occasional cursor blips.
/// <code>synth &lt;outDir&gt; [--style cut|half|fade|slide|mixed] [--pages 8] [--hold 5] [--noise 4] [--seed 1]</code>
/// </summary>
internal static class SynthCommand
{
    private const int W = 1920, H = 1080, Scale = 6; // pages are drawn at 320×180 and scaled up
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Run(CommandLine cl)
    {
        string outDir = Path.GetFullPath(cl.Arg(0, "outDir"));
        string style = cl.Get("style") ?? "mixed";
        int pages = cl.Int("pages", 8), hold = cl.Int("hold", 5), noise = cl.Int("noise", 4), seed = cl.Int("seed", 1);
        Directory.CreateDirectory(outDir);
        foreach (string old in Directory.EnumerateFiles(outDir, "frame_*.png")) File.Delete(old);

        var rng = new Random(seed);
        var codec = new WicImageCodec();
        var labels = new List<object>();
        int n = 0;

        void Emit(RawImage img, bool blipAllowed = false)
        {
            n++;
            RawImage frame = Noisy(img, noise, seed * 100_000 + n);
            if (blipAllowed && rng.Next(6) == 0) frame = WithCursor(frame, rng.Next(100, W - 100), rng.Next(100, H - 100));
            codec.Encode(frame, Path.Combine(outDir, string.Create(CultureInfo.InvariantCulture, $"frame_{n:D5}.png")), ImageFormat.Png, 0);
        }

        void Hold(string id, RawImage img)
        {
            int first = n + 1;
            for (int i = 0; i < hold; i++) Emit(img, blipAllowed: i > 0 && i < hold - 1);
            labels.Add(new { id, first, last = n });
        }

        RawImage current = Page(1);
        Hold("p1", current);
        for (int p = 2; p <= pages; p++)
        {
            RawImage next = Page(p);
            string turn = style == "mixed" ? new[] { "cut", "half", "fade", "slide" }[rng.Next(4)] : style;
            switch (turn)
            {
                case "cut":
                    break;
                case "fade":
                    Emit(Blend(current, next, 0.35));
                    Emit(Blend(current, next, 0.7));
                    break;
                case "slide":
                    Emit(Slide(current, next, 0.33));
                    Emit(Slide(current, next, 0.66));
                    break;
                case "half":
                    Hold($"p{p}-top", HalfTurn(current, next)); // partial turn: must be kept too (FR-D5)
                    break;
                default:
                    throw new ArgumentException($"unknown style '{turn}'");
            }
            Hold($"p{p}", next);
            current = next;
        }

        var doc = new
        {
            description = $"Synthetic: style={style} pages={pages} hold={hold} noise=±{noise} seed={seed}, {W}x{H}",
            pages = labels,
        };
        File.WriteAllText(Path.Combine(outDir, "labels.json"), JsonSerializer.Serialize(doc, Indented));
        Console.WriteLine($"wrote {n} frames, {labels.Count} labelled pages to {outDir}");
        return 0;
    }

    /// <summary>Score-like page, drawn small and scaled up with nearest-neighbour.</summary>
    private static RawImage Page(int id)
    {
        const int w = W / Scale, h = H / Scale;
        var small = new byte[w * h];
        Array.Fill(small, (byte)255);
        void Fill(int x, int y, int fw, int fh)
        {
            for (int yy = Math.Max(0, y); yy < Math.Min(h, y + fh); yy++)
                for (int xx = Math.Max(0, x); xx < Math.Min(w, x + fw); xx++) small[yy * w + xx] = 0;
        }

        var rng = new Random(id * 7919);
        for (int system = 0; system < 3; system++)
        {
            int top = 12 + system * (h / 3);
            for (int line = 0; line < 5; line++) Fill(8, top + line * 5, w - 16, 1);
            for (int n = 0; n < (w - 24) / 8; n++)
            {
                int x = 12 + n * 8, y = top - 4 + rng.Next(0, 26);
                Fill(x, y, 6, 5);
                bool up = rng.Next(2) == 0;
                Fill(up ? x + 5 : x, up ? y - 14 : y + 3, 1, 14);
                if (rng.Next(3) == 0) Fill(x, up ? y - 14 : y + 16, 9, 2);
            }
        }

        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                byte v = small[y / Scale * w + x / Scale];
                int o = (y * W + x) * 4;
                px[o] = px[o + 1] = px[o + 2] = v;
                px[o + 3] = 255;
            }
        return new RawImage(W, H, px);
    }

    private static RawImage Blend(RawImage a, RawImage b, double t)
    {
        var px = new byte[a.Bgra.Length];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)Math.Round(a.Bgra[i] * (1 - t) + b.Bgra[i] * t);
        return new RawImage(a.Width, a.Height, px);
    }

    /// <summary>The new page slides in from the right, pushing the old one out.</summary>
    private static RawImage Slide(RawImage from, RawImage to, double t)
    {
        int shift = (int)(W * t), stride = W * 4;
        var px = new byte[from.Bgra.Length];
        for (int y = 0; y < H; y++)
        {
            Array.Copy(from.Bgra, y * stride + shift * 4, px, y * stride, (W - shift) * 4);
            Array.Copy(to.Bgra, y * stride, px, y * stride + (W - shift) * 4, shift * 4);
        }
        return new RawImage(W, H, px);
    }

    private static RawImage HalfTurn(RawImage from, RawImage to)
    {
        var px = (byte[])from.Bgra.Clone();
        Array.Copy(to.Bgra, px, W * 4 * (H / 2));
        return new RawImage(W, H, px);
    }

    private static RawImage Noisy(RawImage img, int amplitude, int seed)
    {
        if (amplitude <= 0) return img;
        var rng = new Random(seed);
        var px = (byte[])img.Bgra.Clone();
        for (int i = 0; i < px.Length; i++) px[i] = (byte)Math.Clamp(px[i] + rng.Next(-amplitude, amplitude + 1), 0, 255);
        return new RawImage(img.Width, img.Height, px);
    }

    private static RawImage WithCursor(RawImage img, int x, int y)
    {
        var px = (byte[])img.Bgra.Clone();
        for (int yy = y; yy < y + 19; yy++)
            for (int xx = x; xx < x + 12 - (yy - y) / 2; xx++)
            {
                int o = (yy * W + xx) * 4;
                px[o] = px[o + 1] = px[o + 2] = 0;
            }
        return new RawImage(img.Width, img.Height, px);
    }
}
