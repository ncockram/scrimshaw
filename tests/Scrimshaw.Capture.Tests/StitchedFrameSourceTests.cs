using Scrimshaw.Capture.Interop;
using Scrimshaw.Capture.Sources;
using Scrimshaw.Core.Imaging;
using Scrimshaw.TestSupport;

namespace Scrimshaw.Capture.Tests;

/// <summary>FR-C2(a): the whole desktop, stitched from per-monitor sources (headless, with fake sources).</summary>
public class StitchedFrameSourceTests
{
    // This development machine's layout (Phase 0, F8/F11): two monitors above and left of the main one.
    private static readonly RectInt32 Main = new(0, 0, 1920, 1080);
    private static readonly RectInt32 LeftTop = new(-1200, -1080, 720, 0);
    private static readonly RectInt32 RightTop = new(720, -1093, 2640, -13);

    private sealed class SolidSource(RawImage? image) : IFrameSource
    {
        public bool IsClosed { get; set; }
        public bool Disposed { get; private set; }
        public RawImage? Grab() => image;
        public void Dispose() => Disposed = true;
    }

    private static (byte B, byte G, byte R) Pixel(RawImage img, int x, int y)
    {
        int o = y * img.Stride + x * 4;
        return (img.Bgra[o], img.Bgra[o + 1], img.Bgra[o + 2]);
    }

    [Fact]
    public void Monitors_land_at_their_desktop_positions()
    {
        var parts = new (IFrameSource, RectInt32)[]
        {
            (new SolidSource(Synthetic.Solid(1920, 1080, 200)), Main),
            (new SolidSource(Synthetic.Solid(1920, 1080, 100)), LeftTop),
            (new SolidSource(Synthetic.Solid(1920, 1080, 50)), RightTop),
        };
        using var stitched = new StitchedFrameSource(parts);

        RawImage img = stitched.Grab()!;

        // Union: x -1200..2640, y -1093..1080 → 3840 × 2173 (as captured in Phase 0).
        Assert.Equal((3840, 2173), (img.Width, img.Height));
        Assert.Equal((200, 200, 200), Pixel(img, 0 + 1200, 0 + 1093));        // main's top-left
        Assert.Equal((100, 100, 100), Pixel(img, 0, 13));                     // left monitor's top-left
        Assert.Equal((50, 50, 50), Pixel(img, 720 + 1200, 0));                // right monitor's top-left
        Assert.Equal((0, 0, 0), Pixel(img, 0, 0));                            // gap above the left monitor stays black
    }

    [Fact]
    public void Nothing_until_every_monitor_has_a_frame()
    {
        using var stitched = new StitchedFrameSource([(new SolidSource(Synthetic.Solid(1920, 1080, 1)), Main), (new SolidSource(null), LeftTop)]);
        Assert.Null(stitched.Grab());
    }

    [Fact]
    public void Closed_if_any_monitor_goes_away_and_disposes_them_all()
    {
        var a = new SolidSource(Synthetic.Solid(10, 10, 1));
        var b = new SolidSource(Synthetic.Solid(10, 10, 1));
        var stitched = new StitchedFrameSource([(a, new RectInt32(0, 0, 10, 10)), (b, new RectInt32(10, 0, 20, 10))]);
        Assert.False(stitched.IsClosed);
        b.IsClosed = true;
        Assert.True(stitched.IsClosed);
        stitched.Dispose();
        Assert.True(a.Disposed && b.Disposed);
    }
}
