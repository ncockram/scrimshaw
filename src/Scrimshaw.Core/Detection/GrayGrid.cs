using Scrimshaw.Core.Imaging;

namespace Scrimshaw.Core.Detection;

/// <summary>
/// A coarse grayscale summary of an image: <see cref="DefaultColumns"/> columns of cells, with the row count
/// chosen to keep the aspect ratio. Each cell holds the mean brightness (0–255) of the pixels it covers.
/// </summary>
public sealed class GrayGrid
{
    public const int DefaultColumns = 160;

    public int Columns { get; }
    public int Rows { get; }

    /// <summary>Row-major cell brightness values.</summary>
    public float[] Cells { get; }

    /// <summary>Size of the image the grid was built from.</summary>
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    private GrayGrid(int columns, int rows, float[] cells, int sourceWidth, int sourceHeight)
    {
        Columns = columns;
        Rows = rows;
        Cells = cells;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    public static GrayGrid FromImage(RawImage image, int columns = DefaultColumns)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);

        int w = image.Width, h = image.Height;
        int cols = Math.Min(columns, w);
        int rows = Math.Clamp((int)Math.Round((double)cols * h / w), 1, h);

        // Sum luminance per cell in one pass over the pixels.
        var sums = new long[cols * rows];
        var counts = new int[cols * rows];
        var colOfX = new int[w];
        for (int x = 0; x < w; x++) colOfX[x] = (int)((long)x * cols / w);

        byte[] px = image.Bgra;
        for (int y = 0; y < h; y++)
        {
            int rowBase = (int)((long)y * rows / h) * cols;
            int offset = y * image.Stride;
            for (int x = 0; x < w; x++, offset += 4)
            {
                // Integer BT.601 luma: (77 R + 150 G + 29 B) / 256
                int luma = (px[offset + 2] * 77 + px[offset + 1] * 150 + px[offset] * 29) >> 8;
                int cell = rowBase + colOfX[x];
                sums[cell] += luma;
                counts[cell]++;
            }
        }

        var cells = new float[cols * rows];
        for (int i = 0; i < cells.Length; i++) cells[i] = (float)sums[i] / counts[i];
        return new GrayGrid(cols, rows, cells, w, h);
    }

    public bool SameShapeAs(GrayGrid other) =>
        Columns == other.Columns && Rows == other.Rows &&
        SourceWidth == other.SourceWidth && SourceHeight == other.SourceHeight;
}
