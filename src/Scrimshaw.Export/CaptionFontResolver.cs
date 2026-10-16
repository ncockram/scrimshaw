using PdfSharp.Fonts;

namespace Scrimshaw.Export;

/// <summary>
/// Supplies the caption font from the Windows Fonts folder. PDFsharp's own Windows lookup only knows a few
/// families, so Segoe UI (with Arial as a fallback) is resolved here instead.
/// </summary>
internal sealed class CaptionFontResolver : IFontResolver
{
    public const string Family = "Segoe UI";

    private static readonly string Fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? "caption-bold" : "caption-regular");

    public byte[]? GetFont(string faceName)
    {
        string[] candidates = faceName == "caption-bold" ? ["segoeuib.ttf", "arialbd.ttf"] : ["segoeui.ttf", "arial.ttf"];
        foreach (string file in candidates)
        {
            string path = Path.Combine(Fonts, file);
            if (File.Exists(path)) return File.ReadAllBytes(path);
        }
        return null;
    }
}
