using Microsoft.Extensions.Time.Testing;
using Scrimshaw.App.ViewModels;
using Scrimshaw.Capture.Interop;
using Scrimshaw.Capture.Targets;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.Imaging;
using Scrimshaw.TestSupport;

namespace Scrimshaw.App.Tests;

/// <summary>FR-C12: marking, clearing and keeping the music area from the main window.</summary>
public sealed class RegionViewModelTests : IAsyncDisposable
{
    private static readonly WindowInfo Browser = new(new IntPtr(42), "Etude - YouTube - Google Chrome", "chrome", 1, new RectInt32(0, 0, 1920, 1080));
    private static readonly CaptureRegion TopEightyPercent = new() { X = 0, Y = 0, W = 1, H = 0.8 };

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new();
    private readonly QueuedUi _ui = new();
    private readonly FakePreview _preview = new();
    private readonly FakeCapture _capture;
    private readonly FakeDialogs _dialogs = new();
    private MainViewModel _vm;

    public RegionViewModelTests()
    {
        _capture = new FakeCapture(_time, () => new ScriptedSource(Synthetic.Page(1)));
        _vm = NewViewModel();
    }

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        _dir.Dispose();
    }

    private MainViewModel NewViewModel() =>
        new(_dialogs, _ui, new FakeTargets(new WindowTarget(Browser)), _capture, preview: _preview);

    private void OpenWorkspaceAndPreview()
    {
        _dialogs.Folders.Enqueue(_dir.Path);
        _vm.NewWorkspaceCommand.Execute(null);
        _vm.SelectedTarget = _vm.Targets[0];
    }

    [Fact]
    public void Starts_with_the_whole_source()
    {
        OpenWorkspaceAndPreview();
        Assert.Null(_vm.Region);
        Assert.True(_vm.CanDrawRegion);
        Assert.False(_vm.CanSaveRegionOnly);
        Assert.False(_vm.ClearRegionCommand.CanExecute(null));
        Assert.StartsWith("Optional: drag on the preview", _vm.RegionText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_drawn_area_is_shown_and_kept_with_the_workspace()
    {
        OpenWorkspaceAndPreview();
        _vm.SetRegionCommand.Execute(TopEightyPercent);

        Assert.Equal(TopEightyPercent, _vm.Region);
        Assert.Equal(TopEightyPercent, _vm.Workspace!.Settings.Region);
        Assert.True(_vm.CanSaveRegionOnly);
        Assert.True(_vm.ShowRegionOnLatestFrame);
        Assert.Contains("100% × 80%", _vm.RegionText, StringComparison.Ordinal);
        Assert.True(_vm.ClearRegionCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_area_and_save_option_come_back_when_the_workspace_is_reopened()
    {
        OpenWorkspaceAndPreview();
        _vm.SetRegionCommand.Execute(TopEightyPercent);
        _vm.SaveRegionOnly = true;
        await _vm.DisposeAsync();

        _vm = NewViewModel();
        _vm.Open(_dir.Path);
        Assert.Equal(TopEightyPercent, _vm.Region);
        Assert.True(_vm.SaveRegionOnly);
        Assert.False(_vm.ShowRegionOnLatestFrame); // saved frames are already just the area
    }

    [Fact]
    public void Clearing_returns_to_the_whole_source_and_unticks_save_only()
    {
        OpenWorkspaceAndPreview();
        _vm.SetRegionCommand.Execute(TopEightyPercent);
        _vm.SaveRegionOnly = true;
        _vm.ClearRegionCommand.Execute(null);

        Assert.Null(_vm.Region);
        Assert.False(_vm.SaveRegionOnly);
        Assert.Null(_vm.Workspace!.Settings.Region);
        Assert.False(_vm.Workspace.Settings.SaveRegionOnly);
    }

    [Fact]
    public void Slivers_and_clicks_are_ignored()
    {
        OpenWorkspaceAndPreview();
        _vm.SetRegionCommand.Execute(new CaptureRegion { X = 0.5, Y = 0.5, W = 0.001, H = 0.5 });
        _vm.SetRegionCommand.Execute(null);
        Assert.Null(_vm.Region);
    }

    [Fact]
    public void The_area_cant_change_while_capturing_and_is_used_by_the_session()
    {
        OpenWorkspaceAndPreview();
        _vm.SetRegionCommand.Execute(TopEightyPercent);
        _vm.SaveRegionOnly = true;
        _vm.StartCaptureCommand.Execute(null);

        Assert.True(_vm.IsCapturing);
        Assert.False(_vm.CanDrawRegion);
        Assert.False(_vm.CanSaveRegionOnly);
        Assert.False(_vm.SetRegionCommand.CanExecute(TopEightyPercent));
        Assert.False(_vm.ClearRegionCommand.CanExecute(null));
        WorkspaceSettings s = _vm.Workspace!.Settings;
        Assert.Equal((TopEightyPercent, true), (s.Region, s.SaveRegionOnly));
    }
}
