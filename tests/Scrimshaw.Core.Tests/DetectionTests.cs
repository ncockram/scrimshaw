using Scrimshaw.Core.Detection;
using Scrimshaw.Core.Imaging;
using Scrimshaw.TestSupport;

namespace Scrimshaw.Core.Tests;

public class GrayGridTests
{
    [Fact]
    public void Keeps_aspect_ratio()
    {
        GrayGrid g = GrayGrid.FromImage(Synthetic.Solid(1920, 1080, 128));
        Assert.Equal(160, g.Columns);
        Assert.Equal(90, g.Rows);
        Assert.All(g.Cells, c => Assert.Equal(128f, c, 0.01f));
    }

    [Fact]
    public void Small_images_use_one_cell_per_pixel_at_most()
    {
        GrayGrid g = GrayGrid.FromImage(Synthetic.Solid(40, 10, 0));
        Assert.Equal(40, g.Columns);
        Assert.Equal(10, g.Rows);
    }

    [Fact]
    public void Ignores_stride_padding()
    {
        // 2x1 image with 4 bytes of junk padding per row.
        var px = new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 9, 9, 9, 9 };
        GrayGrid g = GrayGrid.FromImage(new RawImage(2, 1, 12, px));
        Assert.All(g.Cells, c => Assert.Equal(254f, c, 1f));
    }

    [Fact]
    public void Uses_luma_weights()
    {
        var red = new RawImage(1, 1, [0, 0, 255, 255]);
        var green = new RawImage(1, 1, [0, 255, 0, 255]);
        Assert.True(GrayGrid.FromImage(green).Cells[0] > GrayGrid.FromImage(red).Cells[0]);
    }
}

public class AreaChangeTests
{
    [Fact]
    public void Identical_images_have_no_change()
    {
        GrayGrid a = GrayGrid.FromImage(Synthetic.Page(1));
        Assert.Equal(0, AreaChange.ChangedFraction(a, GrayGrid.FromImage(Synthetic.Page(1)), 24));
    }

    [Fact]
    public void Different_sizes_count_as_fully_changed()
    {
        GrayGrid a = GrayGrid.FromImage(Synthetic.Page(1));
        GrayGrid b = GrayGrid.FromImage(Synthetic.Page(1, 640, 360));
        Assert.Equal(1, AreaChange.ChangedFraction(a, b, 24));
    }

    [Fact]
    public void Black_to_white_is_fully_changed()
    {
        GrayGrid a = GrayGrid.FromImage(Synthetic.Solid(160, 90, 0));
        GrayGrid b = GrayGrid.FromImage(Synthetic.Solid(160, 90, 255));
        Assert.Equal(1, AreaChange.ChangedFraction(a, b, 24));
    }

    [Fact]
    public void Tolerance_absorbs_small_brightness_shifts()
    {
        GrayGrid a = GrayGrid.FromImage(Synthetic.Solid(160, 90, 100));
        GrayGrid b = GrayGrid.FromImage(Synthetic.Solid(160, 90, 120));
        Assert.Equal(0, AreaChange.ChangedFraction(a, b, 24));
        Assert.Equal(1, AreaChange.ChangedFraction(a, b, 8));
    }

    [Fact]
    public void A_new_page_is_a_large_change()
    {
        double f = AreaChange.ChangedFraction(GrayGrid.FromImage(Synthetic.Page(1)), GrayGrid.FromImage(Synthetic.Page(2)), 24);
        Assert.InRange(f, 0.05, 1);
    }
}

public class ChangeDetectorTests
{
    private static readonly ChangeDetectionSettings Defaults = new();

    private static List<DetectionDecision> Feed(ChangeDetector d, params RawImage[] frames) =>
        frames.Select(f => d.Process(f).Decision).ToList();

