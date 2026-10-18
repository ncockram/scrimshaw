using Microsoft.Extensions.Time.Testing;
using Scrimshaw.Capture.Sources;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.Imaging;
using Scrimshaw.TestSupport;

namespace Scrimshaw.Capture.Tests;

/// <summary>Plays back a fixed list of images, one per <see cref="Grab"/>; repeats the last one after that.</summary>
internal sealed class ScriptedSource(params RawImage?[] frames) : IFrameSource
{
    private int _next;

    public bool IsClosed { get; set; }

    public bool Disposed { get; private set; }

    public RawImage? Grab() => frames[Math.Min(_next++, frames.Length - 1)];

    public void Dispose() => Disposed = true;
}

internal sealed class FailingCodec : IImageCodec
{
    public RawImage Decode(string path) => throw new IOException("unreadable");

    public void Encode(RawImage image, string path, ImageFormat format, int jpegQuality) => throw new IOException("There is not enough space on the disk.");
}

/// <summary>Headless tests of the capture loop with a scripted source and a fake clock.</summary>
public sealed class CaptureSessionTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new();
    private readonly WicImageCodec _codec = new();

    public void Dispose() => _dir.Dispose();

    private Workspace NewWorkspace(ImageFormat format = ImageFormat.Png) =>
        Workspace.Create(_dir.Path, new WorkspaceSettings { IntervalSeconds = Interval.TotalSeconds, ImageFormat = format }, _time);

    /// <summary>Advances the clock one interval at a time, waiting for each tick to be processed.</summary>
    private async Task TickAsync(CaptureSession session, int ticks)
    {
        await session.LoopReady.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        for (int i = 0; i < ticks; i++)
        {
            int before = session.Stats.Attempts;
            _time.Advance(Interval);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (session.Stats.Attempts == before && !session.Completion.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"tick {i + 1} was not processed");
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public async Task Saves_each_new_page_once_and_discards_repeats()
    {
        using Workspace ws = NewWorkspace();
        RawImage p1 = Synthetic.Page(1), p2 = Synthetic.Page(2);
        var session = new CaptureSession(ws, new ScriptedSource(p1, p1, p1, p2, p2, p2), _codec, _time);
        var saved = new List<FrameRecord>();
        session.FrameSaved += (_, f) => { lock (saved) saved.Add(f); };

        session.Start();
        await TickAsync(session, 6);
        CaptureStopReason reason = await session.StopAsync();

        Assert.Equal(CaptureStopReason.User, reason);
        CaptureStats stats = session.Stats;
        Assert.Equal((6, 2, 2, 2, 0), (stats.Attempts, stats.Kept, stats.Saved, stats.Discarded, stats.Dropped));
        Assert.Equal([1, 2], ws.Manifest.Current.Frames.Select(f => f.Seq));
        Assert.Equal(2, saved.Count);
        foreach (FrameRecord f in ws.Manifest.Current.Frames)
        {
            RawImage onDisk = _codec.Decode(ws.FullPath(f));
            Assert.Equal((Synthetic.Width, Synthetic.Height), (onDisk.Width, onDisk.Height));
            Assert.Equal((Synthetic.Width, Synthetic.Height), (f.Width, f.Height));
            Assert.Equal(16, f.DHash.Length);
        }
        Assert.Empty(Directory.EnumerateFiles(ws.ImagesPath, "*.tmp"));
        Assert.False(ws.Manifest.IsDirty);
    }

    [Fact]
    public async Task A_cleaner_version_replaces_the_saved_image_in_place()
    {
        using Workspace ws = NewWorkspace();
        RawImage p1 = Synthetic.Page(1), withControls = Synthetic.WithPlayerControls(p1);
        var session = new CaptureSession(ws, new ScriptedSource(withControls, withControls, p1, p1, p1, p1), _codec, _time);
        var events = 0;
        session.FrameSaved += (_, _) => Interlocked.Increment(ref events);

        session.Start();
        await TickAsync(session, 6);
        await session.StopAsync();

        FrameRecord f = Assert.Single(ws.Manifest.Current.Frames);
        Assert.Equal((1, 1), (session.Stats.Saved, session.Stats.Replaced));
        Assert.Equal(2, events);
        RawImage onDisk = _codec.Decode(ws.FullPath(f));
        Assert.Equal(0, Core.Detection.AreaChange.ChangedFraction(Core.Detection.GrayGrid.FromImage(onDisk), Core.Detection.GrayGrid.FromImage(p1), 8));
        Assert.Equal(Core.Detection.DHash.ToHex(Core.Detection.DHash.Compute(Core.Detection.GrayGrid.FromImage(p1))), f.DHash);
        Assert.Empty(Directory.EnumerateFiles(ws.ImagesPath, "*.tmp"));
    }

    [Fact]
    public async Task Ticks_before_the_first_frame_are_counted_but_not_saved()
    {
        using Workspace ws = NewWorkspace();
        RawImage p1 = Synthetic.Page(1);
        var session = new CaptureSession(ws, new ScriptedSource(null, null, p1, p1), _codec, _time);

        session.Start();
        await TickAsync(session, 4);
        await session.StopAsync();

        Assert.Equal(4, session.Stats.Attempts);
        Assert.Single(ws.Manifest.Current.Frames);
    }

    [Fact]
    public async Task Stops_and_keeps_frames_when_the_target_goes_away()
    {
        using Workspace ws = NewWorkspace();
        RawImage p1 = Synthetic.Page(1);
        var source = new ScriptedSource(p1, p1);
        var session = new CaptureSession(ws, source, _codec, _time);

        session.Start();
        await TickAsync(session, 2);
        source.IsClosed = true;
        _time.Advance(Interval);
        CaptureStopReason reason = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStopReason.TargetLost, reason);
        Assert.Single(ws.Manifest.Current.Frames);
    }

    [Fact]
    public async Task Resuming_continues_numbering_and_skips_the_page_already_saved()
    {
        RawImage p1 = Synthetic.Page(1), p2 = Synthetic.Page(2);
        using (Workspace ws = NewWorkspace())
        {
            var first = new CaptureSession(ws, new ScriptedSource(p1, p1), _codec, _time);
            first.Start();
            await TickAsync(first, 2);
            await first.StopAsync();
        }

        using Workspace reopened = Workspace.Open(_dir.Path, _codec, _time);
        var second = new CaptureSession(reopened, new ScriptedSource(p1, p1, p2, p2), _codec, _time);
        second.Start();
        await TickAsync(second, 4);
        await second.StopAsync();

        Assert.Equal([1, 2], reopened.Manifest.Current.Frames.Select(f => f.Seq));
        Assert.Equal(2, second.Stats.Discarded); // p1 was already saved by the first run
    }

    private static readonly CaptureRegion TopEightyPercent = new() { X = 0, Y = 0, W = 1, H = 0.8 };

    /// <summary>Three ticks of each page, with the "performer video" strip different on every tick.</summary>
    private static RawImage[] PagesWithDistraction(params int[] pages) =>
        pages.SelectMany(p => Enumerable.Range(0, 3).Select(i => Synthetic.WithDistraction(Synthetic.Page(p), p * 10 + i))).ToArray();

    [Fact]
    public async Task Without_a_music_area_a_changing_video_strip_keeps_the_page_from_settling()
    {
        using Workspace ws = NewWorkspace();
        var session = new CaptureSession(ws, new ScriptedSource(PagesWithDistraction(1, 2)), _codec, _time);
        session.Start();
        await TickAsync(session, 6);
        await session.StopAsync();

        // The strip never stops changing, so the page never settles and nothing is saved.
        Assert.Equal(0, session.Stats.Saved);
        Assert.True(session.Stats.Settling);
    }

    [Fact]
    public async Task With_a_music_area_only_page_turns_count_and_full_frames_are_saved()
    {
        using Workspace ws = NewWorkspace();
        ws.UpdateSettings(s => s with { Region = TopEightyPercent });
        RawImage[] frames = PagesWithDistraction(1, 2);
        var session = new CaptureSession(ws, new ScriptedSource(frames), _codec, _time);
        session.Start();
        await TickAsync(session, 6);
        await session.StopAsync();

        Assert.Equal((2, 2), (session.Stats.Saved, session.Stats.Discarded)); // each page: settle, save, repeat
        FrameRecord first = ws.Manifest.Current.Frames[0];
        Assert.Equal((Synthetic.Width, Synthetic.Height), (first.Width, first.Height));
        RawImage onDisk = _codec.Decode(ws.FullPath(first));
        Assert.Equal(frames[1].Bgra, onDisk.Bgra); // saved once settled, strip and all: the area only steers detection
        Assert.Equal(Core.Detection.DHash.ToHex(Core.Detection.DHash.Compute(Core.Detection.GrayGrid.FromImage(frames[1]))), first.DHash);
    }

    [Fact]
    public async Task A_half_page_turn_inside_the_area_is_still_caught()
    {
        using Workspace ws = NewWorkspace();
        ws.UpdateSettings(s => s with { Region = TopEightyPercent });
        RawImage p1 = Synthetic.Page(1), p2 = Synthetic.Page(2), half = Synthetic.HalfTurn(p1, p2);
        RawImage[] frames = new[] { p1, p1, p1, half, half, half, p2, p2, p2 }
            .Select((img, i) => Synthetic.WithDistraction(img, 100 + i)).ToArray();
        var session = new CaptureSession(ws, new ScriptedSource(frames), _codec, _time);
        session.Start();
        await TickAsync(session, frames.Length);
        await session.StopAsync();

        Assert.Equal(3, session.Stats.Saved);
    }

    [Fact]
    public async Task Save_only_this_area_crops_what_is_saved()
    {
        using Workspace ws = NewWorkspace();
        ws.UpdateSettings(s => s with { Region = TopEightyPercent, SaveRegionOnly = true });
        var session = new CaptureSession(ws, new ScriptedSource(PagesWithDistraction(1, 2)), _codec, _time);
        session.Start();
        await TickAsync(session, 6);
        await session.StopAsync();

        Assert.Equal(2, session.Stats.Saved);
        Core.Annotation.PixelRect area = TopEightyPercent.ToPixels(Synthetic.Width, Synthetic.Height);
        foreach (FrameRecord f in ws.Manifest.Current.Frames)
        {
            RawImage onDisk = _codec.Decode(ws.FullPath(f));
            Assert.Equal((area.W, area.H), (onDisk.Width, onDisk.Height));
            Assert.Equal((area.W, area.H), (f.Width, f.Height));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resuming_with_a_music_area_skips_the_page_already_saved(bool saveRegionOnly)
    {
        using (Workspace ws = NewWorkspace())
        {
            ws.UpdateSettings(s => s with { Region = TopEightyPercent, SaveRegionOnly = saveRegionOnly });
            var first = new CaptureSession(ws, new ScriptedSource(PagesWithDistraction(1)), _codec, _time);
            first.Start();
            await TickAsync(first, 3);
            await first.StopAsync();
        }

        using Workspace reopened = Workspace.Open(_dir.Path, _codec, _time);
        var second = new CaptureSession(reopened, new ScriptedSource(PagesWithDistraction(1, 2)), _codec, _time);
        second.Start();
        await TickAsync(second, 6);
        await second.StopAsync();

        Assert.Equal([1, 2], reopened.Manifest.Current.Frames.Select(f => f.Seq));
        Assert.Equal(1, second.Stats.Saved); // page 1 was recognised from the saved frame
    }

    [Fact]
    public async Task Write_failure_stops_capture_with_the_error()
    {
        using Workspace ws = NewWorkspace();
        RawImage p1 = Synthetic.Page(1);
        var session = new CaptureSession(ws, new ScriptedSource(p1, p1, p1), new FailingCodec(), _time);

        session.Start();
        await TickAsync(session, 2);
        CaptureStopReason reason = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStopReason.Error, reason);
        Assert.IsType<IOException>(session.Error);
        Assert.Empty(ws.Manifest.Current.Frames);
    }

    [Fact]
    public async Task Jpeg_workspaces_write_jpg_files()
    {
        using Workspace ws = NewWorkspace(ImageFormat.Jpeg);
        RawImage p1 = Synthetic.Page(1);
        var session = new CaptureSession(ws, new ScriptedSource(p1, p1), _codec, _time);

        session.Start();
        await TickAsync(session, 2);
        await session.StopAsync();

        FrameRecord f = Assert.Single(ws.Manifest.Current.Frames);
        Assert.EndsWith(".jpg", f.File, StringComparison.Ordinal);
        Assert.Equal(Synthetic.Width, _codec.Decode(ws.FullPath(f)).Width);
    }

    [Fact]
    public async Task Stops_when_the_window_title_changes_and_keeps_what_was_saved()
    {
        using Workspace ws = NewWorkspace();
        RawImage p1 = Synthetic.Page(1);
        string title = "(1) Etude - YouTube";
        var session = new CaptureSession(ws, new ScriptedSource(p1, p1, p1, p1, p1), _codec, _time,
            new CaptureSessionOptions(() => title, StopOnTitleChange: true));

        session.Start();
        await TickAsync(session, 2);
        title = "(2) Etude - YouTube"; // only the notification count changed
        await TickAsync(session, 1);
        title = "Next video - YouTube";
        await TickAsync(session, 1);
        Assert.False(session.Completion.IsCompleted); // one sighting isn't enough
        _time.Advance(Interval);
        CaptureStopReason reason = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStopReason.TitleChanged, reason);
        Assert.Equal("Next video - YouTube", session.NewTitle);
        Assert.Single(ws.Manifest.Current.Frames);
    }

    [Fact]
    public async Task Title_changes_are_ignored_when_the_option_is_off()
    {
        using Workspace ws = NewWorkspace();
        string title = "A";
        var session = new CaptureSession(ws, new ScriptedSource(Synthetic.Page(1)), _codec, _time,
            new CaptureSessionOptions(() => title, StopOnTitleChange: false));
        session.Start();
        await TickAsync(session, 1);
        title = "B";
        await TickAsync(session, 3);
        Assert.False(session.Completion.IsCompleted);
        Assert.Equal(CaptureStopReason.User, await session.StopAsync());
        Assert.Null(session.NewTitle);
    }

    [Fact]
    public async Task Start_twice_is_rejected()
    {
        using Workspace ws = NewWorkspace();
        var session = new CaptureSession(ws, new ScriptedSource(Synthetic.Page(1)), _codec, _time);
        session.Start();
        Assert.Throws<InvalidOperationException>(session.Start);
        await session.StopAsync();
    }
}
