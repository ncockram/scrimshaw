using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scrimshaw.App.Services;
using Scrimshaw.Core.Annotation;
using Scrimshaw.Core.Export;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.Export;
using Scrimshaw.Imaging;

namespace Scrimshaw.App.ViewModels;

/// <summary>One crop on a preview page, in points from the page's top-left.</summary>
public sealed record PreviewCrop(Rect Rect, string ImagePath, PixelRect Crop, int FrameWidth, int FrameHeight, string? Caption, string? Bookmark);

/// <summary>One page of the layout preview.</summary>
public sealed record PreviewPage(int Number, double Width, double Height, IReadOnlyList<PreviewCrop> Crops);

/// <summary>The Export tab (FR-P1–P11): options, a live layout preview, and the export itself.</summary>
public sealed partial class ExportViewModel : ObservableObject, IDisposable
{
    private readonly Workspace _workspace;
    private readonly IUiDispatcher _ui;
    private readonly IShellService _shell;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _export;
    private bool _loading;

    public ExportViewModel(Workspace workspace, IUiDispatcher ui, IShellService shell, IDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _ui = ui;
        _shell = shell;
        _dialogs = dialogs;
        Load(workspace.Settings.Export);
        workspace.Manifest.Changed += OnManifestChanged;
        Refresh();
    }

    public ObservableCollection<PreviewPage> Pages { get; } = [];

    [ObservableProperty] public partial bool IsStacked { get; set; }
    [ObservableProperty] public partial bool IsSharedScale { get; set; }
    [ObservableProperty] public partial bool IsA4 { get; set; }
    [ObservableProperty] public partial bool IsLandscape { get; set; }
    [ObservableProperty] public partial bool Captions { get; set; }
    [ObservableProperty] public partial string MarginText { get; set; } = "";
    [ObservableProperty] public partial string GapText { get; set; } = "";
    [ObservableProperty] public partial double MaxUpscale { get; set; }

