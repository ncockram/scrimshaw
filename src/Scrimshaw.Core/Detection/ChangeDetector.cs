using Scrimshaw.Core.Imaging;

namespace Scrimshaw.Core.Detection;

public enum DetectionDecision
{
    /// <summary>Same as the last saved frame (or a blip that went away). Throw it away.</summary>
    Discard,

    /// <summary>Changed, but still settling. Nothing to save yet.</summary>
    Pending,

    /// <summary>A new, settled page. Save <see cref="DetectionResult.Image"/>.</summary>
    Save,
}

/// <param name="ChangedFraction">This capture vs the last saved frame (1.0 when nothing has been saved yet).</param>
/// <param name="Image">The capture, when <paramref name="Decision"/> is <see cref="DetectionDecision.Save"/>.</param>
/// <param name="Grid">The capture's grid, reusable by the caller (e.g. for hashing) without recomputing.</param>
public readonly record struct DetectionResult(DetectionDecision Decision, double ChangedFraction, RawImage? Image, GrayGrid Grid);

/// <summary>
/// Decides which captures to keep (FR-D1, D5–D7). Compares each capture with the last saved frame, and
/// only saves a changed page once it has stopped changing, which skips page-turn animations and fades.
/// Not thread-safe: feed it from one capture loop.
/// <code>
/// Stable:  vs lastSaved: different → Pending (or Save at once if settling is off); same → Discard
/// Pending: vs lastSaved: same      → Stable, Discard (it was a blip)
///          vs candidate: settled   → count++; count ≥ SettleCount → Save
///                        moving    → candidate = capture, count = 0
/// </code>
/// </summary>
public sealed class ChangeDetector
{
    private readonly ChangeDetectionSettings _settings;
    private GrayGrid? _lastSaved;
    private GrayGrid? _candidate;
    private int _settledCount;

    public ChangeDetector(ChangeDetectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public bool IsPending => _candidate is not null;

    /// <summary>Starts from an existing last frame, e.g. when resuming capture into a workspace (FR-C11).</summary>
    public void Seed(GrayGrid lastSaved)
    {
        ArgumentNullException.ThrowIfNull(lastSaved);
        _lastSaved = lastSaved;
        _candidate = null;
        _settledCount = 0;
    }

    public DetectionResult Process(RawImage capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        GrayGrid grid = GrayGrid.FromImage(capture);

        double vsSaved = _lastSaved is null ? 1.0 : AreaChange.ChangedFraction(grid, _lastSaved, _settings.PixelTolerance);
        bool differentFromSaved = vsSaved >= _settings.AreaThreshold;

        if (!differentFromSaved)
        {
            // Unchanged, or a pending change reverted (mouse passed over, controls flashed up).
            _candidate = null;
            _settledCount = 0;
            return new(DetectionDecision.Discard, vsSaved, null, grid);
        }

        if (!_settings.SettleEnabled || _settings.SettleCount <= 0)
            return SaveNow(capture, grid, vsSaved);

        if (_candidate is null)
        {
            _candidate = grid;
            _settledCount = 0;
            return new(DetectionDecision.Pending, vsSaved, null, grid);
        }

        double vsCandidate = AreaChange.ChangedFraction(grid, _candidate, _settings.SettleTolerance);
        _candidate = grid;
        if (vsCandidate >= _settings.SettleAreaThreshold)
        {
            _settledCount = 0; // still moving
            return new(DetectionDecision.Pending, vsSaved, null, grid);
        }

        if (++_settledCount >= _settings.SettleCount)
            return SaveNow(capture, grid, vsSaved);
        return new(DetectionDecision.Pending, vsSaved, null, grid);
    }

    private DetectionResult SaveNow(RawImage capture, GrayGrid grid, double vsSaved)
    {
        _lastSaved = grid;
        _candidate = null;
        _settledCount = 0;
        return new(DetectionDecision.Save, vsSaved, capture, grid);
    }
}
