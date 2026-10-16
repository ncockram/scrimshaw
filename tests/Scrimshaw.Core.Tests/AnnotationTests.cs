using Microsoft.Extensions.Time.Testing;
using Scrimshaw.Core.Annotation;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.TestSupport;

namespace Scrimshaw.Core.Tests;

public class BoxGeometryTests
{
    [Fact]
    public void Drag_in_any_direction_gives_a_normalised_box()
    {
        Assert.Equal(new PixelRect(10, 20, 100, 50), BoxGeometry.FromDrag(110, 70, 10, 20, 1000, 800));
    }

    [Fact]
    public void Drag_is_clamped_to_the_frame()
    {
        Assert.Equal(new PixelRect(0, 0, 50, 40), BoxGeometry.FromDrag(-30, -10, 50, 40, 1000, 800));
        Assert.Equal(new PixelRect(900, 700, 100, 100), BoxGeometry.FromDrag(900, 700, 1200, 900, 1000, 800));
    }

    [Fact]
    public void Tiny_drag_is_a_click_not_a_box()
    {
        Assert.Null(BoxGeometry.FromDrag(10, 10, 14, 60, 1000, 800));
    }

    [Theory]
    [InlineData(100, 100, BoxHandle.NW)]
    [InlineData(300, 100, BoxHandle.NE)]
    [InlineData(100, 200, BoxHandle.SW)]
    [InlineData(300, 200, BoxHandle.SE)]
    [InlineData(200, 101, BoxHandle.N)]
    [InlineData(200, 199, BoxHandle.S)]
    [InlineData(102, 150, BoxHandle.W)]
    [InlineData(298, 150, BoxHandle.E)]
    [InlineData(200, 150, BoxHandle.Move)]
    [InlineData(50, 150, BoxHandle.None)]
    public void Hit_testing_finds_handles_then_the_inside(double x, double y, BoxHandle expected)
    {
        Assert.Equal(expected, BoxGeometry.HitTest(new PixelRect(100, 100, 200, 100), x, y, grab: 4));
    }

    [Fact]
    public void Moving_keeps_size_and_stays_in_the_frame()
    {
        var r = new PixelRect(100, 100, 200, 100);
        Assert.Equal(new PixelRect(150, 80, 200, 100), BoxGeometry.Apply(r, BoxHandle.Move, 50, -20, 1000, 800));
        Assert.Equal(new PixelRect(800, 700, 200, 100), BoxGeometry.Apply(r, BoxHandle.Move, 5000, 5000, 1000, 800));
        Assert.Equal(new PixelRect(0, 0, 200, 100), BoxGeometry.Apply(r, BoxHandle.Move, -5000, -5000, 1000, 800));
    }

    [Fact]
    public void Resizing_moves_only_the_dragged_edges()
    {
        var r = new PixelRect(100, 100, 200, 100);
        Assert.Equal(new PixelRect(100, 100, 250, 130), BoxGeometry.Apply(r, BoxHandle.SE, 50, 30, 1000, 800));
        Assert.Equal(new PixelRect(80, 100, 220, 100), BoxGeometry.Apply(r, BoxHandle.W, -20, 99, 1000, 800));
        Assert.Equal(new PixelRect(100, 60, 200, 140), BoxGeometry.Apply(r, BoxHandle.N, 99, -40, 1000, 800));
    }

    [Fact]
    public void Resizing_stops_at_the_minimum_size_and_the_frame_edge()
    {
        var r = new PixelRect(100, 100, 200, 100);
        Assert.Equal(BoxGeometry.MinSize, BoxGeometry.Apply(r, BoxHandle.E, -5000, 0, 1000, 800).W);
        Assert.Equal(1000, BoxGeometry.Apply(r, BoxHandle.E, 5000, 0, 1000, 800).Right);
        Assert.Equal(0, BoxGeometry.Apply(r, BoxHandle.NW, -5000, -5000, 1000, 800).X);
    }

    [Fact]
    public void Clipping_to_a_smaller_frame()
    {
        Assert.Equal(new PixelRect(900, 0, 100, 50), BoxGeometry.ClipTo(new PixelRect(900, 0, 300, 50), 1000, 800));
        Assert.Null(BoxGeometry.ClipTo(new PixelRect(1200, 0, 300, 50), 1000, 800));
    }
}