    [ObservableProperty] public partial string? OptionsError { get; private set; }
    [ObservableProperty] public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial int CropCount { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(CancelCommand))]
    public partial bool IsExporting { get; private set; }

    [ObservableProperty] public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenPdfCommand), nameof(OpenFolderCommand))]
    public partial string? LastExportPath { get; private set; }

    [ObservableProperty] public partial string? Result { get; private set; }

    /// <summary>The unit margins and gaps are typed in: millimetres for A4, inches for Letter.</summary>
    public string Unit => IsA4 ? "mm" : "in";

    /// <summary>Builds the options from the form, or explains what's wrong.</summary>
    public bool TryGetOptions(out ExportOptions options)
    {
        options = _workspace.Settings.Export;
        if (!TryParseLength(MarginText, out double margin) || !TryParseLength(GapText, out double gap))
        {
            OptionsError = $"Enter margins and gaps as numbers in {Unit}.";
            return false;
        }
        var candidate = options with
        {
            Layout = IsStacked ? ExportLayout.Stacked : ExportLayout.OnePerPage,
            Scaling = IsSharedScale ? ExportScaling.Shared : ExportScaling.FitWidth,
            PaperSize = IsA4 ? PaperSize.A4 : PaperSize.Letter,
            Orientation = IsLandscape ? PageOrientation.Landscape : PageOrientation.Portrait,
            Captions = Captions,
            MarginPt = margin,
            GapPt = gap,
            MaxUpscale = Math.Clamp(MaxUpscale, 0.5, 4),
        };
        try
        {
            candidate.Validate();
        }
        catch (ArgumentException ex)
        {
            OptionsError = ex.Message;
            return false;
        }
        OptionsError = null;
        options = candidate;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (!TryGetOptions(out ExportOptions options)) return;
        try
        {
            _workspace.UpdateSettings(s => s with { Export = options });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Saving the options is a convenience; the export itself can still go ahead.
        }

        string path = PdfExporter.NewExportPath(_workspace, DateTime.Now);
        _export = new CancellationTokenSource();
        CancellationToken token = _export.Token;
        IsExporting = true;
        ProgressPercent = 0;
        Result = "Exporting…";
        var progress = new Progress<ExportProgress>(p => ProgressPercent = 100.0 * p.Done / Math.Max(1, p.Total));
        try
        {
            ExportResult r = await Task.Run(() => PdfExporter.Export(_workspace, options, path, progress, token), token);
            LastExportPath = r.Path;
            Result = string.Create(CultureInfo.CurrentCulture,
                $"Saved {r.Pages} pages ({r.Crops} crops, {r.Bookmarks} bookmarks) to {Path.GetFileName(r.Path)}.");
        }
        catch (OperationCanceledException)
        {
            Result = "Export cancelled. Nothing was saved.";
        }
        catch (NothingToExportException ex)
        {
            Result = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Result = "The PDF couldn't be saved.";
            _dialogs.ShowError("Export failed", ex.Message);
        }
        finally
        {
            IsExporting = false;
            _export.Dispose();
            _export = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void Cancel() => _export?.Cancel();

    [RelayCommand(CanExecute = nameof(HasExport))]
    private void OpenPdf() => _shell.Open(LastExportPath!);

    [RelayCommand(CanExecute = nameof(HasExport))]
    private void OpenFolder() => _shell.ShowInFolder(LastExportPath!);

    public void Dispose()
    {
        _workspace.Manifest.Changed -= OnManifestChanged;
        _export?.Cancel();
    }

    private bool CanExport() => !IsExporting && CropCount > 0;
    private bool HasExport() => LastExportPath is not null;

    partial void OnIsStackedChanged(bool value) => Refresh();
    partial void OnIsSharedScaleChanged(bool value) => Refresh();
    partial void OnIsLandscapeChanged(bool value) => Refresh();
    partial void OnCaptionsChanged(bool value) => Refresh();
    partial void OnMarginTextChanged(string value) => Refresh();
    partial void OnGapTextChanged(string value) => Refresh();
    partial void OnMaxUpscaleChanged(double value) => Refresh();

    partial void OnIsA4Changed(bool value)
    {
        if (_loading) return;
        // Keep the same physical lengths, shown in the new unit.
        _loading = true;
        MarginText = FormatLength(ParseIn(MarginText, value ? "in" : "mm"), value);
        GapText = FormatLength(ParseIn(GapText, value ? "in" : "mm"), value);
        _loading = false;
        OnPropertyChanged(nameof(Unit));
        Refresh();
    }

    private void OnManifestChanged(object? sender, EventArgs e) => _ui.Post(Refresh);

    private void Load(ExportOptions o)
    {
        _loading = true;
        IsStacked = o.Layout == ExportLayout.Stacked;
        IsSharedScale = o.Scaling == ExportScaling.Shared;
        IsA4 = o.PaperSize == PaperSize.A4;
        IsLandscape = o.Orientation == PageOrientation.Landscape;
        Captions = o.Captions;
        MaxUpscale = o.MaxUpscale;
        MarginText = FormatLength(o.MarginPt, IsA4);
        GapText = FormatLength(o.GapPt, IsA4);
        _loading = false;
    }

    /// <summary>Recomputes the summary and preview from the manifest and the form.</summary>
    private void Refresh()
    {
        if (_loading) return;
        if (!TryGetOptions(out ExportOptions options))
        {
            Pages.Clear();
            Summary = "";
            return;
        }
        (IReadOnlyList<ExportItem> items, PageLayout layout) = PdfExporter.Plan(_workspace.Manifest.Current.Frames, options);
        CropCount = items.Count;
        int frames = items.Select(i => i.Frame.Seq).Distinct().Count();
        int bookmarks = items.Count(i => !string.IsNullOrEmpty(i.Box.Label));
        Summary = items.Count == 0
            ? "No boxes yet. Draw boxes around the music in the Review tab."
            : string.Create(CultureInfo.CurrentCulture,
                $"{items.Count} crops from {frames} frames → {layout.PageCount} pages. {bookmarks} labelled boxes become bookmarks.");

        Pages.Clear();
        foreach (var page in layout.Placements.GroupBy(p => p.Page))
        {
            var crops = page.Select(p =>
            {
                ExportItem item = items[p.Index];
                string framePath = _workspace.FullPath(item.Frame);
                string thumb = Thumbnails.PathFor(_workspace.ThumbsPath, framePath);
                return new PreviewCrop(
                    new Rect(p.X, p.Y, p.Width, p.Height),
                    File.Exists(thumb) ? thumb : framePath,
                    new PixelRect(item.Box.X, item.Box.Y, item.Box.W, item.Box.H),
                    item.Frame.Width,
                    item.Frame.Height,
                    options.Captions ? PdfExporter.Caption(item) : null,
                    item.Box.Label);
            }).ToList();
            Pages.Add(new PreviewPage(page.Key + 1, layout.PageWidth, layout.PageHeight, crops));
        }
    }

    private bool TryParseLength(string text, out double points)
    {
        points = ParseIn(text, Unit);
        return !double.IsNaN(points) && points >= 0;
    }

    /// <returns>Points, or NaN if <paramref name="text"/> isn't a number.</returns>
    private static double ParseIn(string text, string unit)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            return double.NaN;
        return unit == "mm" ? v * 72 / 25.4 : v * 72;
    }

    private static string FormatLength(double points, bool mm) => double.IsNaN(points)
        ? ""
        : mm ? (points * 25.4 / 72).ToString("0.#", CultureInfo.CurrentCulture) : (points / 72).ToString("0.##", CultureInfo.CurrentCulture);
}
