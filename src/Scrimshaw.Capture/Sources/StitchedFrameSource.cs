using Scrimshaw.Capture.Interop;
using Scrimshaw.Core.Imaging;

namespace Scrimshaw.Capture.Sources;

/// <summary>
/// The whole desktop (FR-C2a): one source per monitor, drawn into a single image at each monitor's
/// position on the virtual desktop. Gaps between monitors stay black.
/// </summary>
public sealed class StitchedFrameSource : IFrameSource
{
    private readonly IReadOnlyList<(IFrameSource Source, RectInt32 Bounds)> _parts;
    private readonly int _left, _top, _width, _height;

    public StitchedFrameSource(IReadOnlyList<(IFrameSource Source, RectInt32 Bounds)> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0) throw new ArgumentException("At least one monitor is needed.", nameof(parts));
        _parts = parts;
        _left = parts.Min(p => p.Bounds.Left);
        _top = parts.Min(p => p.Bounds.Top);
        _width = parts.Max(p => p.Bounds.Right) - _left;
        _height = parts.Max(p => p.Bounds.Bottom) - _top;
    }

    public bool IsClosed => _parts.Any(p => p.Source.IsClosed);

    /// <returns>Null until every monitor has delivered a frame.</returns>
    public RawImage? Grab()
    {
        int stride = _width * 4;
        var canvas = new byte[stride * _height];
        foreach (var (source, bounds) in _parts)
        {
            RawImage? part = source.Grab();
            if (part is null) return null;
            int w = Math.Min(part.Width, bounds.Width), h = Math.Min(part.Height, bounds.Height);
            int x0 = bounds.Left - _left, y0 = bounds.Top - _top;
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(part.Bgra, y * part.Stride, canvas, (y0 + y) * stride + x0 * 4, w * 4);
        }
        return new RawImage(_width, _height, stride, canvas);
    }

    public void Dispose()
    {
        foreach (var (source, _) in _parts) source.Dispose();
    }
}
