using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Scrimshaw.Core.Export;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Core.Workspaces;

namespace Scrimshaw.Export;

/// <param name="Done">Crops placed so far.</param>
public readonly record struct ExportProgress(int Done, int Total);

/// <param name="Path">The PDF written.</param>
public sealed record ExportResult(string Path, int Pages, int Crops, int Bookmarks);

/// <summary>Thrown when there's nothing to export (FR-P9).</summary>
public sealed class NothingToExportException : Exception
{
    public NothingToExportException() : base("There are no boxes to export. Draw boxes on the frames in the Review tab first.") { }
    public NothingToExportException(string message) : base(message) { }
    public NothingToExportException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Builds the PDF (FR-P1–P10): every box of every frame that isn't deleted, cropped in memory from its
/// full-resolution frame, laid out by <see cref="StackedLayoutEngine"/>, with optional captions and a bookmark
/// for each labelled box. Safe to run on a background thread.
/// </summary>
public static class PdfExporter
{
    private const double CaptionFontSize = 8.5;

    static PdfExporter()
    {
        // PDFsharp's core build has no system font access of its own; captions use Segoe UI from Windows.
        GlobalFontSettings.FontResolver ??= new CaptionFontResolver();
    }

    /// <summary>The file name for an export made now (FR-P8): <c>scrimshaw_2026-10-16_1530.pdf</c>, numbered if taken.</summary>
    public static string NewExportPath(Workspace workspace, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string stem = string.Create(CultureInfo.InvariantCulture, $"scrimshaw_{now:yyyy-MM-dd_HHmm}");
        string path = Path.Combine(workspace.ExportsPath, stem + ".pdf");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(workspace.ExportsPath, string.Create(CultureInfo.InvariantCulture, $"{stem}_{n}.pdf"));
        return path;
    }

    /// <summary>The layout an export would produce, for previews and page counts, without reading any images.</summary>
    public static (IReadOnlyList<ExportItem> Items, PageLayout Layout) Plan(IEnumerable<FrameRecord> frames, ExportOptions options)
    {
        IReadOnlyList<ExportItem> items = ExportPlanner.Plan(frames);
        PageLayout layout = StackedLayoutEngine.Layout(items.Select(i => (i.Box.W, i.Box.H)).ToList(), options);
        return (items, layout);
    }

    /// <exception cref="NothingToExportException">No frame that isn't deleted has a box.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; no file is left behind.</exception>
    public static ExportResult Export(
        Workspace workspace, ExportOptions options, string outputPath,
        IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        (IReadOnlyList<ExportItem> items, PageLayout layout) = Plan(workspace.Manifest.Current.Frames, options);
        if (items.Count == 0) throw new NothingToExportException();

        using var document = new PdfDocument();
        document.Info.Title = Path.GetFileName(workspace.Root.TrimEnd(Path.DirectorySeparatorChar));
        document.Info.Creator = "Scrimshaw";
        var pages = new List<PdfPage>();
        var graphics = new List<XGraphics>();
        XFont? captionFont = options.Captions ? new XFont(CaptionFontResolver.Family, CaptionFontSize, XFontStyleEx.Regular) : null;
        bool jpeg = workspace.Settings.ImageFormat == ImageFormat.Jpeg;
        int bookmarks = 0, done = 0;

        try
        {
            for (int p = 0; p < layout.PageCount; p++)
            {
                PdfPage page = document.AddPage();
                page.Width = XUnit.FromPoint(layout.PageWidth);
                page.Height = XUnit.FromPoint(layout.PageHeight);
                pages.Add(page);
                graphics.Add(XGraphics.FromPdfPage(page));
            }

            // Decode each frame once, however many boxes it has.
            foreach (var group in items.Select((item, index) => (item, index)).GroupBy(x => x.item.Frame.Seq))
            {
                cancellationToken.ThrowIfCancellationRequested();
                BitmapSource frame = Decode(workspace.FullPath(group.First().item.Frame));
                foreach (var (item, index) in group)
                {
                    Placement place = layout.Placements[index];
                    using (var crop = new MemoryStream(Crop(frame, item.Box, jpeg, workspace.Settings.JpegQuality)))
                    using (XImage image = XImage.FromStream(crop))
                        graphics[place.Page].DrawImage(image, place.X, place.Y, place.Width, place.Height);

                    if (captionFont is not null && place.CaptionTop is double top)
                        graphics[place.Page].DrawString(Caption(item), captionFont, XBrushes.DimGray,
                            new XRect(place.X, top + 2, layout.PageWidth - 2 * options.MarginPt, options.CaptionHeightPt), XStringFormats.TopLeft);

                    if (item.Box.Label is { Length: > 0 } label)
                    {
                        PdfOutline outline = document.Outlines.Add(label, pages[place.Page], true);
                        outline.PageDestinationType = PdfPageDestinationType.Xyz;
                        outline.Left = 0;
                        outline.Top = layout.PageHeight - place.Y; // PDF outline coordinates start at the bottom (plan F4)
                        bookmarks++;
                    }
                    progress?.Report(new ExportProgress(++done, items.Count));
                }
            }
        }
        finally
        {
            foreach (XGraphics g in graphics) g.Dispose();
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        string temp = outputPath + ".tmp";
        try
        {
            document.Save(temp);
            File.Move(temp, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return new ExportResult(outputPath, layout.PageCount, items.Count, bookmarks);
    }

    /// <summary>The caption under a crop (FR-P5): frame number, capture time and label.</summary>
    public static string Caption(ExportItem item)
    {
        string time = item.Frame.CapturedAt.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
        string text = string.Create(CultureInfo.CurrentCulture, $"Frame {item.Frame.Seq} · {time}");
        return item.Box.Label is { Length: > 0 } label ? $"{text} · {label}" : text;
    }

    private static BitmapFrame Decode(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        BitmapFrame frame = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>The box's pixels as PNG (lossless), or JPEG for JPEG workspaces so the file doesn't balloon.</summary>
    private static byte[] Crop(BitmapSource frame, BoxRecord box, bool jpeg, int quality)
    {
        var cropped = new CroppedBitmap(frame, new Int32Rect(box.X, box.Y, box.W, box.H));
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropped));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
