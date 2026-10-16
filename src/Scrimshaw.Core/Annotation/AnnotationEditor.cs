using System.Globalization;
using Scrimshaw.Core.Workspaces;

namespace Scrimshaw.Core.Annotation;

/// <summary>One undoable edit. <see cref="FocusSeq"/> is the frame to show when it's undone or redone.</summary>
public sealed record AnnotationEdit(string Description, int FocusSeq, IReadOnlyList<FrameChange> Changes);

/// <summary>The boxes and deleted flag of one frame, before and after an edit.</summary>
public sealed record FrameChange(int Seq, IReadOnlyList<BoxRecord> BoxesBefore, bool DeletedBefore, IReadOnlyList<BoxRecord> BoxesAfter, bool DeletedAfter);

/// <summary>
/// All box and frame edits (FR-A3–A11), applied straight to the manifest (which saves itself, FR-A6), with
/// one undo/redo history (FR-A7). Undo restores only boxes and the deleted flag, so it never fights with a
/// capture session replacing a frame's image. Use from one thread (the UI).
/// </summary>
public sealed class AnnotationEditor
{
    public const int MaxUndo = 200;

    private readonly ManifestStore _store;
    private readonly LinkedList<AnnotationEdit> _undo = new();
    private readonly Stack<AnnotationEdit> _redo = new();

