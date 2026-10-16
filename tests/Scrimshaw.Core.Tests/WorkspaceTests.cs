using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Scrimshaw.Core.Export;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Core.Workspaces;

namespace Scrimshaw.Core.Tests;

/// <summary>A temp folder deleted after the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scrimshaw-tests", Guid.NewGuid().ToString("N"));

    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

/// <summary>
/// Stand-in for the WIC codec. Files hold "WxH"; anything else fails to decode, like a half-written image.
/// </summary>
internal sealed class FakeCodec : IImageCodec
{
    public RawImage Decode(string path)
    {
        string[] parts = File.ReadAllText(path).Split('x');
        if (parts.Length != 2) throw new InvalidDataException("not an image");
        int w = int.Parse(parts[0], CultureInfo.InvariantCulture), h = int.Parse(parts[1], CultureInfo.InvariantCulture);
        return Synthetic.Page(w + h, w, h);
    }

    public void Encode(RawImage image, string path, ImageFormat format, int jpegQuality) =>
        File.WriteAllText(path, $"{image.Width}x{image.Height}");
}

public class AtomicFileTests
{
    [Fact]
    public void Writes_new_file_and_replaces_existing()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        string path = dir.Sub("a.json");

        AtomicFile.WriteAllBytes(path, "one"u8);
        AtomicFile.WriteAllBytes(path, "two"u8);

        Assert.Equal("two", File.ReadAllText(path));
        Assert.False(File.Exists(AtomicFile.TempPathFor(path)));
    }

    [Fact]
    public void Stale_temp_file_is_deleted_when_the_real_file_exists()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        string path = dir.Sub("a.json");
        File.WriteAllText(path, "good");
        File.WriteAllText(AtomicFile.TempPathFor(path), "half-writ");

        AtomicFile.RecoverIfNeeded(path, _ => true);

        Assert.Equal("good", File.ReadAllText(path));
        Assert.False(File.Exists(AtomicFile.TempPathFor(path)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Temp_file_is_promoted_only_when_valid(bool valid)
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        string path = dir.Sub("a.json");
        File.WriteAllText(AtomicFile.TempPathFor(path), "complete");

        AtomicFile.RecoverIfNeeded(path, _ => valid);

        Assert.Equal(valid, File.Exists(path));
    }
}

public class ManifestStoreTests
{
    private static FrameRecord Frame(int seq, params BoxRecord[] boxes) => new()
    {
        Seq = seq,
        File = Workspace.FrameRelativePath(seq, ImageFormat.Png),
        CapturedAt = new DateTimeOffset(2026, 10, 16, 15, 2, 11, 512, TimeSpan.Zero),
        Width = 1920,
        Height = 1080,
        DHash = "a3f09c1b7e22d410",
        Boxes = boxes,
    };

    private static BoxRecord Box(string id, int order, string? label = null) =>
        new() { Id = id, X = 10, Y = 20, W = 300, H = 400, Order = order, Label = label };

    [Fact]
    public void Changes_are_saved_after_the_debounce_delay()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var time = new FakeTimeProvider();
        string path = dir.Sub(Workspace.ManifestFileName);
        using ManifestStore store = ManifestStore.CreateNew(path, time);

        store.AddFrame(Frame(1));
        Assert.True(store.IsDirty);
        Assert.Empty(ScrimshawJson.ReadManifest(path).Frames);

        time.Advance(ManifestStore.DefaultDebounce);

