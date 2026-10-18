using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Scrimshaw.Capture.Interop;
using Scrimshaw.Capture.Sources;
using Scrimshaw.Capture.Targets;
using Scrimshaw.Core.Detection;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.Imaging;

namespace Scrimshaw.Capture.Tests;

/// <summary>
/// Real Windows.Graphics.Capture against the TestPlayer window. These open windows, show content on a
/// monitor and move the mouse, so they only run on request (<c>--filter-trait "Category=Desktop"</c>)
/// after checking the machine is free (implementation plan §11). Recordings are kept under TestResults/desktop.
/// </summary>
[Trait("Category", "Desktop")]
[Collection("Desktop")] // desktop tests run one at a time
public sealed partial class DesktopCaptureTests
{
    private const int PlayerWidth = 1280, PlayerHeight = 720;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly WicImageCodec _codec = new();

    private sealed record StateLog(string Id, string TurnIn, DateTimeOffset ShownAt, DateTimeOffset HiddenAt, string? Reference);
    private sealed record RunLog(IReadOnlyList<StateLog> States);

    [Theory]
    [InlineData("cut")]
    [InlineData("half")]
    [InlineData("fade")]
    [InlineData("slide")]
    public async Task Captures_every_page_once_and_no_transitions(string style)
    {
        string runDir = RunDirectory(style);
        string logPath = Path.Combine(runDir, "player-log.json");
        string title = "Scrimshaw TestPlayer " + Guid.NewGuid().ToString("N")[..8];
        using Process player = StartPlayer(title, logPath, Path.Combine(runDir, "refs"), style, pages: 5, hold: 3, turn: 0.6);
        WindowTarget target = await FindWindowAsync(title);

        using Workspace ws = Workspace.Create(Path.Combine(runDir, "workspace"), new WorkspaceSettings { IntervalSeconds = 0.5 });
        CaptureSession session;
        using (IFrameSource source = FrameSources.Create(target, includeCursor: false))
        {
            session = new CaptureSession(ws, source, _codec);
            session.Start();
            await player.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
            await session.StopAsync();
        }

        RunLog log = JsonSerializer.Deserialize<RunLog>(await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken), Json)!;
        var refs = log.States.ToDictionary(s => s.Id, s => GrayGrid.FromImage(_codec.Decode(s.Reference!)));
        var matches = new Dictionary<string, List<int>>();
        var unmatched = new List<string>();
        foreach (FrameRecord f in ws.Manifest.Current.Frames)
        {
            RawImage image = _codec.Decode(ws.FullPath(f));
            Assert.Equal((PlayerWidth, PlayerHeight), (image.Width, image.Height));
            GrayGrid grid = GrayGrid.FromImage(image);
            var scores = refs.Select(r => (r.Key, Change: AreaChange.ChangedFraction(grid, r.Value, 8))).OrderBy(x => x.Change).ToList();
            if (scores[0].Change < 0.01)
                (matches.TryGetValue(scores[0].Key, out var list) ? list : matches[scores[0].Key] = []).Add(f.Seq);
            else
                unmatched.Add(string.Create(CultureInfo.InvariantCulture, $"frame {f.Seq} (closest {scores[0].Key} at {scores[0].Change:P1})"));
        }

        CaptureStats stats = session.Stats;
        string summary = string.Create(CultureInfo.InvariantCulture,
            $"{style}: saved {ws.Manifest.Current.Frames.Count}, states {log.States.Count}, matches [{string.Join(", ", matches.Select(m => $"{m.Key}:{string.Join('/', m.Value)}"))}], " +
            $"unmatched [{string.Join("; ", unmatched)}], drift mean {stats.MeanDriftMs:0.0} ms max {stats.MaxDriftMs:0.0} ms, dropped {stats.Dropped}");
        TestContext.Current.TestOutputHelper?.WriteLine(summary);
        await File.WriteAllTextAsync(Path.Combine(runDir, "result.txt"), summary, TestContext.Current.CancellationToken);

