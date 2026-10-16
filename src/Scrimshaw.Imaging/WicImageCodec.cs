using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scrimshaw.Core.Imaging;

namespace Scrimshaw.Imaging;

/// <summary>PNG/JPEG via Windows Imaging Component (WPF's imaging classes). Safe to use from any thread.</summary>
public sealed class WicImageCodec : IImageCodec
{
    public RawImage Decode(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        BitmapFrame frame = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        return ToRawImage(frame);
    }

    public void Encode(RawImage image, string path, ImageFormat format, int jpegQuality)
    {
        ArgumentNullException.ThrowIfNull(image);
        BitmapEncoder encoder = format switch
        {
            ImageFormat.Png => new PngBitmapEncoder(),
            ImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
        encoder.Frames.Add(BitmapFrame.Create(ToBitmapSource(image)));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(fs);
    }

    /// <summary>Wraps raw pixels as a frozen 96-DPI WPF bitmap (alpha ignored).</summary>
    public static BitmapSource ToBitmapSource(RawImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, image.Bgra, image.Stride);
        source.Freeze();
        return source;
    }

    public static RawImage ToRawImage(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        BitmapSource bgra = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Bgr32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);
        return new RawImage(w, h, stride, pixels);
    }
}