        Assert.False(store.IsDirty);
        Assert.Single(ScrimshawJson.ReadManifest(path).Frames);
    }

    [Fact]
    public void Each_change_restarts_the_debounce()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var time = new FakeTimeProvider();
        string path = dir.Sub(Workspace.ManifestFileName);
        using ManifestStore store = ManifestStore.CreateNew(path, time);

        store.AddFrame(Frame(1));
        time.Advance(TimeSpan.FromMilliseconds(200));
        store.AddFrame(Frame(2));
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Empty(ScrimshawJson.ReadManifest(path).Frames);

        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(2, ScrimshawJson.ReadManifest(path).Frames.Count);
    }

    [Fact]
    public void Dispose_flushes_pending_changes()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        string path = dir.Sub(Workspace.ManifestFileName);
        using (ManifestStore store = ManifestStore.CreateNew(path, new FakeTimeProvider()))
            store.AddFrame(Frame(1));

        Assert.Single(ScrimshawJson.ReadManifest(path).Frames);
    }

    [Fact]
    public void Round_trips_every_field()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        string path = dir.Sub(Workspace.ManifestFileName);
        FrameRecord original = Frame(7, Box("b1", 0, "Mvt. I – Allegro"), Box("b2", 1)) with { Deleted = true };
        using (ManifestStore store = ManifestStore.CreateNew(path, new FakeTimeProvider()))
            store.AddFrame(original);

        using ManifestStore reloaded = ManifestStore.Load(path);
        FrameRecord f = Assert.Single(reloaded.Current.Frames);
        Assert.Equal(original with { Boxes = [] }, f with { Boxes = [] });
        Assert.Equal(original.Boxes, f.Boxes);
        Assert.Equal(8, reloaded.Current.NextSequence);
    }

    [Fact]
    public void Sequence_numbers_are_never_reused()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        using ManifestStore store = ManifestStore.CreateNew(dir.Sub("m.json"), new FakeTimeProvider());

        int a = store.ReserveSequence();
        int b = store.ReserveSequence();
        store.AddFrame(Frame(b));
        int c = store.ReserveSequence();

        Assert.Equal([1, 2, 3], new[] { a, b, c });
    }

    [Fact]
    public void Frames_stay_in_sequence_order()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        using ManifestStore store = ManifestStore.CreateNew(dir.Sub("m.json"), new FakeTimeProvider());
        store.AddFrame(Frame(3));
        store.AddFrame(Frame(1));
        Assert.Equal([1, 3], store.Current.Frames.Select(f => f.Seq));
    }

    [Fact]
    public void Duplicate_frame_is_rejected()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        using ManifestStore store = ManifestStore.CreateNew(dir.Sub("m.json"), new FakeTimeProvider());
        store.AddFrame(Frame(1));
        Assert.Throws<InvalidOperationException>(() => store.AddFrame(Frame(1)));
    }

    [Fact]
    public void Update_replaces_one_frame_and_raises_changed()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        using ManifestStore store = ManifestStore.CreateNew(dir.Sub("m.json"), new FakeTimeProvider());
        store.AddFrame(Frame(1));
        store.AddFrame(Frame(2));
        int changes = 0;
        store.Changed += (_, _) => changes++;

        store.UpdateFrame(2, f => f with { Boxes = [Box("b1", 0)] });

        Assert.Empty(store.Current.Frames[0].Boxes);
        Assert.Single(store.Current.Frames[1].Boxes);
        Assert.Equal(1, changes);
        Assert.Throws<KeyNotFoundException>(() => store.UpdateFrame(99, f => f));
        Assert.Throws<InvalidOperationException>(() => store.UpdateFrame(1, f => f with { Seq = 5 }));
    }

    [Fact]
    public void Update_all_frames_is_one_change()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        using ManifestStore store = ManifestStore.CreateNew(dir.Sub("m.json"), new FakeTimeProvider());
        store.AddFrame(Frame(1));
        store.AddFrame(Frame(2));
        int changes = 0;
        store.Changed += (_, _) => changes++;

        store.UpdateFrames(f => f with { Deleted = true });

        Assert.All(store.Current.Frames, f => Assert.True(f.Deleted));
        Assert.Equal(1, changes);
    }
}

public class WorkspaceTests
{
    [Fact]
    public void Create_builds_the_folder_layout()
    {
        using var dir = new TempDir();
        using Workspace ws = Workspace.Create(dir.Path);

        Assert.True(Workspace.IsWorkspace(dir.Path));
        Assert.True(File.Exists(dir.Sub(Workspace.ManifestFileName)));
        Assert.True(Directory.Exists(ws.ImagesPath));
        Assert.True(Directory.Exists(ws.ThumbsPath));
        Assert.True(Directory.Exists(ws.ExportsPath));
        Assert.True(ws.Recovery.IsClean);
    }

