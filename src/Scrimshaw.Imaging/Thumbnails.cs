using System.IO;
using System.Windows.Media.Imaging;

namespace Scrimshaw.Imaging;

/// <summary>
/// Small JPEG copies of frames for the thumbnail strip (FR-A1), cached in the workspace's <c>thumbs/</c>
/// folder. A thumbnail older than its frame (the frame was replaced, FR-D8) is regenerated.
/// </summary>
public static class Thumbnails
{
    public const int Width = 320;

    public static string PathFor(string thumbsFolder, string framePath) =>
        Path.Combine(thumbsFolder, Path.GetFileNameWithoutExtension(framePath) + ".jpg");

    /// <returns>The thumbnail's path, creating or refreshing it if needed.</returns>
    public static string GetOrCreate(string framePath, string thumbsFolder)
    {
        string thumb = PathFor(thumbsFolder, framePath);
        if (File.Exists(thumb) && File.GetLastWriteTimeUtc(thumb) >= File.GetLastWriteTimeUtc(framePath)) return thumb;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.DecodePixelWidth = Width;
        image.UriSource = new Uri(Path.GetFullPath(framePath));
        image.EndInit();
        image.Freeze();

        Directory.CreateDirectory(thumbsFolder);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(image));
        string temp = thumb + ".tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            encoder.Save(fs);
        File.Move(temp, thumb, overwrite: true);
        return thumb;
    }
}