    [Fact]
    public void First_page_is_saved_once_it_settles()
    {
        var d = new ChangeDetector(Defaults);
        RawImage p1 = Synthetic.Page(1);
        Assert.Equal([DetectionDecision.Pending, DetectionDecision.Save, DetectionDecision.Discard, DetectionDecision.Discard],
            Feed(d, p1, p1, p1, p1));
    }

    [Fact]
    public void Cut_page_turn_saves_the_new_page()
    {
        var d = new ChangeDetector(Defaults);
        RawImage p1 = Synthetic.Page(1), p2 = Synthetic.Page(2);
        Feed(d, p1, p1);
        DetectionResult first = d.Process(p2);
        DetectionResult second = d.Process(p2);
        Assert.Equal(DetectionDecision.Pending, first.Decision);
        Assert.Equal(DetectionDecision.Save, second.Decision);
        Assert.Same(p2, second.Image);
        Assert.True(second.ChangedFraction >= Defaults.AreaThreshold);
    }

    [Fact]
    public void Cross_fade_saves_only_the_clean_page()
    {
        var d = new ChangeDetector(Defaults);
        RawImage p1 = Synthetic.Page(1), p2 = Synthetic.Page(2);
        Feed(d, p1, p1);

        var saved = new List<RawImage>();
        foreach (RawImage f in new[] { Synthetic.Blend(p1, p2, 0.3), Synthetic.Blend(p1, p2, 0.6), Synthetic.Blend(p1, p2, 0.9), p2, p2, p2 })
        {
            DetectionResult r = d.Process(f);
            if (r.Decision == DetectionDecision.Save) saved.Add(r.Image!);
        }

        RawImage only = Assert.Single(saved);
        Assert.Same(p2, only);
    }

    [Fact]
    public void Faint_ghost_at_the_end_of_a_fade_does_not_count_as_settled()
    {
        // The 0.9 frame differs from the clean page by less than PixelTolerance, but more than SettleTolerance.
        var d = new ChangeDetector(Defaults);
        RawImage p1 = Synthetic.Page(1), p2 = Synthetic.Page(2);
        Feed(d, p1, p1);
        Assert.Equal(DetectionDecision.Pending, d.Process(Synthetic.Blend(p1, p2, 0.9)).Decision);
        Assert.Equal(DetectionDecision.Pending, d.Process(p2).Decision);
        Assert.Same(p2, d.Process(p2).Image);
    }

    [Fact]
    public void Half_page_turn_is_kept()
    {
        var d = new ChangeDetector(Defaults);
        RawImage p1 = Synthetic.Page(1), half = Synthetic.HalfTurn(p1, Synthetic.Page(2));
        Feed(d, p1, p1);
        Assert.Equal([DetectionDecision.Pending, DetectionDecision.Save], Feed(d, half, half));
    }

    [Fact]
    public void Cursor_blip_is_discarded()
    {
        var d = new ChangeDetector(Defaults);
        RawImage p1 = Synthetic.Page(1);
        Feed(d, p1, p1);
        Assert.Equal([DetectionDecision.Discard, DetectionDecision.Discard], Feed(d, Synthetic.WithCursor(p1, 100, 50), p1));
        Assert.False(d.IsPending);
    }

    [Fact]
    public void Compression_noise_is_discarded()
    {
        var d = new ChangeDetector(Defaults);
        RawImage p1 = Synthetic.Page(1);
        Feed(d, p1, p1);
        for (int seed = 0; seed < 5; seed++)
            Assert.Equal(DetectionDecision.Discard, d.Process(Synthetic.Noisy(p1, 6, seed)).Decision);
    }

    [Fact]
    public void Pending_change_that_reverts_is_dropped()
    {
        var d = new ChangeDetector(Defaults);
        RawImage p1 = Synthetic.Page(1), p2 = Synthetic.Page(2);
        Feed(d, p1, p1);
        Assert.Equal([DetectionDecision.Pending, DetectionDecision.Discard, DetectionDecision.Discard], Feed(d, p2, p1, p1));
    }

