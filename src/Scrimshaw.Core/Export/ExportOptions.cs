namespace Scrimshaw.Core.Export;

public enum ExportLayout
{
    /// <summary>Crops flow top to bottom, several per page (FR-P4 default).</summary>
    Stacked,
    OnePerPage,
}

public enum ExportScaling
{
    /// <summary>One scale for every crop so staves stay the same size (FR-P7 default).</summary>
    Shared,
    /// <summary>Each crop fills the content width on its own.</summary>
    FitWidth,
}

public enum PaperSize
{
    Letter,
    A4,
}

public enum PageOrientation
{
    Portrait,
    Landscape,
}

/// <summary>PDF export settings (FR-P4–P7). Lengths are in PDF points (1/72 in).</summary>
public sealed record ExportOptions
{
    /// <summary>Print size that 1 source pixel maps to before scaling: 150 pixels per inch.</summary>
    public const double BasePointsPerPixel = 72.0 / 150.0;

    public ExportLayout Layout { get; init; } = ExportLayout.Stacked;
    public ExportScaling Scaling { get; init; } = ExportScaling.Shared;
    public PaperSize PaperSize { get; init; } = PaperSize.Letter;
    public PageOrientation Orientation { get; init; } = PageOrientation.Portrait;

    /// <summary>0.5 in.</summary>
    public double MarginPt { get; init; } = 36;

    /// <summary>Space between stacked crops: 0.25 in.</summary>
    public double GapPt { get; init; } = 18;

    /// <summary>Crops are never enlarged beyond this multiple of <see cref="BasePointsPerPixel"/>.</summary>
    public double MaxUpscale { get; init; } = 2.0;

    public bool Captions { get; init; }

    /// <summary>Space reserved under each crop for its caption when <see cref="Captions"/> is on.</summary>
    public double CaptionHeightPt { get; init; } = 14;

    /// <exception cref="ArgumentException">A value is out of range.</exception>
    public void Validate()
    {
        if (MarginPt < 0 || GapPt < 0 || CaptionHeightPt < 0)
            throw new ArgumentException("Margins, gaps and caption height can't be negative.");
        if (MaxUpscale <= 0)
            throw new ArgumentException("The enlargement limit must be above zero.");
        var (w, h) = PageSizePt();
        if (w - 2 * MarginPt < 72 || h - 2 * MarginPt < 72)
            throw new ArgumentException("The margins leave less than an inch of usable page.");
    }

    /// <summary>Page width and height in points, after orientation.</summary>
    public (double Width, double Height) PageSizePt()
    {
        (double w, double h) = PaperSize switch
        {
            PaperSize.Letter => (612.0, 792.0),
            PaperSize.A4 => (595.2756, 841.8898),
            _ => throw new InvalidOperationException($"Unknown paper size {PaperSize}."),
        };
        return Orientation == PageOrientation.Landscape ? (h, w) : (w, h);
    }
}
