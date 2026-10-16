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

    /// <summary>
    /// A cleaner version of the page saved last: it has now been on screen longer than the saved version.
    /// Overwrite the last saved frame's image with <see cref="DetectionResult.Image"/> (FR-D8).
    /// </summary>
    Replace,
}

/// <param name="ChangedFraction">This capture vs the last saved frame (1.0 when nothing has been saved yet).</param>
/// <param name="Image">The capture, when <paramref name="Decision"/> is <see cref="DetectionDecision.Save"/> or <see cref="DetectionDecision.Replace"/>.</param>
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
/// Same page, slightly different version (e.g. player controls gone): count how long each version is shown;
///          once another version outlasts the saved one → Replace
/// </code>
/// </summary>
public sealed class ChangeDetector
{
    private readonly ChangeDetectionSettings _settings;
    private GrayGrid? _lastSaved;
    private GrayGrid? _candidate;
    private int _settledCount;

    // "Longest shown" tracking for the page saved last (FR-D8).
    private int _savedTicks;
    private GrayGrid? _variant;
    private int _variantTicks;

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
        _savedTicks = int.MaxValue / 2; // the saved image of a resumed workspace isn't second-guessed
        _variant = null;
        _variantTicks = 0;
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
            if (_settings.KeepLongestShownVersion && _lastSaved is not null && TrackVersion(grid))
            {
                _lastSaved = grid;
                return new(DetectionDecision.Replace, vsSaved, capture, grid);
            }
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
        _savedTicks = _settledCount + 1; // the captures that showed this version while it settled
        _variant = null;
        _variantTicks = 0;
        _lastSaved = grid;
        _candidate = null;
        _settledCount = 0;
        return new(DetectionDecision.Save, vsSaved, capture, grid);
    }

    /// <summary>
    /// Counts this capture towards the saved version or towards an alternative version of the same page.
    /// Returns true when the alternative has now been shown for longer, so it should replace the saved image.
    /// </summary>
    private bool TrackVersion(GrayGrid grid)
    {
        if (AreaChange.ChangedFraction(grid, _lastSaved!, _settings.SettleTolerance) < _settings.SettleAreaThreshold)
        {
            _savedTicks++;
            return false;
        }

        if (_variant is not null && AreaChange.ChangedFraction(grid, _variant, _settings.SettleTolerance) < _settings.SettleAreaThreshold)
        {
            _variantTicks++;
        }
        else
        {
            _variantTicks = 1;
        }
        _variant = grid;

        if (_variantTicks <= _savedTicks) return false;
        _savedTicks = _variantTicks;
        _variant = null;
        _variantTicks = 0;
        return true;
    }
}
