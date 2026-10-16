namespace Scrimshaw.Core.Detection;

/// <summary>
/// Tuning for <see cref="ChangeDetector"/>. Defaults come from the Phase 0 measurements
/// (implementation plan §6.2, findings F15–F16).
/// </summary>
public sealed record ChangeDetectionSettings
{
    /// <summary>A capture is a new page when at least this fraction of the image changed (FR-D3).</summary>
    public double AreaThreshold { get; init; } = 0.03;

    /// <summary>Per-cell brightness difference (0–255) that counts as "changed" for the page-turn test.</summary>
    public int PixelTolerance { get; init; } = 24;

    /// <summary>Wait for the new page to settle before saving it (FR-D6).</summary>
    public bool SettleEnabled { get; init; } = true;

    /// <summary>How many further matching captures are needed before a changed page is saved.</summary>
    public int SettleCount { get; init; } = 1;

    /// <summary>Stricter per-cell tolerance for the settle test, so a fading ghost still counts as moving.</summary>
    public int SettleTolerance { get; init; } = 8;

    /// <summary>Two captures are "settled" when less than this fraction changed at <see cref="SettleTolerance"/>.</summary>
    public double SettleAreaThreshold { get; init; } = 0.01;
}
