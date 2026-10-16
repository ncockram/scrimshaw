using Scrimshaw.Core.Workspaces;

namespace Scrimshaw.Core.Export;

public sealed record ExportItem(FrameRecord Frame, BoxRecord Box);

/// <summary>What goes into the PDF, in order (FR-P1, FR-P2).</summary>
public static class ExportPlanner
{
    /// <summary>
    /// Every box of every frame that isn't deleted, ordered by frame sequence, then by box creation order.
    /// Boxes with no area, or lying entirely outside their frame, are skipped.
    /// </summary>
    public static IReadOnlyList<ExportItem> Plan(IEnumerable<FrameRecord> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        return frames
            .Where(f => !f.Deleted)
            .OrderBy(f => f.Seq)
            .SelectMany(f => f.Boxes
                .Select((b, i) => (Box: b, Index: i))
                .OrderBy(x => x.Box.Order).ThenBy(x => x.Index)
                .Select(x => ClipToFrame(f, x.Box))
                .Where(b => b is not null)
                .Select(b => new ExportItem(f, b!)))
            .ToList();
    }

    /// <summary>The part of <paramref name="box"/> inside the frame, or null if none is.</summary>
    public static BoxRecord? ClipToFrame(FrameRecord frame, BoxRecord box)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(box);
        int x0 = Math.Max(0, box.X), y0 = Math.Max(0, box.Y);
        int x1 = Math.Min(frame.Width, box.X + box.W), y1 = Math.Min(frame.Height, box.Y + box.H);
        if (x1 <= x0 || y1 <= y0) return null;
        return box with { X = x0, Y = y0, W = x1 - x0, H = y1 - y0 };
    }
}
