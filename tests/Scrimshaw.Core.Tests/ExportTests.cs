using Scrimshaw.Core.Export;
using Scrimshaw.Core.Workspaces;

namespace Scrimshaw.Core.Tests;

public class ExportPlannerTests
{
    private static FrameRecord Frame(int seq, bool deleted = false, params BoxRecord[] boxes) => new()
    {
        Seq = seq, File = $"images/frame_{seq:D5}.png", CapturedAt = DateTimeOffset.UnixEpoch,
        Width = 1000, Height = 800, DHash = "0", Deleted = deleted, Boxes = boxes,
    };

    private static BoxRecord Box(string id, int order, int x = 0, int y = 0, int w = 100, int h = 100) =>
        new() { Id = id, Order = order, X = x, Y = y, W = w, H = h };

    [Fact]
    public void Orders_by_frame_then_box_order_and_skips_deleted_frames()
    {
        var frames = new[]
        {
            Frame(3, false, Box("c", 0)),
            Frame(1, false, Box("b", 1), Box("a", 0)),
            Frame(2, true, Box("deleted", 0)),
        };

        IReadOnlyList<ExportItem> plan = ExportPlanner.Plan(frames);

        Assert.Equal(["a", "b", "c"], plan.Select(i => i.Box.Id));
        Assert.Equal([1, 1, 3], plan.Select(i => i.Frame.Seq));
    }

    [Fact]
    public void Clips_boxes_to_the_frame_and_drops_empty_ones()
    {
        var frames = new[]
        {
            Frame(1, false,
                Box("overhang", 0, x: 900, y: 700, w: 300, h: 300),
                Box("outside", 1, x: 2000, y: 0),
                Box("empty", 2, w: 0)),
        };

        ExportItem only = Assert.Single(ExportPlanner.Plan(frames));
        Assert.Equal(("overhang", 900, 700, 100, 100), (only.Box.Id, only.Box.X, only.Box.Y, only.Box.W, only.Box.H));
    }

    [Fact]
    public void Nothing_to_export_gives_an_empty_plan()
    {
        Assert.Empty(ExportPlanner.Plan([Frame(1)]));
    }
}

public class StackedLayoutEngineTests
{
    private const double Margin = 36, Gap = 18;
    private static readonly ExportOptions Letter = new();
    private const double ContentW = 612 - 2 * Margin; // 540
    private const double ContentH = 792 - 2 * Margin; // 720

    [Fact]
    public void Shared_scale_fits_the_widest_crop_and_keeps_staves_equal()
    {
        PageLayout layout = StackedLayoutEngine.Layout([(1400, 380), (1200, 380)], Letter);

        double s = ContentW / 1400;
        Assert.All(layout.Placements, p => Assert.Equal(s, p.Scale, 9));
        Assert.Equal(ContentW, layout.Placements[0].Width, 9);
        Assert.Equal(1200 * s, layout.Placements[1].Width, 9);
        Assert.Equal(layout.Placements[0].Height, layout.Placements[1].Height, 9);
    }

    [Fact]
    public void Crops_are_left_aligned_and_stacked_with_gaps()
    {
        PageLayout layout = StackedLayoutEngine.Layout([(1400, 380), (1200, 380)], Letter);
        Placement a = layout.Placements[0], b = layout.Placements[1];

        Assert.All(layout.Placements, p => Assert.Equal(Margin, p.X));
        Assert.Equal(Margin, a.Y);
        Assert.Equal(a.Y + a.Height + Gap, b.Y, 9);
        Assert.Equal(1, layout.PageCount);
    }

    [Fact]
    public void Starts_a_new_page_when_the_next_crop_does_not_fit()
    {
        // Each crop is 540 x 270 pt at shared scale; two fit (270 + 18 + 270 = 558), the third doesn't.
        PageLayout layout = StackedLayoutEngine.Layout([(1000, 500), (1000, 500), (1000, 500)], Letter);

        Assert.Equal([0, 0, 1], layout.Placements.Select(p => p.Page));
        Assert.Equal(Margin, layout.Placements[2].Y);
        Assert.Equal(2, layout.PageCount);
    }

