using Scrimshaw.Core.Export;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.Imaging;
using Scrimshaw.TestSupport;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Outline;

namespace Scrimshaw.Export.Tests;

/// <summary>Exports real workspaces and reads the PDFs back with PdfPig (golden checks, Phase 4).</summary>
public sealed class PdfExporterTests : IDisposable
{
    private const int FrameW = 1000, FrameH = 800;
    private readonly TempDir _dir = new();
    private readonly WicImageCodec _codec = new();
    private Workspace? _ws;

    public void Dispose()
    {
        _ws?.Dispose();
        _dir.Dispose();
    }

    /// <summary>A frame split into four coloured quadrants: red, green, blue, white (top-left, top-right, bottom-left, bottom-right).</summary>
    private static RawImage Quadrants()
    {
        var px = new byte[FrameW * FrameH * 4];
        for (int y = 0; y < FrameH; y++)
            for (int x = 0; x < FrameW; x++)
            {
                int o = (y * FrameW + x) * 4;
                (byte r, byte g, byte b) = (x < FrameW / 2, y < FrameH / 2) switch
                {
                    (true, true) => ((byte)220, (byte)20, (byte)20),
                    (false, true) => ((byte)20, (byte)200, (byte)20),
                    (true, false) => ((byte)20, (byte)20, (byte)220),
                    _ => ((byte)255, (byte)255, (byte)255),
                };
                px[o] = b;
                px[o + 1] = g;
                px[o + 2] = r;
                px[o + 3] = 255;
            }
        return new RawImage(FrameW, FrameH, px);
    }

    private Workspace NewWorkspace(ImageFormat format = ImageFormat.Png, int frames = 3)
    {
        _ws = Workspace.Create(_dir.Sub("ws"), new WorkspaceSettings { ImageFormat = format });
        RawImage image = Quadrants();
        for (int i = 0; i < frames; i++)
        {
            int seq = _ws.Manifest.ReserveSequence();
            var record = new FrameRecord
            {
                Seq = seq, File = _ws.RelativeFramePath(seq), CapturedAt = DateTimeOffset.UnixEpoch.AddHours(12),
                Width = FrameW, Height = FrameH, DHash = "0",
            };
            _codec.Encode(image, _ws.FullPath(record), format, 95);
            _ws.Manifest.AddFrame(record);
        }
        return _ws;
    }

    private static BoxRecord Box(string id, int order, int x, int y, int w, int h, string? label = null) =>
        new() { Id = id, Order = order, X = x, Y = y, W = w, H = h, Label = label };

    private static void SetBoxes(Workspace ws, int seq, params BoxRecord[] boxes) => ws.Manifest.UpdateFrame(seq, f => f with { Boxes = boxes });

    private static string Export(Workspace ws, ExportOptions? options = null) =>
        PdfExporter.Export(ws, options ?? new ExportOptions(), PdfExporter.NewExportPath(ws, DateTime.Now)).Path;

    private static List<(int Page, IPdfImage Image, double Top)> Images(PdfDocument pdf) =>
        pdf.GetPages().SelectMany(p => p.GetImages().Select(i => (p.Number, i, p.Height - i.BoundingBox.Top))).ToList();

    [Fact]
    public void One_box_sits_at_the_top_left_at_full_resolution()
    {
        Workspace ws = NewWorkspace(frames: 1);
        SetBoxes(ws, 1, Box("b1", 0, 0, 0, 500, 400));

        using PdfDocument pdf = PdfDocument.Open(Export(ws));
        var (page, image, top) = Assert.Single(Images(pdf));
        Assert.Equal(1, pdf.NumberOfPages);
        Assert.Equal((500, 400), (image.WidthInSamples, image.HeightInSamples)); // full-resolution crop
        Assert.Equal(36, image.BoundingBox.Left, 1);                            // left margin, left-aligned
        Assert.Equal(36, top, 1);
        Assert.Equal(500 * 0.96, image.BoundingBox.Width, 1);                   // capped at 2× the 150 px/in base size
    }

