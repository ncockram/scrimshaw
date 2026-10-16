using Scrimshaw.Core.Imaging;

namespace Scrimshaw.Core.Tests;

/// <summary>Generated "score page" frames for detection tests: staff lines plus noteheads, seeded per page.</summary>
internal static class Synthetic
{
    public const int Width = 320;
    public const int Height = 180;

    public static RawImage Page(int id, int width = Width, int height = Height)
    {
        var px = new byte[width * height * 4];
        Fill(px, width, 0, 0, width, height, 255);
        // Density roughly matches real engraved music: a full page turn changes ~20% of the image (Phase 0, F15).
        var rng = new Random(id);
        int systemHeight = height / 3;
        for (int system = 0; system < 3; system++)
        {
            int top = 12 + system * systemHeight;
            for (int line = 0; line < 5; line++)
                Fill(px, width, 8, top + line * 5, width - 16, 1, 0);
            int notes = (width - 24) / 8;
            for (int n = 0; n < notes; n++)
            {
                int x = 12 + n * 8, y = top - 4 + rng.Next(0, 26);
                Fill(px, width, x, y, 6, 5, 0);                         // notehead
                bool up = rng.Next(2) == 0;
                Fill(px, width, up ? x + 5 : x, up ? y - 14 : y + 3, 1, 14, 0); // stem
                if (rng.Next(3) == 0) Fill(px, width, x, up ? y - 14 : y + 16, 9, 2, 0); // beam
            }
        }
        return new RawImage(width, height, px);
    }

    /// <summary>Cross-fade: <paramref name="t"/> = 0 is <paramref name="from"/>, 1 is <paramref name="to"/>.</summary>
    public static RawImage Blend(RawImage from, RawImage to, double t)
    {
        var px = new byte[from.Bgra.Length];
        for (int i = 0; i < px.Length; i++)
            px[i] = (byte)Math.Round(from.Bgra[i] * (1 - t) + to.Bgra[i] * t);
        return new RawImage(from.Width, from.Height, px);
    }

    /// <summary>Top half from <paramref name="to"/>, bottom half still <paramref name="from"/>.</summary>
    public static RawImage HalfTurn(RawImage from, RawImage to)
    {
        var px = (byte[])from.Bgra.Clone();
        Array.Copy(to.Bgra, px, from.Stride * (from.Height / 2));
        return new RawImage(from.Width, from.Height, px);
    }

    /// <summary>Adds ±<paramref name="amplitude"/> random noise, like video compression shimmer.</summary>
    public static RawImage Noisy(RawImage image, int amplitude, int seed)
    {
        var rng = new Random(seed);
        var px = (byte[])image.Bgra.Clone();
        for (int i = 0; i < px.Length; i++)
            px[i] = (byte)Math.Clamp(px[i] + rng.Next(-amplitude, amplitude + 1), 0, 255);
        return new RawImage(image.Width, image.Height, px);
    }

    /// <summary>A small dark rectangle, like a mouse pointer.</summary>
    public static RawImage WithCursor(RawImage image, int x, int y)
    {
        var px = (byte[])image.Bgra.Clone();
        Fill(px, image.Width, x, y, 8, 12, 0);
        return new RawImage(image.Width, image.Height, px);
    }

    public static RawImage Solid(int width, int height, byte value)
    {
        var px = new byte[width * height * 4];
        Fill(px, width, 0, 0, width, height, value);
        return new RawImage(width, height, px);
    }

    private static void Fill(byte[] px, int width, int x, int y, int w, int h, byte value)
    {
        int height = px.Length / 4 / width;
        for (int yy = Math.Max(0, y); yy < Math.Min(height, y + h); yy++)
            for (int xx = Math.Max(0, x); xx < Math.Min(width, x + w); xx++)
            {
                int o = (yy * width + xx) * 4;
                px[o] = px[o + 1] = px[o + 2] = value;
                px[o + 3] = 255;
            }
    }
}
