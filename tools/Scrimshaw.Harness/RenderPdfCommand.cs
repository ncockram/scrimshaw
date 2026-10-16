using System.Globalization;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Scrimshaw.Harness;

/// <summary>Renders each PDF page to PNG with the built-in Windows PDF renderer, for visual review.</summary>
internal static class RenderPdfCommand
{
    public static async Task<int> RunAsync(CommandLine cl)
    {
        string pdf = Path.GetFullPath(cl.Arg(0, "file.pdf"));
        string outDir = Path.GetFullPath(cl.Positional.Count > 1 ? cl.Positional[1] : Path.ChangeExtension(pdf, null) + "_pages");
        double dpi = cl.Positional.Count > 2 ? double.Parse(cl.Positional[2], CultureInfo.InvariantCulture) : 100;
        Directory.CreateDirectory(outDir);

        StorageFile file = await StorageFile.GetFileFromPathAsync(pdf);
        PdfDocument doc = await PdfDocument.LoadFromFileAsync(file);
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using PdfPage page = doc.GetPage(i);
            var options = new PdfPageRenderOptions
            {
                // page.Size is in DIPs (1/96 in).
                DestinationWidth = (uint)Math.Round(page.Size.Width / 96.0 * dpi),
                DestinationHeight = (uint)Math.Round(page.Size.Height / 96.0 * dpi),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            };
            string outPath = Path.Combine(outDir, string.Create(CultureInfo.InvariantCulture, $"page_{i + 1:D3}.png"));
            using (var fs = File.Create(outPath))
            using (IRandomAccessStream ras = fs.AsRandomAccessStream())
                await page.RenderToStreamAsync(ras, options);
            Console.WriteLine(outPath);
        }
        return 0;
    }
}