    [Fact]
    public void Crops_follow_frame_then_box_order_and_match_the_layout()
    {
        Workspace ws = NewWorkspace();
        SetBoxes(ws, 1, Box("b1", 1, 0, 0, 300, 300), Box("b2", 0, 0, 0, 310, 300)); // order 0 first
        SetBoxes(ws, 2, Box("b1", 0, 0, 0, 320, 300));
        SetBoxes(ws, 3, Box("b1", 0, 0, 0, 330, 300));

        var (_, layout) = PdfExporter.Plan(ws.Manifest.Current.Frames, new ExportOptions());
        using PdfDocument pdf = PdfDocument.Open(Export(ws));
        var images = Images(pdf);

        Assert.Equal([310, 300, 320, 330], images.Select(i => i.Image.WidthInSamples));
        Assert.Equal(layout.PageCount, pdf.NumberOfPages);
        for (int i = 0; i < images.Count; i++)
        {
            Placement p = layout.Placements[i];
            Assert.Equal(p.Page + 1, images[i].Page);
            Assert.Equal(p.X, images[i].Image.BoundingBox.Left, 1);
            Assert.Equal(p.Y, images[i].Top, 1);
            Assert.Equal(p.Width, images[i].Image.BoundingBox.Width, 1);
            Assert.Equal(p.Height, images[i].Image.BoundingBox.Height, 1);
        }
    }

    [Fact]
    public void Shared_scale_keeps_equal_heights_and_nothing_crosses_the_margins()
    {
        Workspace ws = NewWorkspace(frames: 3);
        for (int seq = 1; seq <= 3; seq++)
            SetBoxes(ws, seq, Box("b1", 0, 0, 0, 1000 - seq * 100, 250), Box("b2", 1, 0, 300, 1000 - seq * 50, 250));

        using PdfDocument pdf = PdfDocument.Open(Export(ws));
        var images = Images(pdf);
        Assert.Equal(6, images.Count);
        Assert.All(images, i => Assert.Equal(images[0].Image.BoundingBox.Height, i.Image.BoundingBox.Height, 1));
        foreach (Page page in pdf.GetPages())
            Assert.All(page.GetImages(), i =>
            {
                Assert.True(i.BoundingBox.Left >= 36 - 0.1 && i.BoundingBox.Right <= page.Width - 36 + 0.1);
                Assert.True(i.BoundingBox.Bottom >= 36 - 0.1 && i.BoundingBox.Top <= page.Height - 36 + 0.1);
            });
    }

    [Fact]
    public void Crop_contains_exactly_the_boxed_pixels()
    {
        Workspace ws = NewWorkspace(frames: 1);
        SetBoxes(ws, 1, Box("red", 0, 10, 10, 480, 380), Box("green", 1, 510, 10, 480, 380));

        using PdfDocument pdf = PdfDocument.Open(Export(ws));
        var images = Images(pdf);
        Assert.True(images[0].Image.TryGetPng(out byte[]? redPng));
        Assert.True(images[1].Image.TryGetPng(out byte[]? greenPng));
        AssertSolid(redPng!, 220, 20, 20);
        AssertSolid(greenPng!, 20, 200, 20);
    }

    [Fact]
    public void Deleted_frames_are_left_out()
    {
        Workspace ws = NewWorkspace();
        SetBoxes(ws, 1, Box("b1", 0, 0, 0, 300, 300));
        SetBoxes(ws, 2, Box("b1", 0, 0, 0, 444, 300));
        ws.Manifest.UpdateFrame(2, f => f with { Deleted = true });

        using PdfDocument pdf = PdfDocument.Open(Export(ws));
        Assert.DoesNotContain(Images(pdf), i => i.Image.WidthInSamples == 444);
    }

    [Fact]
    public void Labelled_boxes_become_bookmarks_pointing_at_their_crop()
    {
        Workspace ws = NewWorkspace();
        SetBoxes(ws, 1, Box("b1", 0, 0, 0, 1000, 600, "Mvt. I – Allegro"), Box("b2", 1, 0, 0, 1000, 600));
        SetBoxes(ws, 2, Box("b1", 0, 0, 0, 1000, 600, "Mvt. II"));
        var (_, layout) = PdfExporter.Plan(ws.Manifest.Current.Frames, new ExportOptions());

        string path = Export(ws);
        using PdfDocument pdf = PdfDocument.Open(path);
        Assert.True(pdf.TryGetBookmarks(out Bookmarks? bookmarks));
        var nodes = bookmarks!.GetNodes().OfType<DocumentBookmarkNode>().ToList();
        Assert.Equal(["Mvt. I – Allegro", "Mvt. II"], nodes.Select(n => n.Title));
        Placement first = layout.Placements[0], third = layout.Placements[2];
        Assert.Equal(first.Page + 1, nodes[0].PageNumber);
        Assert.Equal(third.Page + 1, nodes[1].PageNumber);
        Assert.Equal(layout.PageHeight - third.Y, nodes[1].Destination.Coordinates.Top!.Value, 1);
    }

