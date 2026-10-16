namespace Scrimshaw.Core.Export;

/// <summary>Where one crop goes. Coordinates are in points from the page's top-left corner.</summary>
/// <param name="Index">Position of the crop in the input list.</param>
/// <param name="Scale">Points per source pixel.</param>
/// <param name="CaptionTop">Top of the caption line, when captions are on.</param>
public sealed record Placement(int Index, int Page, double X, double Y, double Width, double Height, double Scale, double? CaptionTop);

public sealed record PageLayout(double PageWidth, double PageHeight, int PageCount, IReadOnlyList<Placement> Placements);

/// <summary>
/// Lays crops out on pages (FR-P4, FR-P6, FR-P7): left-aligned, top to bottom, never split across pages.
/// Pure arithmetic, so the PDF writer only has to draw what it's told.
/// </summary>
public static class StackedLayoutEngine
{
    /// <param name="crops">Crop sizes in source pixels, in export order.</param>
    public static PageLayout Layout(IReadOnlyList<(int Width, int Height)> crops, ExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(crops);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (crops.Any(c => c.Width <= 0 || c.Height <= 0))
            throw new ArgumentException("Crop sizes must be positive.", nameof(crops));

        var (pageW, pageH) = options.PageSizePt();
        double m = options.MarginPt;
        double contentW = pageW - 2 * m;
        double contentH = pageH - 2 * m;
        double caption = options.Captions ? options.CaptionHeightPt : 0;
        double maxScale = options.MaxUpscale * ExportOptions.BasePointsPerPixel;
        double imageMaxH = contentH - caption;

        double shared = crops.Count == 0 ? maxScale : Math.Min(contentW / crops.Max(c => c.Width), maxScale);

        var placements = new List<Placement>(crops.Count);
        int page = 0;
        double y = m;
        bool pageHasContent = false;

        for (int i = 0; i < crops.Count; i++)
        {
            var (cw, ch) = crops[i];
            double scale = options.Scaling == ExportScaling.Shared
                ? shared
                : Math.Min(contentW / cw, maxScale);
            // Any crop still too tall for a page is shrunk on its own; it is never split.
            scale = Math.Min(scale, imageMaxH / ch);

            double w = cw * scale, h = ch * scale;
            bool needsNewPage = pageHasContent &&
                (options.Layout == ExportLayout.OnePerPage || y + h + caption > m + contentH + 1e-6);
            if (needsNewPage)
            {
                page++;
                y = m;
            }

            placements.Add(new Placement(i, page, m, y, w, h, scale, options.Captions ? y + h : null));
            y += h + caption + options.GapPt;
            pageHasContent = true;
        }

        return new PageLayout(pageW, pageH, crops.Count == 0 ? 0 : page + 1, placements);
    }
}
