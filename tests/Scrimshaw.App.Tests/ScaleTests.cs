using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Scrimshaw.App.ViewModels;
using Scrimshaw.Core.Annotation;
using Scrimshaw.Core.Export;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.Imaging;
using Scrimshaw.TestSupport;

namespace Scrimshaw.App.Tests;

/// <summary>
/// NFR-6: a workspace of 5,000 frames (about an hour at 0.5 s with little de-duplication) stays usable.
/// Limits are generous (several times the measured times) so the test isn't flaky on a busy machine.
/// </summary>
public sealed class ScaleTests : IDisposable
{
    private const int Frames = 5000;
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Five_thousand_frames_stay_responsive()
    {
        using (Workspace seed = Workspace.Create(_dir.Path, time: new FakeTimeProvider()))
        {
            for (int seq = 1; seq <= Frames; seq++)
                seed.Manifest.AddFrame(new FrameRecord
                {
                    Seq = seq, File = seed.RelativeFramePath(seq), CapturedAt = DateTimeOffset.UnixEpoch.AddSeconds(seq),
                    Width = 1920, Height = 1080, DHash = "0",
                });
        }

        var sw = Stopwatch.StartNew();
        using Workspace ws = Workspace.Open(_dir.Path, new WicImageCodec());
        Limit(sw, 3000, "open the workspace");
        Assert.Equal(Frames, ws.Manifest.Current.Frames.Count);

        var ui = new QueuedUi();
        sw.Restart();
        using var review = new ReviewViewModel(ws, new FakeThumbnails(), ui);
        Limit(sw, 3000, "build the review list");
        Assert.Equal(Frames, review.Frames.Count);

        sw.Restart();
        review.AddBox(new PixelRect(100, 100, 1700, 800));
        Limit(sw, 300, "draw a box");

        sw.Restart();
        for (int i = 0; i < 50; i++) review.NextFrameCommand.Execute(null);
        Limit(sw, 1000, "step through 50 frames");

        review.SelectedFrame = review.Frames[0];
        sw.Restart();
        review.ApplyToFollowingCommand.Execute(null);
        Limit(sw, 3000, "apply boxes to 4,999 frames");
        Assert.All(ws.Manifest.Current.Frames, f => Assert.Single(f.Boxes));

        sw.Restart();
        review.UndoCommand.Execute(null);
        Limit(sw, 3000, "undo that in one step");

        sw.Restart();
        ws.Manifest.Flush();
        Limit(sw, 2000, "save the manifest");

        review.RedoCommand.Execute(null);
        sw.Restart();
        var (items, layout) = Export.PdfExporter.Plan(ws.Manifest.Current.Frames, new ExportOptions());
        Limit(sw, 2000, "plan and lay out 5,000 crops");
        Assert.Equal(Frames, items.Count);
        Assert.True(layout.PageCount > 1000);
    }

    private static void Limit(Stopwatch sw, int ms, string what) =>
        Assert.True(sw.ElapsedMilliseconds < ms, $"{what} took {sw.ElapsedMilliseconds} ms (limit {ms} ms)");
}
