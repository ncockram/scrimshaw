using Scrimshaw.Core.Imaging;

namespace Scrimshaw.Capture.Sources;

/// <summary>Something that can hand over its most recent picture on demand.</summary>
public interface IFrameSource : IDisposable
{
    /// <summary>The latest frame, or null if none has arrived yet. Safe to call from any thread.</summary>
    RawImage? Grab();

    /// <summary>True once the target has gone away (window closed, monitor unplugged) (FR-C10).</summary>
    bool IsClosed { get; }
}
