namespace Scrimshaw.Core.Imaging;

public enum ImageFormat
{
    Png,
    Jpeg,
}

/// <summary>Reads and writes frame images. Implemented outside Core (WIC), faked in tests.</summary>
public interface IImageCodec
{
    RawImage Decode(string path);

    /// <param name="jpegQuality">1–100; ignored for PNG.</param>
    void Encode(RawImage image, string path, ImageFormat format, int jpegQuality);
}

public static class ImageFormatExtensions
{
    public static string FileExtension(this ImageFormat format) => format switch
    {
        ImageFormat.Png => ".png",
        ImageFormat.Jpeg => ".jpg",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };
}
