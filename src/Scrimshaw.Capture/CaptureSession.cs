using System.Diagnostics;
using System.Threading.Channels;
using Scrimshaw.Capture.Sources;
using Scrimshaw.Core.Detection;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Core.Workspaces;

namespace Scrimshaw.Capture;

public enum CaptureStopReason
{
    /// <summary>The user stopped it.</summary>
    User,

    /// <summary>The window closed or the monitor went away (FR-C10).</summary>
    TargetLost,

    /// <summary>Saving failed, e.g. the disk is full. See <see cref="CaptureSession.Error"/>.</summary>
    Error,
}

/// <summary>Live counters for the capture panel (FR-C6).</summary>
/// <param name="Attempts">Timer ticks, including ones where no frame was available yet.</param>
/// <param name="Kept">Frames queued for saving.</param>
/// <param name="Discarded">Captures thrown away as unchanged.</param>
/// <param name="Dropped">Kept frames lost because the writer fell behind (should stay 0).</param>
/// <param name="Saved">Frames written to disk.</param>
/// <param name="LastChangedFraction">The latest capture vs the last saved frame, for the threshold readout.</param>
/// <param name="MeanDriftMs">Average lateness of ticks against the ideal schedule (FR-C4).</param>
public sealed record CaptureStats(
    TimeSpan Elapsed,
    int Attempts,
    int Kept,
    int Discarded,
    int Dropped,
    int Saved,
    bool Settling,
    double? LastChangedFraction,
    double MeanDriftMs,
    double MaxDriftMs)
{
    public static readonly CaptureStats Empty = new(TimeSpan.Zero, 0, 0, 0, 0, 0, false, null, 0, 0);
}

/// <summary>
/// One capture run into a workspace: a timer grabs a frame each interval, <see cref="ChangeDetector"/>
/// decides whether it's a new page, and kept frames are written in the background (FR-C1–C6, C10, C11).
/// Frames get the next free sequence numbers, so a second run adds to the first.
/// </summary>
public sealed class CaptureSession : IAsyncDisposable
{
    private const int QueueCapacity = 8;

    private readonly Workspace _workspace;
    private readonly IFrameSource _source;
    private readonly IImageCodec _codec;
    private readonly TimeProvider _time;
    private readonly WorkspaceSettings _settings;
    private readonly Channel<PendingFrame> _queue = Channel.CreateBounded<PendingFrame>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<CaptureStopReason> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _loopReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _statsLock = new();
    private CaptureStats _stats = CaptureStats.Empty;
    private double _driftTotal;
    private CaptureStopReason _reason = CaptureStopReason.User;
    private Task? _loop;
    private Task? _writer;
    private int _started;

    public CaptureSession(Workspace workspace, IFrameSource source, IImageCodec codec, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(codec);
        _workspace = workspace;
        _source = source;
        _codec = codec;
        _time = time ?? TimeProvider.System;
        _settings = workspace.Settings;
    }

    /// <summary>Raised after every tick, on the capture thread.</summary>
    public event EventHandler<CaptureStats>? StatsChanged;

    /// <summary>Raised after each frame is written and recorded, on the writer thread.</summary>
    public event EventHandler<FrameRecord>? FrameSaved;

    public CaptureStats Stats
    {
        get { lock (_statsLock) return _stats; }
    }

    /// <summary>Completes once capture has stopped and every kept frame is on disk.</summary>
    public Task<CaptureStopReason> Completion => _completion.Task;

    /// <summary>Completes once the capture timer is running (lets tests drive a fake clock).</summary>
    internal Task LoopReady => _loopReady.Task;

