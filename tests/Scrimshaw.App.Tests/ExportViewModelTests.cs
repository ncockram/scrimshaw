using Microsoft.Extensions.Time.Testing;
using Scrimshaw.App.Services;
using Scrimshaw.App.ViewModels;
using Scrimshaw.Core.Export;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.Imaging;
using Scrimshaw.TestSupport;

namespace Scrimshaw.App.Tests;

internal sealed class FakeShell : IShellService
{
    public List<string> Opened { get; } = [];
    public List<string> Shown { get; } = [];

    public void Open(string path) => Opened.Add(path);

    public void ShowInFolder(string path) => Shown.Add(path);
}

public sealed class ExportViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Workspace _ws;
    private readonly QueuedUi _ui = new();
    private readonly FakeShell _shell = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly ExportViewModel _vm;

    public ExportViewModelTests()
    {
        _ws = Workspace.Create(_dir.Path, time: new FakeTimeProvider());
        var codec = new WicImageCodec();
        for (int seq = 1; seq <= 3; seq++)
        {
            var record = new FrameRecord
            {
                Seq = seq, File = _ws.RelativeFramePath(seq), CapturedAt = DateTimeOffset.UnixEpoch,
                Width = Synthetic.Width, Height = Synthetic.Height, DHash = "0",
            };
            codec.Encode(Synthetic.Page(seq), _ws.FullPath(record), ImageFormat.Png, 0);
            _ws.Manifest.AddFrame(record);
        }
        _vm = new ExportViewModel(_ws, _ui, _shell, _dialogs);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _ws.Dispose();
        _dir.Dispose();
    }

    private void BoxAllFrames(string? firstLabel = null)
    {
        _ws.Manifest.UpdateFrames(f => f with
        {
            Boxes = [new BoxRecord { Id = "b1", Order = 0, X = 0, Y = 0, W = 300, H = 120, Label = f.Seq == 1 ? firstLabel : null }],
        });
        _ui.Drain();
    }

    [Fact]
    public void Without_boxes_export_is_off_and_says_why()
    {
        Assert.Equal(0, _vm.CropCount);
        Assert.Contains("No boxes yet", _vm.Summary, StringComparison.Ordinal);
        Assert.False(_vm.ExportCommand.CanExecute(null));
        Assert.Empty(_vm.Pages);
    }

    [Fact]
    public void Summary_and_preview_follow_the_boxes_and_options()
    {
        BoxAllFrames("Intro");
        Assert.Equal(3, _vm.CropCount);
        Assert.Contains("3 crops from 3 frames → 1 pages. 1 labelled boxes", _vm.Summary, StringComparison.Ordinal);
        PreviewPage page = Assert.Single(_vm.Pages);
        Assert.Equal(3, page.Crops.Count);
        Assert.Equal("Intro", page.Crops[0].Bookmark);
        Assert.Null(page.Crops[0].Caption);

        _vm.IsStacked = false;
        Assert.Equal(3, _vm.Pages.Count);
        _vm.Captions = true;
        Assert.StartsWith("Frame 1", _vm.Pages[0].Crops[0].Caption, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_to_A4_shows_lengths_in_millimetres()
    {
        Assert.Equal("in", _vm.Unit);
        Assert.Equal("0.5", _vm.MarginText.Replace(',', '.'));
        _vm.IsA4 = true;
        Assert.Equal("mm", _vm.Unit);
        Assert.Equal("12.7", _vm.MarginText.Replace(',', '.'));
        Assert.True(_vm.TryGetOptions(out ExportOptions o));
        Assert.Equal(36, o.MarginPt, 1);
        Assert.Equal(PaperSize.A4, o.PaperSize);
    }

    [Fact]
    public void Bad_lengths_are_reported_and_clear_the_preview()
    {
        BoxAllFrames();
        _vm.MarginText = "abc";
        Assert.NotNull(_vm.OptionsError);
        Assert.Empty(_vm.Pages);
        _vm.MarginText = "4"; // 4 in margins leave less than an inch
        Assert.Contains("inch", _vm.OptionsError, StringComparison.Ordinal);
        _vm.MarginText = "0.5";
        Assert.Null(_vm.OptionsError);
        Assert.Single(_vm.Pages);
    }

    [Fact]
    public async Task Export_writes_the_pdf_saves_the_options_and_can_open_it()
    {
        BoxAllFrames("Intro");
        _vm.IsA4 = true;
        _vm.Captions = true;

        await _vm.ExportCommand.ExecuteAsync(null);

        Assert.NotNull(_vm.LastExportPath);
        Assert.True(File.Exists(_vm.LastExportPath));
        Assert.StartsWith(_ws.ExportsPath, _vm.LastExportPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Saved 1 pages (3 crops, 1 bookmarks)", _vm.Result, StringComparison.Ordinal);
        Assert.False(_vm.IsExporting);
        Assert.Equal(PaperSize.A4, _ws.Settings.Export.PaperSize);
        Assert.True(_ws.Settings.Export.Captions);

        _vm.OpenPdfCommand.Execute(null);
        _vm.OpenFolderCommand.Execute(null);
        Assert.Equal([_vm.LastExportPath], _shell.Opened);
        Assert.Equal([_vm.LastExportPath], _shell.Shown);
    }

    [Fact]
    public void Options_are_loaded_from_the_workspace()
    {
        _ws.UpdateSettings(s => s with { Export = s.Export with { Layout = ExportLayout.OnePerPage, Scaling = ExportScaling.FitWidth, Captions = true, MaxUpscale = 3 } });
        using var vm = new ExportViewModel(_ws, _ui, _shell, _dialogs);
        Assert.False(vm.IsStacked);
        Assert.False(vm.IsSharedScale);
        Assert.True(vm.Captions);
        Assert.Equal(3, vm.MaxUpscale);
    }
}
