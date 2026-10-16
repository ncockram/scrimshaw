using Microsoft.Extensions.Time.Testing;
using Scrimshaw.App.Services;
using Scrimshaw.App.ViewModels;
using Scrimshaw.Core.Annotation;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.TestSupport;

namespace Scrimshaw.App.Tests;

/// <summary>Queues work like the real WPF dispatcher; tests run it with <see cref="Drain"/>.</summary>
internal sealed class QueuedUi : IUiDispatcher
{
    private readonly Queue<Action> _queue = new();
    private readonly Lock _gate = new();

    public void Post(Action action)
    {
        lock (_gate) _queue.Enqueue(action);
    }

    public void Drain()
    {
        while (true)
        {
            Action? next;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out next)) return;
            }
            next();
        }
    }
}

internal sealed class FakeThumbnails : IThumbnailService
{
    public List<string> Requested { get; } = [];

    public Task<string?> GetAsync(string framePath, string thumbsFolder, CancellationToken cancellationToken)
    {
        lock (Requested) Requested.Add(framePath);
        return Task.FromResult<string?>(framePath + ".thumb.jpg");
    }
}

public sealed class ReviewViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Workspace _ws;
    private readonly QueuedUi _ui = new();
    private readonly FakeThumbnails _thumbs = new();
    private readonly ReviewViewModel _vm;
    private readonly List<string> _messages = [];

    public ReviewViewModelTests()
    {
        _ws = Workspace.Create(_dir.Path, time: new FakeTimeProvider());
        for (int seq = 1; seq <= 4; seq++) AddFrame(seq);
        _vm = new ReviewViewModel(_ws, _thumbs, _ui);
        _vm.Message += (_, m) => _messages.Add(m);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _ws.Dispose();
        _dir.Dispose();
    }

    private void AddFrame(int seq) => _ws.Manifest.AddFrame(new FrameRecord
    {
        Seq = seq, File = _ws.RelativeFramePath(seq), CapturedAt = DateTimeOffset.UnixEpoch, Width = 1000, Height = 800, DHash = "h" + seq,
    });

    [Fact]
    public void Lists_frames_and_starts_on_the_first()
    {
        Assert.Equal([1, 2, 3, 4], _vm.Frames.Select(f => f.Seq));
        Assert.Equal(1, _vm.SelectedFrame?.Seq);
        Assert.StartsWith("Frame 1 · 1 of 4 · 1000×800", _vm.FramePosition, StringComparison.Ordinal);
    }

    [Fact]
    public void Drawing_a_box_adds_and_selects_it_immediately()
    {
        _vm.AddBox(new PixelRect(10, 20, 300, 200));

        BoxViewModel box = Assert.Single(_vm.Boxes);
        Assert.Same(box, _vm.SelectedBox);
        Assert.Equal("1. Box", box.Title);
        Assert.Equal(1, _vm.SelectedFrame!.BoxCount);
    }

    [Fact]
    public void Moving_a_box_keeps_it_selected()
    {
        _vm.AddBox(new PixelRect(10, 20, 300, 200));
        string id = _vm.SelectedBox!.Id;
        _vm.UpdateBox(id, new PixelRect(50, 60, 300, 200));
        Assert.Equal(new PixelRect(50, 60, 300, 200), _vm.SelectedBox!.Rect);
    }

    [Fact]
    public void Label_follows_selection_and_is_saved_on_commit()
    {
        _vm.AddBox(new PixelRect(10, 20, 300, 200));
        _vm.LabelText = "Mvt. I";
        _vm.CommitLabelCommand.Execute(null);
        Assert.Equal("1. Mvt. I", _vm.SelectedBox!.Title);

        _vm.AddBox(new PixelRect(10, 300, 300, 200));
        Assert.Equal("", _vm.LabelText);
        _vm.SelectBox(_vm.Boxes[0].Id);
        Assert.Equal("Mvt. I", _vm.LabelText);
    }

    [Fact]
    public void Navigation_stops_at_the_ends()
    {
        Assert.False(_vm.PreviousFrameCommand.CanExecute(null));
        _vm.NextFrameCommand.Execute(null);
        _vm.NextFrameCommand.Execute(null);
        _vm.NextFrameCommand.Execute(null);
        Assert.Equal(4, _vm.SelectedFrame!.Seq);
        Assert.False(_vm.NextFrameCommand.CanExecute(null));
        _vm.PreviousFrameCommand.Execute(null);
        Assert.Equal(3, _vm.SelectedFrame!.Seq);
    }

    [Fact]
    public void Apply_to_following_then_undo_returns_to_the_source_frame()
    {
        _vm.AddBox(new PixelRect(10, 20, 300, 200));
        _vm.ApplyToFollowingCommand.Execute(null);
        Assert.Equal([1, 1, 1, 1], _vm.Frames.Select(f => f.BoxCount));
        Assert.Contains("3 following frames", _messages[^1], StringComparison.Ordinal);

        _vm.NextFrameCommand.Execute(null);
        _vm.NextFrameCommand.Execute(null);
        _vm.UndoCommand.Execute(null);
        Assert.Equal(1, _vm.SelectedFrame!.Seq);
        Assert.Equal([1, 0, 0, 0], _vm.Frames.Select(f => f.BoxCount));
        Assert.True(_vm.RedoCommand.CanExecute(null));
    }

    [Fact]
    public void Apply_to_following_without_boxes_explains_what_to_do()
    {
        _vm.ApplyToFollowingCommand.Execute(null);
        Assert.Contains("Draw boxes", _messages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Deleting_and_restoring_a_frame()
    {
        _vm.ToggleFrameDeletedCommand.Execute(null);
        Assert.True(_vm.SelectedFrame!.Deleted);
        Assert.Contains("deleted", _vm.FramePosition, StringComparison.Ordinal);
        _vm.ToggleFrameDeletedCommand.Execute(null);
        Assert.False(_vm.SelectedFrame!.Deleted);
    }

    [Fact]
    public void Delete_box_command()
    {
        _vm.AddBox(new PixelRect(10, 20, 300, 200));
        _vm.DeleteSelectedBoxCommand.Execute(null);
        Assert.Empty(_vm.Boxes);
        Assert.Null(_vm.SelectedBox);
        Assert.False(_vm.DeleteSelectedBoxCommand.CanExecute(null));
    }

    [Fact]
    public void Frames_captured_while_reviewing_appear_in_the_strip()
    {
        AddFrame(5);
        _ui.Drain();
        Assert.Equal(5, _vm.Frames.Count);
        Assert.Equal(1, _vm.SelectedFrame!.Seq); // selection isn't disturbed
    }

    [Fact]
    public async Task Thumbnails_are_requested_and_shown()
    {
        for (int i = 0; i < 100 && _vm.Frames.Any(f => f.ThumbPath is null); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            _ui.Drain();
        }
        Assert.All(_vm.Frames, f => Assert.EndsWith(".thumb.jpg", f.ThumbPath, StringComparison.Ordinal));
    }

    [Fact]
    public void A_replaced_image_bumps_the_version_and_refreshes_the_thumbnail()
    {
        string before = _vm.Frames[0].ImageVersion;
        int requests = _thumbs.Requested.Count;
        _ws.Manifest.UpdateFrame(1, f => f with { DHash = "new" });
        _ui.Drain();
        Assert.NotEqual(before, _vm.Frames[0].ImageVersion);
        SpinWait.SpinUntil(() => _thumbs.Requested.Count > requests, TimeSpan.FromSeconds(5));
        Assert.True(_thumbs.Requested.Count > requests);
    }
}