    [Fact]
    public void Settling_can_be_turned_off()
    {
        var d = new ChangeDetector(Defaults with { SettleEnabled = false });
        RawImage p1 = Synthetic.Page(1), p2 = Synthetic.Page(2);
        Assert.Equal([DetectionDecision.Save, DetectionDecision.Discard, DetectionDecision.Save], Feed(d, p1, p1, p2));
    }

    [Fact]
    public void Settle_count_sets_how_many_matching_captures_are_needed()
    {
        var d = new ChangeDetector(Defaults with { SettleCount = 2 });
        RawImage p1 = Synthetic.Page(1);
        Assert.Equal([DetectionDecision.Pending, DetectionDecision.Pending, DetectionDecision.Save], Feed(d, p1, p1, p1));
    }

    [Fact]
    public void Seeded_detector_resumes_from_the_last_saved_frame()
    {
        var d = new ChangeDetector(Defaults);
        RawImage p1 = Synthetic.Page(1);
        d.Seed(GrayGrid.FromImage(p1));
        Assert.Equal(DetectionDecision.Discard, d.Process(p1).Decision);
    }

    [Fact]
    public void Resized_capture_counts_as_a_change()
    {
        var d = new ChangeDetector(Defaults);
        Feed(d, Synthetic.Page(1), Synthetic.Page(1));
        Assert.Equal(DetectionDecision.Pending, d.Process(Synthetic.Page(1, 640, 360)).Decision);
    }

    [Fact]
    public void Higher_threshold_ignores_a_half_page_change()
    {
        var d = new ChangeDetector(Defaults with { AreaThreshold = 0.95 });
        RawImage p1 = Synthetic.Page(1), half = Synthetic.HalfTurn(p1, Synthetic.Page(2));
        Feed(d, p1, p1);
        Assert.Equal([DetectionDecision.Discard, DetectionDecision.Discard], Feed(d, half, half));
    }
}

public class DHashTests
{
    [Fact]
    public void Same_image_same_hash()
    {
        Assert.Equal(DHash.Compute(GrayGrid.FromImage(Synthetic.Page(3))), DHash.Compute(GrayGrid.FromImage(Synthetic.Page(3))));
    }

    [Fact]
    public void Different_pages_are_far_apart()
    {
        ulong a = DHash.Compute(GrayGrid.FromImage(Synthetic.Page(1)));
        ulong b = DHash.Compute(GrayGrid.FromImage(Synthetic.Page(2)));
        Assert.True(DHash.Distance(a, b) > 5);
    }

    [Fact]
    public void Noise_barely_moves_the_hash()
    {
        RawImage p = Synthetic.Page(4);
        ulong a = DHash.Compute(GrayGrid.FromImage(p));
        ulong b = DHash.Compute(GrayGrid.FromImage(Synthetic.Noisy(p, 4, 1)));
        Assert.True(DHash.Distance(a, b) <= 4);
    }

    [Fact]
    public void Hex_round_trips()
    {
        const ulong value = 0xa3f09c1b7e22d410;
        Assert.Equal("a3f09c1b7e22d410", DHash.ToHex(value));
        Assert.Equal(value, DHash.FromHex("a3f09c1b7e22d410"));
    }
}

public class SyntheticTests
{
    [Fact]
    public void Synthetic_turns_change_about_as_much_as_real_ones()
    {
        // Real V3 page turns changed ~20% of the window (Phase 0, F15); keep the fakes in that region.
        GrayGrid p1 = GrayGrid.FromImage(Synthetic.Page(1));
        double full = AreaChange.ChangedFraction(p1, GrayGrid.FromImage(Synthetic.Page(2)), 24);
        double half = AreaChange.ChangedFraction(p1, GrayGrid.FromImage(Synthetic.HalfTurn(Synthetic.Page(1), Synthetic.Page(2))), 24);
        Assert.InRange(full, 0.10, 0.50);
        Assert.InRange(half, 0.05, full);
    }
}
