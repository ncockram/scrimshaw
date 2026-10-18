using System.Windows.Media.Imaging;
using Microsoft.Extensions.Time.Testing;
using Scrimshaw.App.Services;
using Scrimshaw.App.ViewModels;
using Scrimshaw.Capture.Interop;
using Scrimshaw.Capture.Sources;
using Scrimshaw.Capture.Targets;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Imaging;
using Scrimshaw.TestSupport;

namespace Scrimshaw.App.Tests;

/// <summary>Records preview sessions and lets tests push frames into them.</summary>
internal sealed class FakePreview : IPreviewService
{
    public List<FakePreviewSession> Sessions { get; } = [];
    public Exception? FailWith { get; set; }

    public FakePreviewSession? Active => Sessions.LastOrDefault(s => !s.Disposed);

    public IPreviewSession Start(CaptureTarget target, bool includeCursor)
    {
        if (FailWith is not null) throw FailWith;
        var s = new FakePreviewSession(target, includeCursor);
        Sessions.Add(s);
        return s;
    }
}

internal sealed class FakePreviewSession(CaptureTarget target, bool includeCursor) : IPreviewSession
{
    public CaptureTarget Target { get; } = target;
    public bool IncludeCursor { get; } = includeCursor;
    public bool Disposed { get; private set; }

    public event EventHandler<PreviewFrame>? Frame;

    public void Push(PreviewFrame frame) => Frame?.Invoke(this, frame);

    public void Dispose() => Disposed = true;
}

public class ImageOpsTests
{
    [Fact]
    public void Small_images_are_left_alone()
    {
        RawImage img = Synthetic.Solid(800, 600, 10);
        Assert.Same(img, ImageOps.Downscale(img, 1600));
    }

    [Fact]
    public void Large_images_shrink_by_a_whole_factor_to_fit()
    {
        RawImage small = ImageOps.Downscale(Synthetic.Solid(3840, 2160, 77), 1600);
        Assert.Equal((1280, 720), (small.Width, small.Height)); // factor 3
        Assert.Equal(77, small.Bgra[0]);
    }

    [Theory]
    [InlineData(1921, 1081)]
    [InlineData(3201, 7)]
    [InlineData(5000, 1)]
    public void Odd_sizes_dont_read_past_the_edge(int w, int h)
    {
        RawImage small = ImageOps.Downscale(Synthetic.Solid(w, h, 200), 1600);
        Assert.True(small.Width <= 1600 && small.Height >= 1);
        Assert.Equal(200, small.Bgra[^2]); // the last pixel averages only real pixels
    }

    [Fact]
    public void Pixels_are_averaged()
    {
        // 2×1 image: black and white → one grey pixel.
        var img = new RawImage(2, 1, [0, 0, 0, 255, 255, 255, 255, 255]);
        RawImage one = ImageOps.Downscale(img, 1);
        Assert.Equal((1, 1), (one.Width, one.Height));
        Assert.Equal(127, one.Bgra[0]);
    }
}

public class PreviewSessionTests
{
    private sealed class Source(RawImage? image) : IFrameSource
    {
        public RawImage? Image { get; set; } = image;
        public bool IsClosed { get; set; }
        public bool Disposed { get; private set; }
        public RawImage? Grab() => Image;
        public void Dispose() => Disposed = true;
    }

    private static List<PreviewFrame> Collect(PreviewSession s)
    {
        var frames = new List<PreviewFrame>();
        s.Frame += (_, f) => { lock (frames) frames.Add(f); };
        return frames;
    }

    [Fact]
    public void Frames_are_downscaled_but_report_the_full_size()
    {
        using var s = new PreviewSession(new Source(Synthetic.Solid(3840, 2160, 50)), () => false);
        var frames = Collect(s);
        s.Tick();
        PreviewFrame f;
        lock (frames) f = frames[^1];
        Assert.Equal((3840, 2160, (string?)null), (f.Width, f.Height, f.Problem));
        Assert.True(f.Image!.PixelWidth <= PreviewSession.MaxWidth);
        Assert.True(f.Image.IsFrozen);
    }

