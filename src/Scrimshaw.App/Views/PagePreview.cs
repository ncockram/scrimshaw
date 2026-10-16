using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scrimshaw.App.ViewModels;

namespace Scrimshaw.App.Views;

/// <summary>
/// Draws one page of the export layout: the page, its margins and each crop, cut from the frame's thumbnail.
/// A quick, faithful preview of what <c>PdfExporter</c> will produce, without building the PDF.
/// </summary>
public sealed class PagePreview : FrameworkElement
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PreviewPage), typeof(PagePreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Preview width in screen units; the height follows the page's shape.</summary>
    public static readonly DependencyProperty PreviewWidthProperty = DependencyProperty.Register(
        nameof(PreviewWidth), typeof(double), typeof(PagePreview),
        new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Pen PageEdge = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)), 1));
    private static readonly Pen MarginGuide = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x1E, 0x6F, 0xD9)), 1) { DashStyle = DashStyles.Dot });
    private static readonly Brush Missing = Freeze(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)));
    private static readonly Brush BookmarkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)));

    public PreviewPage? Page
    {
        get => (PreviewPage?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public double PreviewWidth
    {
        get => (double)GetValue(PreviewWidthProperty);
        set => SetValue(PreviewWidthProperty, value);
    }

    /// <summary>Forget cached thumbnails (e.g. after frames were replaced).</summary>
    public static void ClearCache() => Cache.Clear();

    protected override Size MeasureOverride(Size availableSize) =>
        Page is { } p ? new Size(PreviewWidth, PreviewWidth * p.Height / p.Width) : new Size(0, 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (Page is not { } page) return;
        DrawingContext dc = drawingContext;
        double s = PreviewWidth / page.Width;
        var pageRect = new Rect(0, 0, page.Width * s, page.Height * s);
        dc.DrawRectangle(Brushes.White, PageEdge, pageRect);

        foreach (PreviewCrop crop in page.Crops)
        {
            var target = new Rect(crop.Rect.X * s, crop.Rect.Y * s, crop.Rect.Width * s, crop.Rect.Height * s);
            BitmapSource? image = Load(crop.ImagePath);
            if (image is null)
            {
                dc.DrawRectangle(Missing, null, target);
            }
            else
            {
                // Draw the whole frame scaled so the cropped region lands exactly on the target, clipped to it.
                double scale = target.Width / crop.Crop.W;
                var frameRect = new Rect(target.X - crop.Crop.X * scale, target.Y - crop.Crop.Y * scale, crop.FrameWidth * scale, crop.FrameHeight * scale);
                dc.PushClip(new RectangleGeometry(target));
                dc.DrawImage(image, frameRect);
                dc.Pop();
            }
            if (crop.Bookmark is not null)
                dc.DrawRectangle(BookmarkBrush, null, new Rect(target.X - 5, target.Y, 3, Math.Min(12, target.Height)));
            if (crop.Caption is not null)
                dc.DrawText(Text(crop.Caption, 8.5 * s), new Point(target.X, target.Bottom + 2 * s));
        }

        dc.DrawText(Text(page.Number.ToString(CultureInfo.CurrentCulture), 9), new Point(pageRect.Right - 14, pageRect.Bottom - 14));
    }

    private FormattedText Text(string text, double size) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), Math.Max(size, 5), Brushes.DimGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static BitmapSource? Load(string path) => Cache.GetOrAdd(path, p =>
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 640;
            image.UriSource = new Uri(p);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    });

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
