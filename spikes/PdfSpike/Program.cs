// Phase 0 spike: confirm PDFsharp 6 on .NET 10 can (a) embed PNG crops at exact positions and
// (b) write outline bookmarks that point at a page + vertical position. Results are read back with PdfPig.
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfDocument = PdfSharp.Pdf.PdfDocument;

namespace PdfSpike;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "out");
        Directory.CreateDirectory(outDir);

        // Three synthetic "score" frames of different sizes, then crop a box out of each in memory.
        var crops = new List<(byte[] Png, int W, int H, string Label)>
        {
            (Crop(RenderScorePage(1600, 900, 1), new Int32Rect(100, 50, 1400, 380)), 1400, 380, "Mvt. I"),
            (Crop(RenderScorePage(1600, 900, 2), new Int32Rect(100, 450, 1200, 380)), 1200, 380, ""),
            (Crop(RenderScorePage(1600, 900, 4), new Int32Rect(100, 20, 1400, 860)), 1400, 860, "Mvt. III"),
            (Crop(RenderScorePage(1600, 900, 3), new Int32Rect(100, 20, 1400, 860)), 1400, 860, "Mvt. II"),
        };
        File.WriteAllBytes(Path.Combine(outDir, "crop1.png"), crops[0].Png);

        // Stacked, left-aligned, shared scale: widest crop fits the content width.
        var doc = new PdfDocument();
        const double margin = 36, gap = 18; // points (0.5 in, 0.25 in)
        PdfPage page = NewPage(doc);
        double contentW = page.Width.Point - 2 * margin, contentH = page.Height.Point - 2 * margin;
        double scale = contentW / crops.Max(c => c.W);
        double y = margin;
        XGraphics gfx = XGraphics.FromPdfPage(page);
        var expected = new List<string>();

        foreach (var c in crops)
        {
            double w = c.W * scale, h = c.H * scale;
            if (y + h > margin + contentH)
            {
                gfx.Dispose();
                page = NewPage(doc);
                gfx = XGraphics.FromPdfPage(page);
                y = margin;
            }
            using (var ms = new MemoryStream(c.Png))
            using (var img = XImage.FromStream(ms))
                gfx.DrawImage(img, margin, y, w, h);

            int pageNo = doc.Pages.Count;
            expected.Add($"page {pageNo}: x={margin:F1} top={y:F1} w={w:F1} h={h:F1} label='{c.Label}'");
            if (c.Label.Length > 0)
            {
                PdfOutline o = doc.Outlines.Add(c.Label, page, true);
                o.PageDestinationType = PdfPageDestinationType.Xyz;
                o.Left = 0;
                o.Top = page.Height.Point - y; // PDF outline coordinates are bottom-left origin; PDFsharp does not convert
            }
            y += h + gap;
        }
        gfx.Dispose();

        string pdfPath = Path.Combine(outDir, "spike.pdf");
        doc.Save(pdfPath);

        Console.WriteLine($"PDFsharp {typeof(PdfDocument).Assembly.GetName().Version} wrote {pdfPath}");
        Console.WriteLine($"shared scale = {scale:F4} pt/px");
        Console.WriteLine("Expected placements (top-left origin):");
        expected.ForEach(e => Console.WriteLine("  " + e));

        // Read back with PdfPig (bottom-left origin).
        using var pig = PdfDocument_Open(pdfPath);
        Console.WriteLine($"PdfPig: {pig.NumberOfPages} pages");
        foreach (var p in pig.GetPages())
            foreach (var im in p.GetImages())
            {
                var b = im.BoundingBox;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  page {p.Number} ({p.Height:F0}pt tall): image x={b.Left:F1} top={p.Height - b.Top:F1} w={b.Width:F1} h={b.Height:F1} px={im.WidthInSamples}x{im.HeightInSamples}"));
            }
        if (pig.TryGetBookmarks(out var bookmarks))
            foreach (var node in bookmarks.GetNodes())
            {
                var dest = node is UglyToad.PdfPig.Outline.DocumentBookmarkNode d ? d.Destination : null;
                string coords = dest is null ? "-" : $"{dest.Type} left={dest.Coordinates.Left} top={dest.Coordinates.Top}";
                Console.WriteLine($"  bookmark '{node.Title}' -> page {(node as UglyToad.PdfPig.Outline.DocumentBookmarkNode)?.PageNumber} [{coords}]");
            }
        else
            Console.WriteLine("  no bookmarks found");
        return 0;
    }

    private static UglyToad.PdfPig.PdfDocument PdfDocument_Open(string path) => UglyToad.PdfPig.PdfDocument.Open(path);

    private static PdfPage NewPage(PdfDocument doc)
    {
        PdfPage p = doc.AddPage();
        p.Size = PageSize.Letter;
        return p;
    }

    /// <summary>Draws a fake score page: staves, noteheads and a big page number.</summary>
    private static RenderTargetBitmap RenderScorePage(int w, int h, int pageNo)
    {
        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            var pen = new Pen(Brushes.Black, 2);
            var rng = new Random(pageNo);
            for (int system = 0; system < 4; system++)
            {
                double top = 80 + system * 200;
                for (int line = 0; line < 5; line++)
                    dc.DrawLine(pen, new Point(120, top + line * 16), new Point(w - 120, top + line * 16));
                for (int n = 0; n < 24; n++)
                    dc.DrawEllipse(Brushes.Black, null, new Point(160 + n * 54, top + rng.Next(0, 9) * 8), 9, 7);
            }
            var text = new FormattedText($"Page {pageNo}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 40, Brushes.Black, 1.0);
            dc.DrawText(text, new Point(w / 2.0 - 70, h - 60));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static byte[] Crop(BitmapSource src, Int32Rect r)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(new CroppedBitmap(src, r)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
