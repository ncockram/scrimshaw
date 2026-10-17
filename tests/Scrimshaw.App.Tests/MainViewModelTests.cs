using Microsoft.Extensions.Time.Testing;
using Scrimshaw.App.Services;
using Scrimshaw.App.ViewModels;
using Scrimshaw.Capture;
using Scrimshaw.Capture.Interop;
using Scrimshaw.Capture.Sources;
using Scrimshaw.Capture.Targets;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.Imaging;
using Scrimshaw.TestSupport;

namespace Scrimshaw.App.Tests;

internal sealed class FakeDialogs : IDialogService
{
    public Queue<string?> Folders { get; } = new();
    public List<string> Errors { get; } = [];

    public string? PickFolder(string title) => Folders.Count > 0 ? Folders.Dequeue() : null;

    public void ShowError(string title, string message) => Errors.Add(message);

    public Queue<bool> Answers { get; } = new();
    public List<string> Questions { get; } = [];

    public bool Confirm(string title, string message)
    {
        Questions.Add(message);
        return Answers.Count > 0 && Answers.Dequeue();
    }
}

internal sealed class ImmediateUi : IUiDispatcher
{
    private readonly Lock _gate = new();

    public void Post(Action action)
    {
        lock (_gate) action();
    }
}

internal sealed class FakeTargets(params CaptureTarget[] targets) : ITargetProvider
{
    public IReadOnlyList<CaptureTarget> GetAll() => targets;
}

internal sealed class ScriptedSource(params RawImage[] frames) : IFrameSource
{
    private int _next;

    public bool IsClosed { get; set; }

    public RawImage? Grab() => frames[Math.Min(_next++, frames.Length - 1)];

    public void Dispose() { }
}

/// <summary>Runs real <see cref="CaptureSession"/>s over a scripted source and a fake clock.</summary>
internal sealed class FakeCapture(FakeTimeProvider time, Func<ScriptedSource> source) : ICaptureService
{
    public IImageCodec Codec { get; } = new WicImageCodec();
    public CaptureSession? Last { get; private set; }
    public ScriptedSource? LastSource { get; private set; }
    public CaptureTarget? LastTarget { get; private set; }

    public CaptureSession Start(Workspace workspace, CaptureTarget target)
    {
        LastTarget = target;
        LastSource = source();
        Last = new CaptureSession(workspace, LastSource, Codec, time);
        Last.Start();
        return Last;
    }
}

public sealed class MainViewModelTests : IAsyncDisposable
{
    private static readonly WindowInfo Browser = new(new IntPtr(42), "Cinema Paradiso - YouTube - Google Chrome", "chrome", 1, new RectInt32(0, 0, 1920, 1080));
    private static readonly MonitorInfo Main = new(new IntPtr(7), @"\\.\DISPLAY1", new RectInt32(0, 0, 1920, 1080), 120, true);

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeCapture _capture;
    private readonly MainViewModel _vm;