    /// <summary>Set when the session stopped because of <see cref="CaptureStopReason.Error"/>.</summary>
    public Exception? Error { get; private set; }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Capture has already started.");
        var detector = new ChangeDetector(_settings.Detection);
        SeedFromLastFrame(detector);
        _writer = Task.Run(WriteLoopAsync);
        _loop = Task.Run(() => CaptureLoopAsync(detector, _stop.Token));
        _ = FinishAsync();
    }

    /// <summary>Stops capturing and waits until every kept frame is saved (FR-C3).</summary>
    public async Task<CaptureStopReason> StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_started == 0) _completion.TrySetResult(CaptureStopReason.User);
        return await Completion.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task CaptureLoopAsync(ChangeDetector detector, CancellationToken token)
    {
        TimeSpan interval = TimeSpan.FromSeconds(_settings.IntervalSeconds);
        long start = _time.GetTimestamp();
        using var timer = new PeriodicTimer(interval, _time);
        _loopReady.TrySetResult();
        int tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                tick++;
                double driftMs = (_time.GetElapsedTime(start) - interval * tick).TotalMilliseconds;
                if (_source.IsClosed)
                {
                    RequestStop(CaptureStopReason.TargetLost);
                    break;
                }

                DateTimeOffset capturedAt = _time.GetUtcNow();
                RawImage? image = _source.Grab();
                DetectionResult? result = image is null ? null : detector.Process(image);
                bool dropped = false;
                if (result is { Decision: DetectionDecision.Save } r)
                    dropped = !_queue.Writer.TryWrite(new PendingFrame(r.Image!, r.Grid, capturedAt));

                CaptureStats stats;
                lock (_statsLock)
                {
                    _driftTotal += Math.Abs(driftMs);
                    _stats = stats = _stats with
                    {
                        Elapsed = _time.GetElapsedTime(start),
                        Attempts = _stats.Attempts + 1,
                        Kept = _stats.Kept + (result?.Decision == DetectionDecision.Save && !dropped ? 1 : 0),
                        Dropped = _stats.Dropped + (dropped ? 1 : 0),
                        Discarded = _stats.Discarded + (result?.Decision == DetectionDecision.Discard ? 1 : 0),
                        Settling = detector.IsPending,
                        LastChangedFraction = result?.ChangedFraction ?? _stats.LastChangedFraction,
                        MeanDriftMs = _driftTotal / tick,
                        MaxDriftMs = Math.Max(_stats.MaxDriftMs, Math.Abs(driftMs)),
                    };
                }
                StatsChanged?.Invoke(this, stats);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            _queue.Writer.TryComplete();
        }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (PendingFrame frame in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (Error is not null) continue; // drain without writing after a failure
            try
            {
                FrameRecord record = Write(frame);
                lock (_statsLock) _stats = _stats with { Saved = _stats.Saved + 1 };
                FrameSaved?.Invoke(this, record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = ex;
                RequestStop(CaptureStopReason.Error);
            }
        }
    }

    private FrameRecord Write(PendingFrame frame)
    {
        int seq = _workspace.Manifest.ReserveSequence();
        string relative = _workspace.RelativeFramePath(seq);
        var record = new FrameRecord
        {
            Seq = seq,
            File = relative,
            CapturedAt = frame.CapturedAt,
            Width = frame.Image.Width,
            Height = frame.Image.Height,
            DHash = DHash.ToHex(DHash.Compute(frame.Grid)),
        };
        string path = _workspace.FullPath(record);
        string temp = path + ".tmp";
        _codec.Encode(frame.Image, temp, _settings.ImageFormat, _settings.JpegQuality);
        File.Move(temp, path, overwrite: false);
        _workspace.Manifest.AddFrame(record);
        return record;
    }

    private void RequestStop(CaptureStopReason reason)
    {
        lock (_statsLock)
        {
            if (_reason == CaptureStopReason.User) _reason = reason;
        }
        _stop.Cancel();
    }

    private async Task FinishAsync()
    {
        try
        {
            await _loop!.ConfigureAwait(false);
            await _writer!.ConfigureAwait(false);
            _workspace.Manifest.Flush();
        }
#pragma warning disable CA1031 // Surface any failure through Completion rather than an unobserved task.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error ??= ex;
            _reason = CaptureStopReason.Error;
        }
        _completion.TrySetResult(_reason);
    }

    /// <summary>Resume support (FR-C11): compare the first captures with the newest existing frame.</summary>
    private void SeedFromLastFrame(ChangeDetector detector)
    {
        IReadOnlyList<FrameRecord> frames = _workspace.Manifest.Current.Frames;
        if (frames.Count == 0) return;
        FrameRecord last = frames[^1];
        try
        {
            detector.Seed(GrayGrid.FromImage(_codec.Decode(_workspace.FullPath(last))));
        }
#pragma warning disable CA1031 // A missing or unreadable last frame just means starting fresh.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Debug.WriteLine($"Couldn't seed change detection from frame {last.Seq}: {ex.Message}");
        }
    }

    private sealed record PendingFrame(RawImage Image, GrayGrid Grid, DateTimeOffset CapturedAt);
}