public sealed class AnnotationEditorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Workspace _ws;
    private readonly AnnotationEditor _editor;

    public AnnotationEditorTests()
    {
        _ws = Workspace.Create(_dir.Path, time: new FakeTimeProvider());
        foreach (int seq in new[] { 1, 2, 3, 4 })
            _ws.Manifest.AddFrame(new FrameRecord { Seq = seq, File = _ws.RelativeFramePath(seq), CapturedAt = DateTimeOffset.UnixEpoch, Width = 1000, Height = 800, DHash = "0" });
        _editor = new AnnotationEditor(_ws.Manifest);
    }

    public void Dispose()
    {
        _ws.Dispose();
        _dir.Dispose();
    }

    private IReadOnlyList<BoxRecord> Boxes(int seq) => _editor.Frame(seq).Boxes;

    [Fact]
    public void Add_move_label_and_delete_a_box()
    {
        BoxRecord a = _editor.AddBox(1, new PixelRect(10, 20, 300, 200));
        BoxRecord b = _editor.AddBox(1, new PixelRect(10, 300, 300, 200));
        Assert.Equal(("b1", 0, "b2", 1), (a.Id, a.Order, b.Id, b.Order));

        _editor.UpdateBox(1, "b1", new PixelRect(15, 25, 300, 200));
        _editor.SetLabel(1, "b1", "  Mvt. I  ");
        Assert.Equal((15, 25, "Mvt. I"), (Boxes(1)[0].X, Boxes(1)[0].Y, Boxes(1)[0].Label));

        _editor.DeleteBox(1, "b1");
        Assert.Equal(["b2"], Boxes(1).Select(x => x.Id));
        Assert.Equal("b3", _editor.AddBox(1, new PixelRect(0, 0, 50, 50)).Id); // ids aren't reused
    }

    [Fact]
    public void Blank_label_clears_it()
    {
        _editor.AddBox(1, new PixelRect(10, 20, 300, 200));
        _editor.SetLabel(1, "b1", "x");
        _editor.SetLabel(1, "b1", "   ");
        Assert.Null(Boxes(1)[0].Label);
    }

    [Fact]
    public void Undo_and_redo_walk_the_history_and_report_the_frame()
    {
        _editor.AddBox(1, new PixelRect(10, 20, 300, 200));
        _editor.AddBox(2, new PixelRect(10, 20, 300, 200));
        _editor.UpdateBox(2, "b1", new PixelRect(50, 50, 300, 200));

        Assert.Equal(("Move box", 2), (_editor.Undo()!.Description, _editor.Undo()!.FocusSeq));
        Assert.Empty(Boxes(2));
        Assert.Single(Boxes(1));
        Assert.Equal(2, _editor.Redo()!.FocusSeq);
        Assert.Single(Boxes(2));
        Assert.True(_editor.CanRedo);

        _editor.AddBox(3, new PixelRect(10, 20, 300, 200)); // a new edit clears redo
        Assert.False(_editor.CanRedo);
    }

    [Fact]
    public void Undo_with_nothing_to_undo_is_harmless()
    {
        Assert.Null(_editor.Undo());
        Assert.Null(_editor.Redo());
    }

    [Fact]
    public void Apply_to_following_fills_empty_later_frames_in_one_step()
    {
        _editor.AddBox(1, new PixelRect(10, 20, 300, 200));
        _editor.SetLabel(1, "b1", "Mvt. I");
        _editor.AddBox(3, new PixelRect(500, 500, 100, 100)); // already boxed: left alone
        _editor.SetDeleted(4, true);                            // deleted: left alone

        Assert.Equal(1, _editor.ApplyToFollowing(1));
        Assert.Equal((10, 20, 300, 200), (Boxes(2)[0].X, Boxes(2)[0].Y, Boxes(2)[0].W, Boxes(2)[0].H));
        Assert.Null(Boxes(2)[0].Label); // labels aren't copied
        Assert.Equal(500, Boxes(3)[0].X);
        Assert.Empty(Boxes(4));

        Assert.Equal("Apply boxes to 1 following frames", _editor.Undo()!.Description);
        Assert.Empty(Boxes(2));
    }

    [Fact]
    public void Copy_from_previous_uses_the_nearest_earlier_boxed_frame()
    {
        _editor.AddBox(1, new PixelRect(10, 20, 300, 200));
        _editor.AddBox(1, new PixelRect(10, 300, 300, 200));
        Assert.Equal(2, _editor.CopyFromPrevious(3)); // frame 2 has no boxes, so frame 1 is used
        Assert.Equal(["b1", "b2"], Boxes(3).Select(b => b.Id));
        Assert.Equal(0, _editor.CopyFromPrevious(1));
    }

    [Fact]
    public void Copied_boxes_are_clipped_to_a_smaller_frame()
    {
        _ws.Manifest.UpdateFrame(2, f => f with { Width = 200, Height = 100 });
        _editor.AddBox(1, new PixelRect(150, 50, 300, 200));
        _editor.ApplyToFollowing(1);
        Assert.Equal((150, 50, 50, 50), (Boxes(2)[0].X, Boxes(2)[0].Y, Boxes(2)[0].W, Boxes(2)[0].H));
    }

    [Fact]
    public void Deleting_a_frame_keeps_its_boxes_and_can_be_undone()
    {
        _editor.AddBox(2, new PixelRect(10, 20, 300, 200));
        _editor.SetDeleted(2, true);
        Assert.True(_editor.Frame(2).Deleted);
        Assert.Single(Boxes(2));
        _editor.Undo();
        Assert.False(_editor.Frame(2).Deleted);
    }

    [Fact]
    public void Undo_restores_boxes_without_undoing_a_replaced_image()
    {
        _editor.AddBox(1, new PixelRect(10, 20, 300, 200));
        _ws.Manifest.UpdateFrame(1, f => f with { DHash = "replaced" }); // capture swapped in a cleaner image
        _editor.Undo();
        Assert.Equal("replaced", _editor.Frame(1).DHash);
        Assert.Empty(Boxes(1));
    }

    [Fact]
    public void Edits_reach_the_manifest_on_disk()
    {
        _editor.AddBox(1, new PixelRect(10, 20, 300, 200));
        _ws.Manifest.Flush();
        Assert.Single(ScrimshawJson.ReadManifest(Path.Combine(_ws.Root, Workspace.ManifestFileName)).Frames[0].Boxes);
    }

    [Fact]
    public void History_is_capped()
    {
        for (int i = 0; i < AnnotationEditor.MaxUndo + 10; i++)
            _editor.AddBox(1, new PixelRect(0, 0, 20, 20));
        int undone = 0;
        while (_editor.Undo() is not null) undone++;
        Assert.Equal(AnnotationEditor.MaxUndo, undone);
    }
}
