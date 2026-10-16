namespace Scrimshaw.Core.Imaging;

/// <summary>
/// Uncompressed 32-bit BGRA pixels, top row first. The common currency between capture, change detection,
/// encoding and export. Alpha is ignored.
/// </summary>
public sealed class RawImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Bytes per row; at least <c>Width * 4</c>.</summary>
    public int Stride { get; }

    public byte[] Bgra { get; }

    public RawImage(int width, int height, int stride, byte[] bgra)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length < (long)stride * height)
            throw new ArgumentException($"Buffer holds {bgra.Length} bytes; {stride * height} required.", nameof(bgra));

        Width = width;
        Height = height;
        Stride = stride;
        Bgra = bgra;
    }

    /// <summary>Creates a tightly packed image (<c>Stride == Width * 4</c>).</summary>
    public RawImage(int width, int height, byte[] bgra) : this(width, height, width * 4, bgra) { }
}
