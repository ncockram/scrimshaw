using System.Globalization;
using Scrimshaw.Core.Annotation;

namespace Scrimshaw.Core.Imaging;

/// <summary>
/// The music area (FR-C12): a rectangle stored as fractions (0–1) of the source's width and height, so it still
/// fits after a window is resized or the resolution changes. Change detection looks only inside it.
/// </summary>
public sealed record CaptureRegion
{
    /// <summary>Areas smaller than this fraction of the source (either side) are rejected as accidental.</summary>
    public const double MinFraction = 0.02;

    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }

    public bool IsValid =>
        W >= MinFraction && H >= MinFraction && X >= 0 && Y >= 0 && X + W <= 1.0000001 && Y + H <= 1.0000001;

    /// <summary>The area in pixels of a <paramref name="width"/> × <paramref name="height"/> frame, at least 1 × 1 and inside it.</summary>
    public PixelRect ToPixels(int width, int height)
    {
        int left = Math.Clamp((int)Math.Round(X * width), 0, width - 1);
        int top = Math.Clamp((int)Math.Round(Y * height), 0, height - 1);
        int right = Math.Clamp((int)Math.Round((X + W) * width), left + 1, width);
        int bottom = Math.Clamp((int)Math.Round((Y + H) * height), top + 1, height);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// The region for a rectangle in a frame's pixels, clamped to the frame; null if too small to be meant
    /// (see <see cref="MinFraction"/>).
    /// </summary>
    public static CaptureRegion? FromPixels(PixelRect rect, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        double left = Math.Clamp(rect.X, 0, width), top = Math.Clamp(rect.Y, 0, height);
        double right = Math.Clamp(rect.Right, 0, width), bottom = Math.Clamp(rect.Bottom, 0, height);
        var region = new CaptureRegion { X = left / width, Y = top / height, W = (right - left) / width, H = (bottom - top) / height };
        return region.IsValid ? region : null;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X:0.###},{Y:0.###},{W:0.###},{H:0.###}");

    /// <summary>Parses "x,y,w,h" fractions (as <see cref="ToString"/> writes them).</summary>
    public static CaptureRegion Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        double[] v = text.Split(',').Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (v.Length != 4) throw new FormatException("Expected x,y,w,h as fractions, e.g. 0,0,1,0.8");
        var region = new CaptureRegion { X = v[0], Y = v[1], W = v[2], H = v[3] };
        return region.IsValid ? region : throw new FormatException($"'{text}' isn't a usable area.");
    }
}
