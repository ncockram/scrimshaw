namespace Scrimshaw.Core.Imaging;

/// <summary>Small pixel operations on <see cref="RawImage"/>.</summary>
public static class ImageOps
{
    /// <summary>
    /// Shrinks the image by a whole-number factor (box filter) until it's at most <paramref name="maxWidth"/> wide.
    /// Returns the same instance when it's already small enough. Used for the source preview (FR-C13).
    /// </summary>
    public static RawImage Downscale(RawImage image, int maxWidth)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        if (image.Width <= maxWidth) return image;

        int f = (image.Width + maxWidth - 1) / maxWidth;
        int w = Math.Max(1, image.Width / f), h = Math.Max(1, image.Height / f), stride = w * 4;
        var dst = new byte[stride * h];
        byte[] src = image.Bgra;
        for (int y = 0; y < h; y++)
        {
            // Blocks at the edges can be cut short when the size isn't a multiple of f (or smaller than it).
            int rows = Math.Min(f, image.Height - y * f);
            for (int x = 0; x < w; x++)
            {
                int cols = Math.Min(f, image.Width - x * f);
                int b = 0, g = 0, r = 0;
                for (int dy = 0; dy < rows; dy++)
                {
                    int o = (y * f + dy) * image.Stride + x * f * 4;
                    for (int dx = 0; dx < cols; dx++, o += 4)
                    {
                        b += src[o];
                        g += src[o + 1];
                        r += src[o + 2];
                    }
                }
                int area = rows * cols, d = y * stride + x * 4;
                dst[d] = (byte)(b / area);
                dst[d + 1] = (byte)(g / area);
                dst[d + 2] = (byte)(r / area);
                dst[d + 3] = 255;
            }
        }
        return new RawImage(w, h, stride, dst);
    }
}