    [Fact]
    public void Create_refuses_a_folder_with_other_files()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(dir.Sub("notes.txt"), "mine");
        Assert.Throws<WorkspaceException>(() => Workspace.Create(dir.Path));
    }

    [Fact]
    public void Open_refuses_a_folder_that_is_not_a_workspace()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        Assert.Throws<WorkspaceException>(() => Workspace.Open(dir.Path, new FakeCodec()));
    }

    [Fact]
    public void Settings_round_trip_and_reopen()
    {
        using var dir = new TempDir();
        using (Workspace ws = Workspace.Create(dir.Path))
        {
            ws.UpdateSettings(s => s with
            {
                IntervalSeconds = 0.5,
                ImageFormat = ImageFormat.Jpeg,
                JpegQuality = 80,
                Target = new CaptureTargetDescriptor { Kind = CaptureTargetKind.Window, WindowProcessName = "chrome", WindowTitle = "V3" },
                Detection = s.Detection with { AreaThreshold = 0.05 },
                Export = s.Export with { Scaling = ExportScaling.FitWidth },
            });
        }

        using Workspace reopened = Workspace.Open(dir.Path, new FakeCodec());
        WorkspaceSettings s = reopened.Settings;
        Assert.Equal(0.5, s.IntervalSeconds);
        Assert.Equal(ImageFormat.Jpeg, s.ImageFormat);
        Assert.Equal(80, s.JpegQuality);
        Assert.Equal("chrome", s.Target!.WindowProcessName);
        Assert.Equal(0.05, s.Detection.AreaThreshold);
        Assert.Equal(ExportScaling.FitWidth, s.Export.Scaling);
        Assert.Contains("\"imageFormat\": \"Jpeg\"", File.ReadAllText(dir.Sub(Workspace.SettingsFileName)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0)]
    public void Interval_below_minimum_is_rejected(double interval)
    {
        using var dir = new TempDir();
        using Workspace ws = Workspace.Create(dir.Path);
        Assert.Throws<ArgumentException>(() => ws.UpdateSettings(s => s with { IntervalSeconds = interval }));
        Assert.Equal(1.0, ws.Settings.IntervalSeconds);
    }

    [Fact]
    public void Image_format_is_fixed_once_frames_exist()
    {
        using var dir = new TempDir();
        using Workspace ws = Workspace.Create(dir.Path);
        ws.Manifest.AddFrame(new FrameRecord { Seq = 1, File = ws.RelativeFramePath(1), CapturedAt = DateTimeOffset.UtcNow, Width = 1, Height = 1, DHash = "0" });
        Assert.Throws<InvalidOperationException>(() => ws.UpdateSettings(s => s with { ImageFormat = ImageFormat.Jpeg }));
    }

    [Fact]
    public void Frame_paths_use_the_workspace_format()
    {
        Assert.Equal("images/frame_00042.png", Workspace.FrameRelativePath(42, ImageFormat.Png));
        Assert.Equal("images/frame_00042.jpg", Workspace.FrameRelativePath(42, ImageFormat.Jpeg));
    }

    [Fact]
    public void Open_recovers_images_the_manifest_missed()
    {
        using var dir = new TempDir();
        var codec = new FakeCodec();
        using (Workspace ws = Workspace.Create(dir.Path))
        {
            // Frame 1 fully recorded; frames 2 and 3 written to disk but the app died before the manifest was saved.
            codec.Encode(Synthetic.Solid(64, 36, 0), ws.FullPath(Recorded(ws, 1)), ImageFormat.Png, 0);
            codec.Encode(Synthetic.Solid(64, 36, 0), Path.Combine(ws.ImagesPath, "frame_00002.png"), ImageFormat.Png, 0);
            codec.Encode(Synthetic.Solid(32, 18, 0), Path.Combine(ws.ImagesPath, "frame_00003.png"), ImageFormat.Png, 0);
            File.WriteAllText(Path.Combine(ws.ImagesPath, "frame_00004.png"), "truncated");
            File.WriteAllText(Path.Combine(ws.ImagesPath, "unrelated.txt"), "ignore me");
        }

        using Workspace reopened = Workspace.Open(dir.Path, codec);

        Assert.Equal([2, 3], reopened.Recovery.RecoveredFrames);
        Assert.Equal(["frame_00004.png"], reopened.Recovery.UnreadableFiles);
        Assert.Empty(reopened.Recovery.MissingFiles);
        Assert.Equal([1, 2, 3], reopened.Manifest.Current.Frames.Select(f => f.Seq));
        FrameRecord f3 = reopened.Manifest.Current.Frames[2];
        Assert.Equal((32, 18), (f3.Width, f3.Height));
        Assert.Equal(16, f3.DHash.Length);
        Assert.Equal(4, reopened.Manifest.Current.NextSequence);
        Assert.False(reopened.Manifest.IsDirty); // recovery was saved
    }

    [Fact]
    public void Open_reports_missing_image_files()
    {
        using var dir = new TempDir();
        using (Workspace ws = Workspace.Create(dir.Path))
            Recorded(ws, 1);

        using Workspace reopened = Workspace.Open(dir.Path, new FakeCodec());
        Assert.Equal([1], reopened.Recovery.MissingFiles);
        Assert.Single(reopened.Manifest.Current.Frames);
    }

    [Fact]
    public void Open_survives_a_crash_mid_manifest_write()
    {
        using var dir = new TempDir();
        using (Workspace ws = Workspace.Create(dir.Path))
            Recorded(ws, 1);
        string manifest = dir.Sub(Workspace.ManifestFileName);
        File.WriteAllText(AtomicFile.TempPathFor(manifest), "{ \"frames\": [ {\"se"); // torn write

        using Workspace reopened = Workspace.Open(dir.Path, new FakeCodec());

        Assert.Single(reopened.Manifest.Current.Frames);
        Assert.False(File.Exists(AtomicFile.TempPathFor(manifest)));
    }

    [Fact]
    public void Open_uses_a_complete_temp_manifest_if_the_swap_never_happened()
    {
        using var dir = new TempDir();
        using (Workspace ws = Workspace.Create(dir.Path))
            Recorded(ws, 1);
        string manifest = dir.Sub(Workspace.ManifestFileName);
        File.Move(manifest, AtomicFile.TempPathFor(manifest));

        using Workspace reopened = Workspace.Open(dir.Path, new FakeCodec());

        Assert.Single(reopened.Manifest.Current.Frames);
        Assert.True(File.Exists(manifest));
    }

    [Fact]
    public void Damaged_manifest_is_reported()
    {
        using var dir = new TempDir();
        Workspace.Create(dir.Path).Dispose();
        File.WriteAllText(dir.Sub(Workspace.ManifestFileName), "{ not json");

        WorkspaceException ex = Assert.Throws<WorkspaceException>(() => Workspace.Open(dir.Path, new FakeCodec()));
        Assert.Contains("damaged", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Newer_schema_is_refused()
    {
        using var dir = new TempDir();
        Workspace.Create(dir.Path).Dispose();
        string settings = dir.Sub(Workspace.SettingsFileName);
        File.WriteAllText(settings, File.ReadAllText(settings).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal));

        Assert.Throws<WorkspaceException>(() => Workspace.Open(dir.Path, new FakeCodec()));
    }

    private static FrameRecord Recorded(Workspace ws, int seq)
    {
        var frame = new FrameRecord { Seq = seq, File = ws.RelativeFramePath(seq), CapturedAt = DateTimeOffset.UtcNow, Width = 64, Height = 36, DHash = "0000000000000000" };
        ws.Manifest.AddFrame(frame);
        return frame;
    }
}
