using Scrimshaw.Core.Annotation;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.TestSupport;

namespace Scrimshaw.Core.Tests;

/// <summary>FR-C12: the music area, stored as fractions of the source.</summary>
public class CaptureRegionTests
{
    [Fact]
    public void Pixels_round_trip_through_fractions()
    {
        CaptureRegion region = CaptureRegion.FromPixels(new PixelRect(192, 108, 960, 540), 1920, 1080)!;
        Assert.Equal(new CaptureRegion { X = 0.1, Y = 0.1, W = 0.5, H = 0.5 }, region);
        Assert.Equal(new PixelRect(192, 108, 960, 540), region.ToPixels(1920, 1080));
        Assert.Equal(new PixelRect(128, 72, 640, 360), region.ToPixels(1280, 720)); // still fits after a resize
    }

    [Fact]
    public void A_drag_past_the_edges_is_clamped_to_the_source()
    {
        CaptureRegion region = CaptureRegion.FromPixels(new PixelRect(-50, 540, 3000, 900), 1920, 1080)!;
        Assert.Equal(new PixelRect(0, 540, 1920, 540), region.ToPixels(1920, 1080));
    }

    [Theory]
    [InlineData(10, 10, 20, 500)]  // narrower than 2%
    [InlineData(10, 10, 500, 10)]  // shorter than 2%
    [InlineData(2000, 0, 100, 100)] // entirely outside
    public void Tiny_or_outside_areas_are_rejected(int x, int y, int w, int h) =>
        Assert.Null(CaptureRegion.FromPixels(new PixelRect(x, y, w, h), 1920, 1080));

    [Fact]
    public void Pixels_are_at_least_one_and_inside_the_frame()
    {
        var region = new CaptureRegion { X = 0.99, Y = 0.99, W = 0.01, H = 0.01 };
        PixelRect r = region.ToPixels(10, 10);
        Assert.True(r.W >= 1 && r.H >= 1 && r.Right <= 10 && r.Bottom <= 10);
    }

    [Fact]
    public void Parses_what_it_writes()
    {
        var region = new CaptureRegion { X = 0, Y = 0.125, W = 1, H = 0.8 };
        Assert.Equal("0,0.125,1,0.8", region.ToString());
        Assert.Equal(region, CaptureRegion.Parse(region.ToString()));
        Assert.Throws<FormatException>(() => CaptureRegion.Parse("0,0,1"));
        Assert.Throws<FormatException>(() => CaptureRegion.Parse("0.5,0,0.8,1")); // runs off the right edge
    }

    [Fact]
    public void Crop_copies_the_pixels_inside()
    {
        RawImage page = Synthetic.Page(1);
        RawImage crop = ImageOps.Crop(page, new PixelRect(10, 20, 30, 40));
        Assert.Equal((30, 40), (crop.Width, crop.Height));
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 30; x++)
                Assert.Equal(page.Bgra[(20 + y) * page.Stride + (10 + x) * 4], crop.Bgra[y * crop.Stride + x * 4]);
        Assert.Same(page, ImageOps.Crop(page, new PixelRect(0, 0, page.Width, page.Height)));
    }

    [Fact]
    public void The_area_and_save_option_are_kept_with_the_workspace()
    {
        using var dir = new TempDir();
        var region = new CaptureRegion { X = 0, Y = 0, W = 1, H = 0.8 };
        using (Workspace ws = Workspace.Create(dir.Path))
            ws.UpdateSettings(s => s with { Region = region, SaveRegionOnly = true });

        using Workspace reopened = Workspace.Open(dir.Path, new FakeCodec());
        Assert.Equal(region, reopened.Settings.Region);
        Assert.True(reopened.Settings.SaveRegionOnly);
    }

    [Fact]
    public void Settings_without_an_area_watch_the_whole_source()
    {
        var s = new WorkspaceSettings();
        Assert.Null(s.Region);
        Assert.False(s.SaveRegionOnly);
    }

    [Fact]
    public void An_unusable_area_is_rejected()
    {
        using var dir = new TempDir();
        using Workspace ws = Workspace.Create(dir.Path);
        Assert.Throws<ArgumentException>(() => ws.UpdateSettings(s => s with { Region = new CaptureRegion { X = 0, Y = 0, W = 0.01, H = 1 } }));
        Assert.Null(ws.Settings.Region);
    }
}
