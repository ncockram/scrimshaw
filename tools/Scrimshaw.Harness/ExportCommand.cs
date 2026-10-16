using System.Diagnostics;
using Scrimshaw.Core.Export;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.Export;
using Scrimshaw.Imaging;

namespace Scrimshaw.Harness;

/// <summary>
/// Exports a workspace to PDF from the command line, with timing (NFR-4).
/// <code>export &lt;workspace&gt; [--out file.pdf] [--captions] [--one-per-page] [--fit-width] [--a4] [--landscape]</code>
/// </summary>
internal static class ExportCommand
{
    public static int Run(CommandLine cl)
    {
        using Workspace ws = Workspace.Open(cl.Arg(0, "workspace"), new WicImageCodec());
        ExportOptions options = ws.Settings.Export with
        {
            Captions = cl.Has("captions") || ws.Settings.Export.Captions,
            Layout = cl.Has("one-per-page") ? ExportLayout.OnePerPage : ws.Settings.Export.Layout,
            Scaling = cl.Has("fit-width") ? ExportScaling.FitWidth : ws.Settings.Export.Scaling,
            PaperSize = cl.Has("a4") ? PaperSize.A4 : ws.Settings.Export.PaperSize,
            Orientation = cl.Has("landscape") ? PageOrientation.Landscape : ws.Settings.Export.Orientation,
        };
        string output = cl.Get("out") is { } o ? Path.GetFullPath(o) : PdfExporter.NewExportPath(ws, DateTime.Now);
        var sw = Stopwatch.StartNew();
        ExportResult r = PdfExporter.Export(ws, options, output);
        Console.WriteLine($"{r.Crops} crops, {r.Pages} pages, {r.Bookmarks} bookmarks in {sw.Elapsed.TotalSeconds:0.0} s -> {r.Path}");
        return 0;
    }
}
