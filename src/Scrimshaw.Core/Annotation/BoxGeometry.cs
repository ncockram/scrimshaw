namespace Scrimshaw.Core.Annotation;

/// <summary>A rectangle in a frame's own pixels (FR-A10).</summary>
public readonly record struct PixelRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

/// <summary>What part of a box the pointer is on.</summary>
public enum BoxHandle
{
    None,
    Move,
    N,
    S,
    E,
    W,
    NE,
    NW,
    SE,
    SW,
}

/// <summary>Pure geometry for drawing and editing boxes; all coordinates are frame pixels.</summary>
public static class BoxGeometry
{
    /// <summary>Boxes smaller than this (either side) are treated as an accidental click.</summary>
    public const int MinSize = 8;

    /// <summary>The box spanned by a drag, clamped to the frame. Null if it's too small to be deliberate.</summary>
    public static PixelRect? FromDrag(double x0, double y0, double x1, double y1, int frameWidth, int frameHeight)
    {
        int left = (int)Math.Round(Math.Clamp(Math.Min(x0, x1), 0, frameWidth));
        int top = (int)Math.Round(Math.Clamp(Math.Min(y0, y1), 0, frameHeight));
        int right = (int)Math.Round(Math.Clamp(Math.Max(x0, x1), 0, frameWidth));
        int bottom = (int)Math.Round(Math.Clamp(Math.Max(y0, y1), 0, frameHeight));
        return right - left < MinSize || bottom - top < MinSize ? null : new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Which handle of <paramref name="r"/> is under (<paramref name="x"/>, <paramref name="y"/>).
    /// <paramref name="grab"/> is the grab distance in frame pixels (screen distance ÷ zoom).
    /// </summary>
    public static BoxHandle HitTest(PixelRect r, double x, double y, double grab)
    {
        bool nearL = Math.Abs(x - r.X) <= grab, nearR = Math.Abs(x - r.Right) <= grab;
        bool nearT = Math.Abs(y - r.Y) <= grab, nearB = Math.Abs(y - r.Bottom) <= grab;
        bool withinX = x >= r.X - grab && x <= r.Right + grab, withinY = y >= r.Y - grab && y <= r.Bottom + grab;
        if (!withinX || !withinY) return BoxHandle.None;
        if (nearT && nearL) return BoxHandle.NW;
        if (nearT && nearR) return BoxHandle.NE;
        if (nearB && nearL) return BoxHandle.SW;
        if (nearB && nearR) return BoxHandle.SE;
        if (nearT) return BoxHandle.N;
        if (nearB) return BoxHandle.S;
        if (nearL) return BoxHandle.W;
        if (nearR) return BoxHandle.E;
        return r.Contains(x, y) ? BoxHandle.Move : BoxHandle.None;
    }

    /// <summary>
    /// <paramref name="original"/> after dragging <paramref name="handle"/> by (<paramref name="dx"/>, <paramref name="dy"/>)
    /// frame pixels. Moves keep the size and stay inside the frame; resizes stop at <see cref="MinSize"/> and the frame edge.
    /// </summary>
    public static PixelRect Apply(PixelRect original, BoxHandle handle, double dx, double dy, int frameWidth, int frameHeight)
    {
        int ix = (int)Math.Round(dx), iy = (int)Math.Round(dy);
        if (handle == BoxHandle.Move)
        {
            int x = Math.Clamp(original.X + ix, 0, Math.Max(0, frameWidth - original.W));
            int y = Math.Clamp(original.Y + iy, 0, Math.Max(0, frameHeight - original.H));
            return original with { X = x, Y = y };
        }

        int left = original.X, top = original.Y, right = original.Right, bottom = original.Bottom;
        if (handle is BoxHandle.W or BoxHandle.NW or BoxHandle.SW) left = Math.Clamp(left + ix, 0, right - MinSize);
        if (handle is BoxHandle.E or BoxHandle.NE or BoxHandle.SE) right = Math.Clamp(right + ix, left + MinSize, frameWidth);
        if (handle is BoxHandle.N or BoxHandle.NW or BoxHandle.NE) top = Math.Clamp(top + iy, 0, bottom - MinSize);
        if (handle is BoxHandle.S or BoxHandle.SW or BoxHandle.SE) bottom = Math.Clamp(bottom + iy, top + MinSize, frameHeight);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>The part of <paramref name="r"/> inside a frame, or null if none of it is (e.g. copying to a smaller frame).</summary>
    public static PixelRect? ClipTo(PixelRect r, int frameWidth, int frameHeight)
    {
        int left = Math.Max(0, r.X), top = Math.Max(0, r.Y);
        int right = Math.Min(frameWidth, r.Right), bottom = Math.Min(frameHeight, r.Bottom);
        return right - left < MinSize || bottom - top < MinSize ? null : new PixelRect(left, top, right - left, bottom - top);
    }
}
