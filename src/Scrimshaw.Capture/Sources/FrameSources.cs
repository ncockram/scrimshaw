using Scrimshaw.Capture.Targets;
using Scrimshaw.Core.Imaging;

namespace Scrimshaw.Capture.Sources;

/// <summary>Builds the right frame source for a target.</summary>
public static class FrameSources
{
    /// <exception cref="NotSupportedException">Windows.Graphics.Capture isn't available.</exception>
    public static IFrameSource Create(CaptureTarget target, bool includeCursor)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!WgcFrameSource.IsSupported)
            throw new NotSupportedException("Screen capture isn't supported on this version of Windows.");

        var d3d = new D3DDevice();
        try
        {
            IFrameSource source = target switch
            {
                MonitorTarget m => WgcFrameSource.ForMonitor(d3d, m.Monitor.Handle, includeCursor),
                WindowTarget w => WgcFrameSource.ForWindow(d3d, w.Window.Handle, includeCursor),
                AllMonitorsTarget all => new StitchedFrameSource(all.Monitors
                    .Select(m => ((IFrameSource)WgcFrameSource.ForMonitor(d3d, m.Handle, includeCursor), m.Bounds))
                    .ToList()),
                _ => throw new ArgumentException($"Unknown target type {target.GetType().Name}.", nameof(target)),
            };
            return new OwningSource(source, d3d);
        }
        catch
        {
            d3d.Dispose();
            throw;
        }
    }

    /// <summary>Disposes the shared device after the sources that use it.</summary>
    private sealed class OwningSource(IFrameSource inner, IDisposable device) : IFrameSource
    {
        public bool IsClosed => inner.IsClosed;

        public RawImage? Grab() => inner.Grab();

        public void Dispose()
        {
            inner.Dispose();
            device.Dispose();
        }
    }
}
