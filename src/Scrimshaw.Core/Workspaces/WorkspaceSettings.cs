using Scrimshaw.Core.Detection;
using Scrimshaw.Core.Export;
using Scrimshaw.Core.Imaging;

namespace Scrimshaw.Core.Workspaces;

public enum CaptureTargetKind
{
    AllMonitors,
    Monitor,
    Window,
}

/// <summary>
/// How to find the capture target again when a workspace is reopened. During a session the target is
/// tracked by handle; titles change (e.g. YouTube autoplay), so they're only a hint for re-finding it.
/// </summary>
public sealed record CaptureTargetDescriptor
{
    public required CaptureTargetKind Kind { get; init; }

    /// <summary>For <see cref="CaptureTargetKind.Monitor"/>: the device name, e.g. <c>\\.\DISPLAY1</c>.</summary>
    public string? MonitorDeviceName { get; set; }

    /// <summary>For <see cref="CaptureTargetKind.Window"/>: owning process name, e.g. <c>chrome</c>.</summary>
    public string? WindowProcessName { get; set; }

    /// <summary>For <see cref="CaptureTargetKind.Window"/>: the title when the target was chosen.</summary>
    public string? WindowTitle { get; set; }
}

/// <summary>Contents of <c>scrimshaw.json</c> (FR-W2).</summary>
public sealed record WorkspaceSettings
{
    public const int CurrentSchemaVersion = 1;
    public const double MinIntervalSeconds = 0.25;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public CaptureTargetDescriptor? Target { get; set; }
    public double IntervalSeconds { get; set; } = 1.0;
    public bool IncludeCursor { get; set; }

    /// <summary>Stop capturing a window when its title changes, e.g. the next video starts (FR-C14).</summary>
    public bool StopOnTitleChange { get; set; } = true;
    public ChangeDetectionSettings Detection { get; set; } = new();
    public ImageFormat ImageFormat { get; set; } = ImageFormat.Png;
    public int JpegQuality { get; set; } = 90;
    public ExportOptions Export { get; set; } = new();

    /// <exception cref="ArgumentException">A value is out of range.</exception>
    public void Validate()
    {
        static void Check(bool ok, string message)
        {
            if (!ok) throw new ArgumentException(message);
        }

        Check(IntervalSeconds >= MinIntervalSeconds, $"Capture interval must be at least {MinIntervalSeconds} s.");
        Check(JpegQuality is >= 1 and <= 100, "JPEG quality must be between 1 and 100.");
        Check(Detection.AreaThreshold is > 0 and <= 1, "Change threshold must be above 0% and at most 100%.");
        Check(Detection.PixelTolerance is >= 0 and <= 255, "Pixel tolerance must be 0–255.");
        Check(Detection.SettleTolerance is >= 0 and <= 255, "Settle tolerance must be 0–255.");
        Check(Detection.SettleAreaThreshold is > 0 and <= 1, "Settle threshold must be above 0% and at most 100%.");
        Check(Detection.SettleCount >= 0, "Settle count can't be negative.");
        Export.Validate();
    }
}