    [Fact]
    public void A_minimised_window_is_reported_instead_of_a_stale_picture()
    {
        bool minimised = true;
        using var s = new PreviewSession(new Source(Synthetic.Solid(10, 10, 1)), () => minimised);
        var frames = Collect(s);
        s.Tick();
        lock (frames) Assert.Equal(PreviewService.Minimised, frames[^1].Problem);
    }

    [Fact]
    public void One_missing_frame_is_tolerated_two_are_reported()
    {
        var source = new Source(null);
        using var s = new PreviewSession(source, () => false);
        var frames = Collect(s);
        s.Tick();
        int afterOne;
        lock (frames) afterOne = frames.Count(f => f.Problem is not null);
        s.Tick();
        lock (frames) Assert.True(frames.Count(f => f.Problem is not null) > afterOne || afterOne > 0);
    }

    [Fact]
    public void A_closed_window_is_reported()
    {
        using var s = new PreviewSession(new Source(Synthetic.Solid(10, 10, 1)) { IsClosed = true }, () => false);
        var frames = Collect(s);
        s.Tick();
        lock (frames) Assert.Equal(PreviewService.Closed, frames[^1].Problem);
    }

    [Fact]
    public void Disposing_releases_the_source_and_stops_frames()
    {
        var source = new Source(Synthetic.Solid(10, 10, 1));
        var s = new PreviewSession(source, () => false);
        var frames = Collect(s);
        s.Dispose();
        int count;
        lock (frames) count = frames.Count;
        s.Tick();
        Assert.True(source.Disposed);
        lock (frames) Assert.Equal(count, frames.Count);
    }
}

public sealed class PreviewViewModelTests : IAsyncDisposable
{
    private static readonly WindowInfo Browser = new(new IntPtr(42), "Etude - YouTube - Google Chrome", "chrome", 1, new RectInt32(0, 0, 1920, 1080));
    private static readonly MonitorInfo Main = new(new IntPtr(7), @"\\.\DISPLAY1", new RectInt32(0, 0, 1920, 1080), 96, true);

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new();
    private readonly QueuedUi _ui = new();
    private readonly FakePreview _preview = new();
    private readonly FakeCapture _capture;
    private readonly MainViewModel _vm;
    private readonly FakeDialogs _dialogs = new();