        Assert.Empty(unmatched);
        Assert.All(log.States, s => Assert.True(matches.TryGetValue(s.Id, out var m) && m.Count == 1, $"{s.Id} saved {(matches.TryGetValue(s.Id, out var x) ? x.Count : 0)} times. {summary}"));
        Assert.Equal(0, stats.Dropped);
        Assert.True(stats.MeanDriftMs < 50, summary); // FR-C4: ±10% of 500 ms
    }

    /// <summary>Phase 4 end to end: TestPlayer → capture → a box on every frame → PDF with one crop per page.</summary>
    [Fact]
    public async Task Capture_box_and_export_end_to_end()
    {
        string runDir = RunDirectory("end-to-end");
        string title = "Scrimshaw TestPlayer " + Guid.NewGuid().ToString("N")[..8];
        using Process player = StartPlayer(title, Path.Combine(runDir, "player-log.json"), null, "mixed", pages: 6, hold: 2.5, turn: 0.6);
        WindowTarget target = await FindWindowAsync(title);

        using Workspace ws = Workspace.Create(Path.Combine(runDir, "workspace"), new WorkspaceSettings { IntervalSeconds = 0.5 });
        using (IFrameSource source = FrameSources.Create(target, includeCursor: false))
        {
            var session = new CaptureSession(ws, source, _codec);
            session.Start();
            await player.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
            await session.StopAsync();
        }
        RunLog log = JsonSerializer.Deserialize<RunLog>(await File.ReadAllTextAsync(Path.Combine(runDir, "player-log.json"), TestContext.Current.CancellationToken), Json)!;
        Assert.Equal(log.States.Count, ws.Manifest.Current.Frames.Count);

        // Box the music area of the first frame, label it, and apply it to the rest (as a user would).
        var editor = new Core.Annotation.AnnotationEditor(ws.Manifest);
        editor.AddBox(1, new Core.Annotation.PixelRect(40, 30, PlayerWidth - 80, PlayerHeight - 90));
        editor.SetLabel(1, "b1", "Start");
        editor.ApplyToFollowing(1);

        Export.ExportResult result = Export.PdfExporter.Export(ws, new Core.Export.ExportOptions(), Path.Combine(runDir, "score.pdf"), cancellationToken: TestContext.Current.CancellationToken);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(result.Path);
        var images = pdf.GetPages().SelectMany(p => p.GetImages()).ToList();
        Assert.Equal(log.States.Count, images.Count);
        Assert.All(images, i => Assert.Equal((PlayerWidth - 80, PlayerHeight - 90), (i.WidthInSamples, i.HeightInSamples)));
        Assert.Equal(1, result.Bookmarks);
        TestContext.Current.TestOutputHelper?.WriteLine($"{result.Crops} crops on {result.Pages} pages -> {result.Path}");
    }

    [Fact]
    public async Task Cursor_is_left_out_unless_asked_for()
    {
        string runDir = RunDirectory("cursor");
        string title = "Scrimshaw TestPlayer " + Guid.NewGuid().ToString("N")[..8];
        using Process player = StartPlayer(title, Path.Combine(runDir, "player-log.json"), null, "cut", pages: 1, hold: 15, turn: 0);
        WindowTarget target = await FindWindowAsync(title);
        RectInt32 b = target.Window.Bounds;
        GetCursorPos(out CursorPoint original);
        try
        {
            var changed = new Dictionary<bool, double>();
            foreach (bool includeCursor in new[] { false, true })
            {
                using IFrameSource source = FrameSources.Create(target, includeCursor);
                SetCursorPos(b.Left - 50, b.Top - 50); // well outside the window
                RawImage away = await GrabAfterAsync(source, 700);
                SetCursorPos(b.Left + b.Width / 3, b.Top + b.Height / 3); // over the music
                RawImage over = await GrabAfterAsync(source, 700);
                changed[includeCursor] = AreaChange.ChangedFraction(GrayGrid.FromImage(away), GrayGrid.FromImage(over), 8);
                _codec.Encode(over, Path.Combine(runDir, $"cursor-{(includeCursor ? "included" : "excluded")}.png"), ImageFormat.Png, 0);
            }
            TestContext.Current.TestOutputHelper?.WriteLine($"changed with cursor excluded {changed[false]:P2}, included {changed[true]:P2}");
            Assert.Equal(0, changed[false]);
            Assert.True(changed[true] > 0, "with the cursor included, moving it over the window should change the picture");
        }
        finally
        {
            SetCursorPos(original.X, original.Y);
            if (!player.HasExited) player.Kill();
        }
    }

    /// <summary>FR-C14: the TestPlayer renames itself partway through, like YouTube starting the next video.</summary>
    [Fact]
    public async Task Capture_stops_when_the_window_title_changes()
    {
        string runDir = RunDirectory("title-change");
        string title = "(1) Scrimshaw TestPlayer " + Guid.NewGuid().ToString("N")[..8]; // with a notification-style prefix
        using Process player = StartPlayer(title, Path.Combine(runDir, "player-log.json"), null, "cut", pages: 3, hold: 3, turn: 0, retitleAfter: 5);
        WindowTarget target = await FindWindowAsync(title);
        using Workspace ws = Workspace.Create(Path.Combine(runDir, "workspace"), new WorkspaceSettings { IntervalSeconds = 0.5 });
        using IFrameSource source = FrameSources.Create(target, includeCursor: false);
        var session = new CaptureSession(ws, source, _codec, options: new CaptureSessionOptions(
            () => CaptureTargets.GetWindowTitle(target.Window.Handle), StopOnTitleChange: true));

        var clock = Stopwatch.StartNew();
        session.Start();
        CaptureStopReason reason = await session.Completion.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine($"stopped after {clock.Elapsed.TotalSeconds:0.0} s: {reason} -> '{session.NewTitle}', {ws.Manifest.Current.Frames.Count} frames");

        Assert.Equal(CaptureStopReason.TitleChanged, reason);
        Assert.EndsWith(" - next video", session.NewTitle, StringComparison.Ordinal);
        Assert.InRange(clock.Elapsed.TotalSeconds, 4.5, 8); // retitled at ~5 s, confirmed on the second 0.5 s check
        Assert.Equal(2, ws.Manifest.Current.Frames.Count); // pages 1 and 2 were shown before the retitle
        if (!player.HasExited) player.Kill();
    }

    /// <summary>
    /// FR-C12: the TestPlayer's bottom 20% flickers like a performer video. Watching the whole window, that
    /// strip keeps every page from settling; with the music area set to the rest, each page is saved once, in full.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_music_area_ignores_a_changing_strip_outside_it(bool withRegion)
    {
        string runDir = RunDirectory(withRegion ? "region-on" : "region-off");
        string title = "Scrimshaw TestPlayer " + Guid.NewGuid().ToString("N")[..8];
        string logPath = Path.Combine(runDir, "player-log.json");
        using Process player = StartPlayer(title, logPath, null, "cut", pages: 3, hold: 3, turn: 0, distraction: true);
        WindowTarget target = await FindWindowAsync(title);
        var settings = new WorkspaceSettings
        {
            IntervalSeconds = 0.5,
            Region = withRegion ? new CaptureRegion { X = 0, Y = 0, W = 1, H = 0.8 } : null,
        };
        using Workspace ws = Workspace.Create(Path.Combine(runDir, "workspace"), settings);
        using IFrameSource source = FrameSources.Create(target, includeCursor: false);
        var session = new CaptureSession(ws, source, _codec);
        session.Start();
        for (int i = 0; i < 300 && !player.HasExited; i++) await Task.Delay(100, TestContext.Current.CancellationToken);
        await session.StopAsync();

        CaptureStats stats = session.Stats;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"region={withRegion}: {stats.Attempts} captures, {stats.Saved} saved, {stats.Discarded} skipped, settling={stats.Settling}");
        if (withRegion)
        {
            Assert.Equal(3, ws.Manifest.Current.Frames.Count);
            Assert.All(ws.Manifest.Current.Frames, f => Assert.Equal((PlayerWidth, PlayerHeight), (f.Width, f.Height)));
        }
        else
        {
            Assert.NotEqual(3, ws.Manifest.Current.Frames.Count);
        }
    }

    [Fact]
    public async Task Closing_the_window_stops_capture_as_target_lost()
    {
        string runDir = RunDirectory("target-lost");
        string title = "Scrimshaw TestPlayer " + Guid.NewGuid().ToString("N")[..8];
        using Process player = StartPlayer(title, Path.Combine(runDir, "player-log.json"), null, "cut", pages: 1, hold: 30, turn: 0);
        WindowTarget target = await FindWindowAsync(title);
        using Workspace ws = Workspace.Create(Path.Combine(runDir, "workspace"), new WorkspaceSettings { IntervalSeconds = 0.5 });
        using IFrameSource source = FrameSources.Create(target, includeCursor: false);
        var session = new CaptureSession(ws, source, _codec);
        session.Start();
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        player.Kill();
        CaptureStopReason reason = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStopReason.TargetLost, reason);
        Assert.Single(ws.Manifest.Current.Frames);
    }

    private static async Task<RawImage> GrabAfterAsync(IFrameSource source, int delayMs)
    {
        await Task.Delay(delayMs, TestContext.Current.CancellationToken);
        for (int i = 0; i < 50; i++)
        {
            if (source.Grab() is { } image) return image;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("no frame arrived");
    }

    private static Process StartPlayer(string title, string log, string? refs, string style, int pages, double hold, double turn, double retitleAfter = 0,
        bool distraction = false)
    {
        string exe = Path.Combine(RepoRoot(), "tools", "Scrimshaw.TestPlayer", "bin", "Debug", "net10.0-windows", "scrimshaw-testplayer.exe");
        if (!File.Exists(exe)) Assert.Skip($"Build the TestPlayer first: {exe} not found.");

        // Prefer a secondary monitor so the player doesn't cover the main screen.
        IReadOnlyList<MonitorInfo> monitors = CaptureTargets.GetMonitors();
        MonitorInfo m = monitors.FirstOrDefault(x => !x.IsPrimary && x.Bounds.Width >= PlayerWidth && x.Bounds.Height >= PlayerHeight) ?? monitors[0];
        var args = new List<string>
        {
            "--title", title, "--log", log, "--style", style,
            "--pages", pages.ToString(CultureInfo.InvariantCulture),
            "--hold", hold.ToString(CultureInfo.InvariantCulture),
            "--turn", turn.ToString(CultureInfo.InvariantCulture),
            "--x", (m.Bounds.Left + 40).ToString(CultureInfo.InvariantCulture),
            "--y", (m.Bounds.Top + 40).ToString(CultureInfo.InvariantCulture),
            "--w", PlayerWidth.ToString(CultureInfo.InvariantCulture),
            "--h", PlayerHeight.ToString(CultureInfo.InvariantCulture),
        };
        if (refs is not null) args.AddRange(["--refs", refs]);
        if (retitleAfter > 0) args.AddRange(["--retitle-after", retitleAfter.ToString(CultureInfo.InvariantCulture)]);
        if (distraction) args.Add("--distraction");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    private static async Task<WindowTarget> FindWindowAsync(string title)
    {
        for (int i = 0; i < 100; i++)
        {
            WindowInfo? w = CaptureTargets.GetWindows().FirstOrDefault(x => x.Title == title && x.Bounds.Width == PlayerWidth);
            if (w is not null) return new WindowTarget(w);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"TestPlayer window '{title}' didn't appear");
    }

    private static string RunDirectory(string name)
    {
        string dir = Path.Combine(RepoRoot(), "TestResults", "desktop",
            string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrimshaw.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out CursorPoint point);
}