    public AnnotationEditor(ManifestStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Raised after any edit, undo or redo.</summary>
    public event EventHandler? HistoryChanged;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Last?.Value.Description;
    public string? RedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;

    public FrameRecord Frame(int seq) => _store.Current.Frames.First(f => f.Seq == seq);

    public BoxRecord AddBox(int seq, PixelRect rect)
    {
        FrameRecord frame = Frame(seq);
        PixelRect clipped = BoxGeometry.ClipTo(rect, frame.Width, frame.Height)
            ?? throw new ArgumentException("The box is outside the frame or too small.", nameof(rect));
        BoxRecord box = NewBox(frame.Boxes, clipped, label: null);
        Commit("Add box", seq, [(seq, [.. frame.Boxes, box], frame.Deleted)]);
        return box;
    }

    public void UpdateBox(int seq, string id, PixelRect rect)
    {
        FrameRecord frame = Frame(seq);
        BoxRecord old = Find(frame, id);
        if (old.X == rect.X && old.Y == rect.Y && old.W == rect.W && old.H == rect.H) return;
        BoxRecord moved = old with { X = rect.X, Y = rect.Y, W = rect.W, H = rect.H };
        Commit(old.W == rect.W && old.H == rect.H ? "Move box" : "Resize box", seq,
            [(seq, frame.Boxes.Select(b => b.Id == id ? moved : b).ToList(), frame.Deleted)]);
    }

    public void DeleteBox(int seq, string id)
    {
        FrameRecord frame = Frame(seq);
        Find(frame, id);
        Commit("Delete box", seq, [(seq, frame.Boxes.Where(b => b.Id != id).ToList(), frame.Deleted)]);
    }

    /// <summary>Sets or clears a box's label (FR-A5). Blank means no label.</summary>
    public void SetLabel(int seq, string id, string? label)
    {
        FrameRecord frame = Frame(seq);
        BoxRecord old = Find(frame, id);
        string? clean = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (old.Label == clean) return;
        Commit(clean is null ? "Clear label" : "Label box", seq,
            [(seq, frame.Boxes.Select(b => b.Id == id ? b with { Label = clean } : b).ToList(), frame.Deleted)]);
    }

    /// <summary>Copies the boxes of the nearest earlier frame that has any onto this frame, without labels (FR-A11).</summary>
    /// <returns>Boxes added (0 if no earlier frame has boxes).</returns>
    public int CopyFromPrevious(int seq)
    {
        FrameRecord frame = Frame(seq);
        FrameRecord? source = _store.Current.Frames.LastOrDefault(f => f.Seq < seq && !f.Deleted && f.Boxes.Count > 0);
        if (source is null) return 0;
        List<BoxRecord> boxes = [.. frame.Boxes];
        foreach (BoxRecord b in source.Boxes.OrderBy(b => b.Order))
            if (BoxGeometry.ClipTo(new PixelRect(b.X, b.Y, b.W, b.H), frame.Width, frame.Height) is { } r)
                boxes.Add(NewBox(boxes, r, label: null));
        int added = boxes.Count - frame.Boxes.Count;
        if (added > 0) Commit("Copy boxes from previous frame", seq, [(seq, boxes, frame.Deleted)]);
        return added;
    }

    /// <summary>
    /// Copies this frame's boxes (without labels) onto every later frame that isn't deleted and has no boxes
    /// yet (FR-A11). One undo step.
    /// </summary>
    /// <returns>Frames that received boxes.</returns>
    public int ApplyToFollowing(int seq)
    {
        FrameRecord source = Frame(seq);
        if (source.Boxes.Count == 0) return 0;
        var changes = new List<(int, IReadOnlyList<BoxRecord>, bool)>();
        foreach (FrameRecord f in _store.Current.Frames.Where(f => f.Seq > seq && !f.Deleted && f.Boxes.Count == 0))
        {
            List<BoxRecord> boxes = [];
            foreach (BoxRecord b in source.Boxes.OrderBy(b => b.Order))
                if (BoxGeometry.ClipTo(new PixelRect(b.X, b.Y, b.W, b.H), f.Width, f.Height) is { } r)
                    boxes.Add(NewBox(boxes, r, label: null));
            if (boxes.Count > 0) changes.Add((f.Seq, boxes, f.Deleted));
        }
        if (changes.Count > 0)
            Commit(string.Create(CultureInfo.InvariantCulture, $"Apply boxes to {changes.Count} following frames"), seq, changes);
        return changes.Count;
    }

    /// <summary>Marks a frame deleted or restores it (FR-A9). Its boxes are kept but not exported.</summary>
    public void SetDeleted(int seq, bool deleted)
    {
        FrameRecord frame = Frame(seq);
        if (frame.Deleted == deleted) return;
        Commit(deleted ? "Delete frame" : "Restore frame", seq, [(seq, frame.Boxes, deleted)]);
    }

    /// <returns>The edit that was undone (to show its frame), or null if there was nothing to undo.</returns>
    public AnnotationEdit? Undo()
    {
        if (_undo.Last is not { } node) return null;
        _undo.RemoveLast();
        Apply(node.Value, after: false);
        _redo.Push(node.Value);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return node.Value;
    }

    public AnnotationEdit? Redo()
    {
        if (_redo.Count == 0) return null;
        AnnotationEdit edit = _redo.Pop();
        Apply(edit, after: true);
        Push(edit);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return edit;
    }

    private void Commit(string description, int focus, IReadOnlyList<(int Seq, IReadOnlyList<BoxRecord> Boxes, bool Deleted)> after)
    {
        var bySeq = _store.Current.Frames.ToDictionary(f => f.Seq);
        var edit = new AnnotationEdit(description, focus, after
            .Select(a => new FrameChange(a.Seq, bySeq[a.Seq].Boxes, bySeq[a.Seq].Deleted, a.Boxes, a.Deleted))
            .ToList());
        Apply(edit, after: true);
        Push(edit);
        _redo.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(AnnotationEdit edit, bool after)
    {
        var changes = edit.Changes.ToDictionary(c => c.Seq);
        _store.UpdateFrames(f => changes.TryGetValue(f.Seq, out FrameChange? c)
            ? f with { Boxes = after ? c.BoxesAfter : c.BoxesBefore, Deleted = after ? c.DeletedAfter : c.DeletedBefore }
            : f);
    }

    private void Push(AnnotationEdit edit)
    {
        _undo.AddLast(edit);
        if (_undo.Count > MaxUndo) _undo.RemoveFirst();
    }

    private static BoxRecord Find(FrameRecord frame, string id) =>
        frame.Boxes.FirstOrDefault(b => b.Id == id) ?? throw new KeyNotFoundException($"Box {id} isn't on frame {frame.Seq}.");

    private static BoxRecord NewBox(IReadOnlyList<BoxRecord> existing, PixelRect r, string? label)
    {
        int next = existing.Select(b => int.TryParse(b.Id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0)
            .DefaultIfEmpty(0).Max() + 1;
        int order = existing.Count == 0 ? 0 : existing.Max(b => b.Order) + 1;
        return new BoxRecord
        {
            Id = string.Create(CultureInfo.InvariantCulture, $"b{next}"),
            X = r.X,
            Y = r.Y,
            W = r.W,
            H = r.H,
            Order = order,
            Label = label,
        };
    }
}