    public PreviewViewModelTests()
    {
        _capture = new FakeCapture(_time, () => new ScriptedSource(Synthetic.Page(1)));
        _vm = new MainViewModel(_dialogs, _ui, new FakeTargets(new MonitorTarget(Main, 1), new WindowTarget(Browser)), _capture, preview: _preview);
    }

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        _dir.Dispose();
    }

    private void OpenWorkspace()
    {
        _dialogs.Folders.Enqueue(_dir.Path);
        _vm.NewWorkspaceCommand.Execute(null);
    }

    private static PreviewFrame Picture(int w = 1920, int h = 1080) =>
        new(WicImageCodec.ToBitmapSource(Synthetic.Solid(64, 36, 90)), w, h, null);

    [Fact]
    public void No_preview_without_a_workspace()
    {
        _vm.SelectedTarget = _vm.Targets[1];
        Assert.Empty(_preview.Sessions);
        Assert.False(_vm.IsPreviewing);
    }

    [Fact]
    public void Choosing_a_source_previews_it_without_saving_anything()
    {
        OpenWorkspace();
        _vm.SelectedTarget = _vm.Targets[1];

        Assert.True(_vm.IsPreviewing);
        Assert.Same(_vm.Targets[1].Target, _preview.Active!.Target);
        Assert.Equal("Preview: not saved", _vm.FrameAreaHeader);

        _preview.Active.Push(Picture());
        _ui.Drain();
        Assert.NotNull(_vm.PreviewImage);
        Assert.Equal("Preview: not saved (1920×1080)", _vm.FrameAreaHeader);
        Assert.Equal(0, _vm.FrameCount);
        Assert.Empty(Directory.EnumerateFiles(_vm.Workspace!.ImagesPath));
    }

    [Fact]
    public void Changing_source_restarts_the_preview_for_the_new_one()
    {
        OpenWorkspace();
        _vm.SelectedTarget = _vm.Targets[1];
        FakePreviewSession first = _preview.Active!;
        _vm.SelectedTarget = _vm.Targets[0];
        Assert.True(first.Disposed);
        Assert.Same(_vm.Targets[0].Target, _preview.Active!.Target);
    }

    [Fact]
    public void Starting_capture_stops_the_preview_first_then_shows_saved_frames()
    {
        OpenWorkspace();
        _vm.SelectedTarget = _vm.Targets[1];
        FakePreviewSession session = _preview.Active!;
        bool previewStoppedBeforeCapture = false;
        _capture.BeforeStart = () => previewStoppedBeforeCapture = session.Disposed;

        _vm.StartCaptureCommand.Execute(null);

        Assert.True(previewStoppedBeforeCapture);
        Assert.False(_vm.IsPreviewing);
        Assert.StartsWith("Latest saved frame", _vm.FrameAreaHeader, StringComparison.Ordinal);
        Assert.Null(_preview.Active);
        Assert.False(_vm.ShowPreviewCommand.CanExecute(null)); // not while capturing
    }

    [Fact]
    public async Task After_capture_the_latest_frame_shows_until_preview_is_asked_for()
    {
        OpenWorkspace();
        _vm.SelectedTarget = _vm.Targets[0];
        _vm.StartCaptureCommand.Execute(null);
        await _vm.StopCaptureCommand.ExecuteAsync(null);
        await _capture.Last!.Completion;
        for (int i = 0; i < 200 && _vm.IsCapturing; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            _ui.Drain();
        }

        Assert.False(_vm.IsPreviewing);
        Assert.True(_vm.ShowPreviewCommand.CanExecute(null));
        _vm.ShowPreviewCommand.Execute(null);
        Assert.True(_vm.IsPreviewing);
        Assert.NotNull(_preview.Active);
    }

    [Fact]
    public void Leaving_the_capture_tab_pauses_the_preview()
    {
        OpenWorkspace();
        _vm.SelectedTarget = _vm.Targets[1];
        FakePreviewSession first = _preview.Active!;

        _vm.SelectedTab = 1;
        Assert.True(first.Disposed);
        Assert.True(_vm.IsPreviewing); // still in preview mode for when you come back

        _vm.SelectedTab = 0;
        Assert.NotNull(_preview.Active);
        Assert.NotSame(first, _preview.Active);
    }

    [Fact]
    public void Cursor_setting_is_respected_by_the_preview()
    {
        OpenWorkspace();
        _vm.SelectedTarget = _vm.Targets[1];
        Assert.False(_preview.Active!.IncludeCursor);
        _vm.IncludeCursor = true;
        Assert.True(_preview.Active!.IncludeCursor);
    }

    [Fact]
    public void Problems_are_shown_instead_of_a_picture()
    {
        OpenWorkspace();
        _vm.SelectedTarget = _vm.Targets[1];
        _preview.Active!.Push(new PreviewFrame(null, 0, 0, PreviewService.Minimised));
        _ui.Drain();
        Assert.Equal(PreviewService.Minimised, _vm.PreviewProblem);
        Assert.Null(_vm.PreviewImage);

        _preview.Active.Push(Picture());
        _ui.Drain();
        Assert.Null(_vm.PreviewProblem);
    }

    [Fact]
    public void A_preview_that_cant_start_says_why()
    {
        OpenWorkspace();
        _preview.FailWith = new NotSupportedException("Screen capture isn't supported on this version of Windows.");
        _vm.SelectedTarget = _vm.Targets[1];
        Assert.StartsWith("Preview unavailable", _vm.PreviewProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void Frames_from_a_stopped_session_are_ignored()
    {
        OpenWorkspace();
        _vm.SelectedTarget = _vm.Targets[1];
        FakePreviewSession old = _preview.Active!;
        _vm.SelectedTarget = _vm.Targets[0];
        old.Push(Picture(111, 222));
        _ui.Drain();
        Assert.Null(_vm.PreviewSize);
    }

    [Fact]
    public async Task Switching_workspace_and_closing_stop_the_preview()
    {
        OpenWorkspace();
        _vm.SelectedTarget = _vm.Targets[1];
        FakePreviewSession first = _preview.Active!;

        string other = _dir.Sub("other");
        Core.Workspaces.Workspace.Create(other).Dispose();
        _vm.Open(other);
        Assert.True(first.Disposed);

        await _vm.DisposeAsync();
        Assert.All(_preview.Sessions, s => Assert.True(s.Disposed));
    }
}