    [Fact]
    public void No_crop_crosses_the_bottom_margin()
    {
        var crops = Enumerable.Range(0, 40).Select(i => (1000 + i * 13, 150 + i * 37 % 400)).ToList();
        PageLayout layout = StackedLayoutEngine.Layout(crops, Letter);

        Assert.All(layout.Placements, p =>
        {
            Assert.True(p.Y >= Margin - 1e-9);
            Assert.True(p.Y + p.Height <= Margin + ContentH + 1e-6, $"crop {p.Index} ends at {p.Y + p.Height}");
            Assert.True(p.X + p.Width <= Margin + ContentW + 1e-6);
        });
        Assert.Equal(Enumerable.Range(0, 40), layout.Placements.Select(p => p.Index));
    }

    [Fact]
    public void Crop_taller_than_a_page_is_shrunk_on_its_own()
    {
        PageLayout layout = StackedLayoutEngine.Layout([(1000, 300), (1000, 3000)], Letter);
        Placement normal = layout.Placements[0], tall = layout.Placements[1];

        Assert.Equal(ContentW / 1000, normal.Scale, 9);
        Assert.Equal(ContentH, tall.Height, 9);
        Assert.True(tall.Scale < normal.Scale);
        Assert.Equal(1, tall.Page);
    }

    [Fact]
    public void Small_crops_are_not_enlarged_past_the_limit()
    {
        PageLayout layout = StackedLayoutEngine.Layout([(100, 50)], Letter);
        double cap = 2.0 * 72 / 150;
        Assert.Equal(cap, layout.Placements[0].Scale, 9);
        Assert.Equal(96, layout.Placements[0].Width, 9);
    }

    [Fact]
    public void Fit_width_scales_each_crop_separately()
    {
        PageLayout layout = StackedLayoutEngine.Layout([(1400, 380), (1200, 380)], Letter with { Scaling = ExportScaling.FitWidth });
        Assert.All(layout.Placements, p => Assert.Equal(ContentW, p.Width, 9));
        Assert.NotEqual(layout.Placements[0].Scale, layout.Placements[1].Scale);
    }

    [Fact]
    public void One_per_page_puts_each_crop_on_its_own_page()
    {
        PageLayout layout = StackedLayoutEngine.Layout([(100, 50), (100, 50), (100, 50)], Letter with { Layout = ExportLayout.OnePerPage });
        Assert.Equal([0, 1, 2], layout.Placements.Select(p => p.Page));
        Assert.All(layout.Placements, p => Assert.Equal(Margin, p.Y));
    }

    [Fact]
    public void Captions_reserve_space_below_each_crop()
    {
        ExportOptions withCaptions = Letter with { Captions = true };
        PageLayout layout = StackedLayoutEngine.Layout([(1000, 300), (1000, 300)], withCaptions);
        Placement a = layout.Placements[0], b = layout.Placements[1];

        Assert.Equal(a.Y + a.Height, a.CaptionTop);
        Assert.Equal(a.Y + a.Height + withCaptions.CaptionHeightPt + Gap, b.Y, 9);
        Assert.Null(StackedLayoutEngine.Layout([(1000, 300)], Letter).Placements[0].CaptionTop);
    }

    [Theory]
    [InlineData(PaperSize.Letter, PageOrientation.Portrait, 612, 792)]
    [InlineData(PaperSize.Letter, PageOrientation.Landscape, 792, 612)]
    [InlineData(PaperSize.A4, PageOrientation.Portrait, 595.2756, 841.8898)]
    public void Page_sizes(PaperSize size, PageOrientation orientation, double w, double h)
    {
        PageLayout layout = StackedLayoutEngine.Layout([(10, 10)], Letter with { PaperSize = size, Orientation = orientation });
        Assert.Equal(w, layout.PageWidth, 3);
        Assert.Equal(h, layout.PageHeight, 3);
    }

    [Fact]
    public void Empty_input_gives_no_pages()
    {
        Assert.Equal(0, StackedLayoutEngine.Layout([], Letter).PageCount);
    }

    [Fact]
    public void Bad_options_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => StackedLayoutEngine.Layout([(10, 10)], Letter with { MarginPt = 280 }));
        Assert.Throws<ArgumentException>(() => StackedLayoutEngine.Layout([(10, 10)], Letter with { MaxUpscale = 0 }));
        Assert.Throws<ArgumentException>(() => StackedLayoutEngine.Layout([(0, 10)], Letter));
    }
}
