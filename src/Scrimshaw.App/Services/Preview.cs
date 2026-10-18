using System.Windows.Media.Imaging;
using Scrimshaw.Capture.Sources;
using Scrimshaw.Capture.Targets;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Imaging;

namespace Scrimshaw.App.Services;

/// <summary>One preview update: a picture of the source, or why there isn't one.</summary>
/// <param name="Image">Frozen and downscaled, safe to show on the UI thread. Null when <paramref name="Problem"/> is set.</param>
/// <param name="Width">The source's full width in pixels.</param>
/// <param name="Problem">Why nothing can be shown, e.g. the window is minimised.</param>
public sealed record PreviewFrame(BitmapSource? Image, int Width, int Height, string? Problem);

/// <summary>A running preview. Disposing it stops grabbing and releases the capture source.</summary>
public interface IPreviewSession : IDisposable
{
    /// <summary>Raised about once a second, on a background thread.</summary>
    event EventHandler<PreviewFrame>? Frame;
}

/// <summary>Live preview of a capture target (FR-C13), behind an interface so view-models can be tested.</summary>
public interface IPreviewService
{
    /// <exception cref="NotSupportedException">Screen capture isn't available.</exception>
    IPreviewSession Start(CaptureTarget target, bool includeCursor);
}

public sealed class PreviewService : IPreviewService
{
    public const string Minimised = "Nothing to preview: is the window minimised?";
    public const string Closed = "Nothing to preview: the window has closed.";

    public IPreviewSession Start(CaptureTarget target, bool includeCursor)
    {
        ArgumentNullException.ThrowIfNull(target);
        Func<bool> minimised = target is WindowTarget w ? () => CaptureTargets.IsWindowMinimized(w.Window.Handle) : () => false;
        return new PreviewSession(FrameSources.Create(target, includeCursor), minimised);
    }
}

/// <summary>
/// Grabs the latest frame about once a second. Pictures are shrunk to at most <see cref="MaxWidth"/> pixels
/// wide and frozen, so a preview costs little memory and can be handed to the UI thread directly.
/// </summary>
public sealed class PreviewSession : IPreviewSession
{
    public const int MaxWidth = 1600;
    private static readonly TimeSpan FirstGrab = TimeSpan.FromMilliseconds(250), Period = TimeSpan.FromSeconds(1);

    private readonly IFrameSource _source;
    private readonly Func<bool> _isMinimised;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private bool _disposed;
    private int _misses;

    public PreviewSession(IFrameSource source, Func<bool> isMinimised)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _isMinimised = isMinimised;
        _timer = new Timer(_ => Tick(), null, FirstGrab, Period);
    }

    public event EventHandler<PreviewFrame>? Frame;

    /// <summary>One grab; also called directly by tests.</summary>
    internal void Tick()
    {
        lock (_gate)
        {
            if (_disposed) return;
            PreviewFrame frame;
            if (_source.IsClosed)
            {
                frame = new(null, 0, 0, PreviewService.Closed);
            }
            else if (_isMinimised())
            {
                // A minimised window delivers no new frames; the last one would be stale, so say so instead.
                frame = new(null, 0, 0, PreviewService.Minimised);
            }
            else if (_source.Grab() is { } image)
            {
                _misses = 0;
                frame = new(WicImageCodec.ToBitmapSource(ImageOps.Downscale(image, MaxWidth)), image.Width, image.Height, null);
            }
            else if (++_misses >= 2)
            {
                frame = new(null, 0, 0, PreviewService.Minimised);
            }
            else
            {
                return; // the first frame may simply not have arrived yet
            }
            Frame?.Invoke(this, frame);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer.Dispose();
        lock (_gate) _source.Dispose(); // waits for an in-flight grab to finish
    }
}