    [Fact]
    public void Captions_show_frame_and_label_when_turned_on()
    {
        Workspace ws = NewWorkspace(frames: 1);
        SetBoxes(ws, 1, Box("b1", 0, 0, 0, 500, 300, "Coda"));

        using (PdfDocument pdf = PdfDocument.Open(Export(ws, new ExportOptions { Captions = true })))
        {
            string text = string.Join(" ", pdf.GetPage(1).GetWords().Select(w => w.Text));
            Assert.Contains("Frame", text, StringComparison.Ordinal);
            Assert.Contains("Coda", text, StringComparison.Ordinal);
        }
        using (PdfDocument pdf = PdfDocument.Open(Export(ws)))
            Assert.Empty(pdf.GetPage(1).GetWords());
    }

    [Fact]
    public void Jpeg_workspaces_embed_jpeg_crops()
    {
        Workspace ws = NewWorkspace(ImageFormat.Jpeg, frames: 1);
        SetBoxes(ws, 1, Box("b1", 0, 0, 0, 500, 400));

        using PdfDocument pdf = PdfDocument.Open(Export(ws));
        IPdfImage image = Assert.Single(Images(pdf)).Image;
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, image.RawMemory.Span[..2].ToArray()); // DCT (JPEG) stream
    }

    [Fact]
    public void Layout_options_are_applied()
    {
        Workspace ws = NewWorkspace();
        for (int seq = 1; seq <= 3; seq++) SetBoxes(ws, seq, Box("b1", 0, 0, 0, 200, 100));

        using (PdfDocument stacked = PdfDocument.Open(Export(ws)))
            Assert.Equal(1, stacked.NumberOfPages);
        using (PdfDocument onePer = PdfDocument.Open(Export(ws, new ExportOptions { Layout = ExportLayout.OnePerPage, PaperSize = PaperSize.A4 })))
        {
            Assert.Equal(3, onePer.NumberOfPages);
            Assert.Equal(595.3, onePer.GetPage(1).Width, 1);
        }
    }

    [Fact]
    public void Nothing_to_export_is_refused_without_writing_a_file()
    {
        Workspace ws = NewWorkspace();
        Assert.Throws<NothingToExportException>(() => Export(ws));
        Assert.Empty(Directory.EnumerateFiles(ws.ExportsPath));
    }

    [Fact]
    public void Cancelling_leaves_no_file()
    {
        Workspace ws = NewWorkspace();
        for (int seq = 1; seq <= 3; seq++) SetBoxes(ws, seq, Box("b1", 0, 0, 0, 200, 100));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            PdfExporter.Export(ws, new ExportOptions(), PdfExporter.NewExportPath(ws, DateTime.Now), null, cts.Token));
        Assert.Empty(Directory.EnumerateFiles(ws.ExportsPath));
    }

    [Fact]
    public void Progress_counts_every_crop()
    {
        Workspace ws = NewWorkspace();
        for (int seq = 1; seq <= 3; seq++) SetBoxes(ws, seq, Box("b1", 0, 0, 0, 200, 100), Box("b2", 1, 0, 200, 200, 100));
        var reports = new List<ExportProgress>();
        ExportResult result = PdfExporter.Export(ws, new ExportOptions(), PdfExporter.NewExportPath(ws, DateTime.Now), new SyncProgress(reports), TestContext.Current.CancellationToken);
        Assert.Equal(6, reports.Count);
        Assert.Equal(new ExportProgress(6, 6), reports[^1]);
        Assert.Equal((6, 0), (result.Crops, result.Bookmarks));
    }

    [Fact]
    public void Export_names_are_timestamped_and_never_overwrite()
    {
        Workspace ws = NewWorkspace(frames: 1);
        var now = new DateTime(2026, 10, 16, 15, 30, 0);
        string first = PdfExporter.NewExportPath(ws, now);
        Assert.Equal("scrimshaw_2026-10-16_1530.pdf", Path.GetFileName(first));
        File.WriteAllText(first, "x");
        Assert.Equal("scrimshaw_2026-10-16_1530_2.pdf", Path.GetFileName(PdfExporter.NewExportPath(ws, now)));
    }

    private void AssertSolid(byte[] png, byte r, byte g, byte b)
    {
        string path = _dir.Sub(Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, png);
        RawImage img = _codec.Decode(path);
        for (int i = 0; i < img.Width * img.Height; i += 97)
        {
            int o = i / img.Width * img.Stride + i % img.Width * 4;
            Assert.Equal((r, g, b), (img.Bgra[o + 2], img.Bgra[o + 1], img.Bgra[o]));
        }
    }

    /// <summary>Reports synchronously (Progress&lt;T&gt; posts to a context, which makes counting racy).</summary>
    private sealed class SyncProgress(List<ExportProgress> sink) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => sink.Add(value);
    }
}
