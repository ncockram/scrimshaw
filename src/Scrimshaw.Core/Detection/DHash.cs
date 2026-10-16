using System.Globalization;
using System.Numerics;

namespace Scrimshaw.Core.Detection;

/// <summary>
/// 64-bit difference hash (FR-D4): shrink to 9×8, set a bit where a cell is brighter than its right-hand
/// neighbour. Stored per frame as a fingerprint and for near-duplicate hints; it does not drive saving.
/// A small dead-zone keeps near-ties in flat areas (blank paper) from flipping on noise.
/// </summary>
public static class DHash
{
    private const float DeadZone = 1.0f;

    public static ulong Compute(GrayGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        float[] small = Resample(grid, 9, 8);
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++, bit++)
                if (small[y * 9 + x] > small[y * 9 + x + 1] + DeadZone)
                    hash |= 1UL << bit;
        return hash;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    public static string ToHex(ulong hash) => hash.ToString("x16", CultureInfo.InvariantCulture);

    public static ulong FromHex(string hex) => ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>Area-average resample of the grid to <paramref name="cols"/> × <paramref name="rows"/>.</summary>
    private static float[] Resample(GrayGrid grid, int cols, int rows)
    {
        var result = new float[cols * rows];
        for (int ty = 0; ty < rows; ty++)
        {
            double y0 = (double)ty * grid.Rows / rows, y1 = (double)(ty + 1) * grid.Rows / rows;
            for (int tx = 0; tx < cols; tx++)
            {
                double x0 = (double)tx * grid.Columns / cols, x1 = (double)(tx + 1) * grid.Columns / cols;
                double sum = 0, area = 0;
                for (int sy = (int)y0; sy < Math.Min(grid.Rows, (int)Math.Ceiling(y1)); sy++)
                {
                    double hy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
                    for (int sx = (int)x0; sx < Math.Min(grid.Columns, (int)Math.Ceiling(x1)); sx++)
                    {
                        double wgt = hy * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));
                        sum += grid.Cells[sy * grid.Columns + sx] * wgt;
                        area += wgt;
                    }
                }
                result[ty * cols + tx] = (float)(sum / area);
            }
        }
        return result;
    }
}
