namespace Scrimshaw.Core.Detection;

/// <summary>
/// How much of an image changed: the fraction of <see cref="GrayGrid"/> cells whose brightness moved by
/// more than a tolerance. A page turn changes a large area; noise, a passing cursor or a focus change
/// changes little or nothing.
/// </summary>
public static class AreaChange
{
    /// <returns>0–1. Grids of different shapes (the source was resized) count as fully changed.</returns>
    public static double ChangedFraction(GrayGrid a, GrayGrid b, int tolerance)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (!a.SameShapeAs(b)) return 1.0;

        float[] ca = a.Cells, cb = b.Cells;
        int changed = 0;
        for (int i = 0; i < ca.Length; i++)
            if (Math.Abs(ca[i] - cb[i]) > tolerance) changed++;
        return (double)changed / ca.Length;
    }
}
