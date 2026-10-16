// Renders each page of a PDF to PNG using the built-in Windows.Data.Pdf renderer,
// so exported PDFs can be reviewed as images. Usage: PdfRender <file.pdf> [outDir] [dpi]
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

string pdf = Path.GetFullPath(args[0]);
string outDir = Path.GetFullPath(args.Length > 1 ? args[1] : Path.ChangeExtension(pdf, null) + "_pages");
double dpi = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 100;
Directory.CreateDirectory(outDir);

StorageFile file = await StorageFile.GetFileFromPathAsync(pdf);
PdfDocument doc = await PdfDocument.LoadFromFileAsync(file);
for (uint i = 0; i < doc.PageCount; i++)
{
    using PdfPage page = doc.GetPage(i);
    var options = new PdfPageRenderOptions
    {
        DestinationWidth = (uint)Math.Round(page.Size.Width / 96.0 * dpi),
        DestinationHeight = (uint)Math.Round(page.Size.Height / 96.0 * dpi),
        BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
    };
    string outPath = Path.Combine(outDir, $"page_{i + 1:D3}.png");
    using (var fs = File.Create(outPath))
    using (IRandomAccessStream ras = fs.AsRandomAccessStream())
        await page.RenderToStreamAsync(ras, options);
    Console.WriteLine(outPath);
}
