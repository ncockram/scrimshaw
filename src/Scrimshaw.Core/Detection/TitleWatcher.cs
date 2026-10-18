using System.Text.RegularExpressions;

namespace Scrimshaw.Core.Detection;

/// <summary>
/// Decides when a captured window's title has really changed (FR-C14), as when YouTube moves on to the next
/// video. A leading notification count such as "(86) " is ignored, because it can change mid-video, and a new
/// title must be seen on <see cref="Confirmations"/> consecutive checks, so a momentary flicker doesn't count.
/// </summary>
public sealed partial class TitleWatcher
{
    public const int DefaultConfirmations = 2;

    private string? _candidate;
    private int _seen;

    /// <param name="startTitle">The title when capture started; changes are measured against it.</param>
    public TitleWatcher(string? startTitle, int confirmations = DefaultConfirmations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(confirmations);
        Reference = Normalize(startTitle);
        Confirmations = confirmations;
    }

    /// <summary>The normalised start title.</summary>
    public string Reference { get; }

    public int Confirmations { get; }

    /// <summary>The new title (as the window showed it) once a change has been confirmed.</summary>
    public string? ChangedTo { get; private set; }

    /// <summary>Feeds one check of the title.</summary>
    /// <returns>True once the change is confirmed (and from then on).</returns>
    public bool Observe(string? title)
    {
        if (ChangedTo is not null) return true;
        if (string.IsNullOrWhiteSpace(title))
        {
            // No title (e.g. the window is closing): not evidence of a new video. Closing is handled elsewhere.
            _candidate = null;
            _seen = 0;
            return false;
        }

        string normalized = Normalize(title);
        if (normalized == Reference)
        {
            _candidate = null;
            _seen = 0;
            return false;
        }

        if (normalized == _candidate)
        {
            _seen++;
        }
        else
        {
            _candidate = normalized;
            _seen = 1;
        }

        if (_seen < Confirmations) return false;
        ChangedTo = title.Trim();
        return true;
    }

    /// <summary>The title without a leading "(number)" notification count, trimmed.</summary>
    public static string Normalize(string? title) =>
        title is null ? "" : NotificationCount().Replace(title, "").Trim();

    [GeneratedRegex(@"^\s*\(\d+\)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex NotificationCount();
}