    public MainViewModelTests()
    {
        RawImage p1 = Synthetic.Page(1), p2 = Synthetic.Page(2);
        _capture = new FakeCapture(_time, () => new ScriptedSource(p1, p1, p2, p2));
        _vm = new MainViewModel(_dialogs, new ImmediateUi(), new FakeTargets(new MonitorTarget(Main, 1), new WindowTarget(Browser)), _capture);
    }

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        _dir.Dispose();
    }

    private void CreateWorkspace()
    {
        _dialogs.Folders.Enqueue(_dir.Path);
        _vm.NewWorkspaceCommand.Execute(null);
    }

    private async Task TickAsync(int ticks)
    {
        CaptureSession session = _capture.Last!;
        await session.LoopReady.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        for (int i = 0; i < ticks; i++)
        {
            int before = session.Stats.Attempts;
            _time.Advance(TimeSpan.FromSeconds(1));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (session.Stats.Attempts == before && !session.Completion.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException();
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0.5", true)]
    [InlineData("0.25", true)]
    [InlineData("0.1", false)]
    [InlineData("61", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void Interval_validation(string text, bool valid)
    {
        Assert.Equal(valid, MainViewModel.TryParseInterval(text, out _));
        _vm.IntervalText = text;
        Assert.Equal(valid, _vm.IntervalError is null);
    }

    [Fact]
    public void Lists_targets_screens_first()
    {
        Assert.Equal(["Screens", "Windows"], _vm.Targets.Select(t => t.Group));
        Assert.Contains("125%", _vm.Targets[0].DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_needs_a_workspace_a_target_and_a_valid_interval()
    {
        Assert.False(_vm.StartCaptureCommand.CanExecute(null));
        CreateWorkspace();
        Assert.True(_vm.HasWorkspace);
        Assert.False(_vm.StartCaptureCommand.CanExecute(null));
        _vm.SelectedTarget = _vm.Targets[1];
        Assert.True(_vm.StartCaptureCommand.CanExecute(null));
        _vm.IntervalText = "0";
        Assert.False(_vm.StartCaptureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Capture_saves_settings_shows_progress_and_reports_the_result()
    {
        CreateWorkspace();
        _vm.SelectedTarget = _vm.Targets[1];
        _vm.IntervalText = "1";
        _vm.ThresholdPercent = 5;
        _vm.IsJpeg = true;

        _vm.StartCaptureCommand.Execute(null);
        Assert.True(_vm.IsCapturing);
        Assert.False(_vm.CanEditSettings);
        Assert.False(_vm.NewWorkspaceCommand.CanExecute(null));
        Assert.Same(_vm.Targets[1].Target, _capture.LastTarget);

        WorkspaceSettings saved = _vm.Workspace!.Settings;
        Assert.Equal(0.05, saved.Detection.AreaThreshold, 9);
        Assert.Equal(ImageFormat.Jpeg, saved.ImageFormat);
        Assert.Equal("chrome", saved.Target!.WindowProcessName);

        await TickAsync(4);
        await _vm.StopCaptureCommand.ExecuteAsync(null);
        await _capture.Last!.Completion;
        await UntilAsync(() => !_vm.IsCapturing); // the view-model reacts to completion on its own continuation

        Assert.False(_vm.IsCapturing);
        Assert.Equal(2, _vm.FrameCount);
        Assert.Equal(4, _vm.Stats.Attempts);
        Assert.EndsWith("frame_00002.jpg", _vm.LatestFramePath, StringComparison.Ordinal);
        Assert.Equal($"{Synthetic.Width}×{Synthetic.Height}", _vm.LatestFrameSize);
        Assert.Contains("2 new frames saved", _vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(_vm.CanChangeFormat); // frames exist now
    }

    [Fact]
    public async Task Losing_the_target_stops_capture_with_an_explanation()
    {
        CreateWorkspace();
        _vm.SelectedTarget = _vm.Targets[0];
        _vm.StartCaptureCommand.Execute(null);
        await TickAsync(2);

        _capture.LastSource!.IsClosed = true;
        _time.Advance(TimeSpan.FromSeconds(1));
        await _capture.Last!.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await UntilAsync(() => !_vm.IsCapturing);

        Assert.False(_vm.IsCapturing);
        Assert.Contains("went away", _vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, _vm.FrameCount);
    }

    [Fact]
    public async Task Reopening_restores_settings_and_the_saved_target()
    {
        CreateWorkspace();
        _vm.SelectedTarget = _vm.Targets[1];
        _vm.IntervalText = "0.5";
        _vm.ThresholdPercent = 7;
        _vm.IncludeCursor = true;
        _vm.StartCaptureCommand.Execute(null);
        await _vm.StopCaptureCommand.ExecuteAsync(null);
        await _capture.Last!.Completion;

        var other = new MainViewModel(new FakeDialogs(), new ImmediateUi(),
            new FakeTargets(new MonitorTarget(Main, 1), new WindowTarget(Browser with { Handle = new IntPtr(99) })), _capture);
        await _vm.DisposeAsync(); // release the workspace first
        other.Open(_dir.Path);

        Assert.Equal("0.5", other.IntervalText.Replace(',', '.'));
        Assert.Equal(7, other.ThresholdPercent, 9);
        Assert.True(other.IncludeCursor);
        Assert.IsType<WindowTarget>(other.SelectedTarget?.Target);
        await other.DisposeAsync();
    }

    [Fact]
    public void Creating_a_workspace_in_a_used_folder_shows_an_error()
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(_dir.Sub("something.txt"), "x");
        CreateWorkspace();
        Assert.False(_vm.HasWorkspace);
        Assert.Single(_dialogs.Errors);
    }

    [Fact]
    public void Threshold_is_kept_in_range()
    {
        _vm.ThresholdPercent = 80;
        Assert.Equal(MainViewModel.MaxThresholdPercent, _vm.ThresholdPercent);
        _vm.ThresholdPercent = 0;
        Assert.Equal(MainViewModel.MinThresholdPercent, _vm.ThresholdPercent);
    }
}
