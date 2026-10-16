using System.Diagnostics;
using System.Globalization;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Scrimshaw.Core.Detection;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.Imaging;

namespace Scrimshaw.App.UiTests;

/// <summary>
/// End-to-end review workflow in the real app (FR-A1–A11). Moves the real mouse and keyboard, so it only
/// runs on request: <c>dotnet test --project tests/Scrimshaw.App.UiTests</c> after checking the machine is free.
/// Screenshots and workspaces are kept under TestResults/ui for review.
/// </summary>
[Trait("Category", "Desktop")]
public sealed class ReviewUiTests : IDisposable
{
    private static readonly int[] SampleFrames = [7, 32, 62, 92]; // the four clean V1 pages
    private readonly string _runDir;
    private readonly UIA3Automation _automation = new();
    private Application? _app;

    public ReviewUiTests()
    {
        _runDir = Path.Combine(RepoRoot(), "TestResults", "ui", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_runDir);
    }

    public void Dispose()
    {
        if (_app is { HasExited: false }) _app.Kill();
        _app?.Dispose();
        _automation.Dispose();
    }

    [Fact]
    public void Draw_label_apply_to_following_and_reopen()
    {
        string ws = MakeWorkspace("workflow");
        Window window = Launch(ws);
        OpenReviewTab(window);
        AutomationElement surface = Find(window, "Surface");

        // Draw a box over most of the page by dragging on the frame.
        var r = surface.BoundingRectangle;
        var from = new System.Drawing.Point(r.Left + r.Width * 22 / 100, r.Top + r.Height * 12 / 100);
        var to = new System.Drawing.Point(r.Left + r.Width * 78 / 100, r.Top + r.Height * 88 / 100);
        Mouse.Drag(from, to, MouseButton.Left);
        Wait.UntilInputIsProcessed();
        Assert.Single(Find(window, "BoxList").AsListBox().Items);

        // Label it.
        TextBox label = Find(window, "Label").AsTextBox();
        label.Focus();
        label.Text = "Etude No. 45";
        Keyboard.Type(VirtualKeyShort.ENTER);
        Wait.UntilInputIsProcessed();

        // Apply to the following frames, then step through them.
        Find(window, "ApplyToFollowing").AsButton().Invoke();
        Wait.UntilInputIsProcessed();
        Assert.Contains("3 following frames", Find(window, "StatusMessage").Name, StringComparison.Ordinal);
        Screenshot(window, "1-applied-125pct.png");

        Find(window, "NextFrame").AsButton().Invoke();
        Wait.UntilInputIsProcessed();
        Assert.Single(Find(window, "BoxList").AsListBox().Items);
        Assert.StartsWith("Frame 2", Find(window, "FramePosition").Name, StringComparison.Ordinal);
        Screenshot(window, "2-frame2-125pct.png");

        // Same view on a 100% monitor, if there is one.
        MoveToSecondaryMonitor(window);
        Screenshot(window, "3-frame2-100pct.png");

        window.Close();
        Assert.True(_app!.WaitWhileMainHandleIsMissing(TimeSpan.Zero) || WaitForExit(_app), "app didn't close");

        using Workspace reopened = Workspace.Open(ws, new WicImageCodec());
        IReadOnlyList<FrameRecord> frames = reopened.Manifest.Current.Frames;
        Assert.All(frames, f => Assert.Single(f.Boxes));
        Assert.Equal("Etude No. 45", frames[0].Boxes[0].Label);
        Assert.Null(frames[1].Boxes[0].Label);
        BoxRecord b = frames[0].Boxes[0];
        Assert.InRange(b.W, 1920 * 0.3, 1920 * 0.8); // a large box, in frame pixels
        Assert.Equal((b.X, b.Y, b.W, b.H), (frames[3].Boxes[0].X, frames[3].Boxes[0].Y, frames[3].Boxes[0].W, frames[3].Boxes[0].H));
    }

    [Fact]
    public void Killing_the_app_after_an_edit_loses_nothing()
    {
        string ws = MakeWorkspace("crash");
        Window window = Launch(ws);
        OpenReviewTab(window);
        var r = Find(window, "Surface").BoundingRectangle;
        Mouse.Drag(new System.Drawing.Point(r.Left + r.Width / 3, r.Top + r.Height / 3),
            new System.Drawing.Point(r.Left + r.Width * 2 / 3, r.Top + r.Height * 2 / 3), MouseButton.Left);
        Wait.UntilInputIsProcessed();
        Thread.Sleep(1000); // well past the 250 ms save delay

        _app!.Kill();
        WaitForExit(_app);

        using Workspace reopened = Workspace.Open(ws, new WicImageCodec());
        Assert.Single(reopened.Manifest.Current.Frames[0].Boxes);
    }

    private string MakeWorkspace(string name)
    {
        string root = Path.Combine(_runDir, name);
        string samples = Path.Combine(RepoRoot(), "samples", "real", "v1");
        if (!File.Exists(Path.Combine(samples, "frame_00007.png"))) Assert.Skip("samples/real/v1 frames aren't present on this machine.");
        var codec = new WicImageCodec();
        using Workspace ws = Workspace.Create(root);
        foreach (int n in SampleFrames)
        {
            int seq = ws.Manifest.ReserveSequence();
            string source = Path.Combine(samples, string.Create(CultureInfo.InvariantCulture, $"frame_{n:D5}.png"));
            var record = new FrameRecord
            {
                Seq = seq,
                File = ws.RelativeFramePath(seq),
                CapturedAt = DateTimeOffset.Now,
                Width = 1920,
                Height = 1080,
                DHash = DHash.ToHex(DHash.Compute(GrayGrid.FromImage(codec.Decode(source)))),
            };
            File.Copy(source, ws.FullPath(record));
            ws.Manifest.AddFrame(record);
        }
        return root;
    }

    private Window Launch(string workspace)
    {
        string exe = Path.Combine(RepoRoot(), "src", "Scrimshaw.App", "bin", "Debug", "net10.0-windows10.0.26100.0", "Scrimshaw.exe");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add(workspace);
        _app = Application.Launch(psi);
        Window window = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(15)) ?? throw new TimeoutException("main window didn't appear");
        window.SetForeground();
        return window;
    }

    private static void OpenReviewTab(Window window)
    {
        Find(window, "TabReview").AsTabItem().Select();
        Wait.UntilInputIsProcessed();
        Thread.Sleep(500); // first frame loads and fits
    }

    private static AutomationElement Find(AutomationElement root, string automationId)
    {
        AutomationElement? found = null;
        for (int i = 0; i < 50 && found is null; i++)
        {
            found = root.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (found is null) Thread.Sleep(100);
        }
        return found ?? throw new InvalidOperationException($"'{automationId}' not found");
    }

    private void Screenshot(Window window, string name)
    {
        Thread.Sleep(300);
        Capture.Element(window).ToFile(Path.Combine(_runDir, name));
    }

    private static void MoveToSecondaryMonitor(Window window)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var target = screens.FirstOrDefault(s => !s.Primary && s.Bounds.Width >= 1600);
        if (target is null) return;
        window.Patterns.Transform.Pattern.Move(target.Bounds.Left + 40, target.Bounds.Top + 40);
        Thread.Sleep(800);
    }

    private static bool WaitForExit(Application app)
    {
        for (int i = 0; i < 100 && !app.HasExited; i++) Thread.Sleep(100);
        return app.HasExited;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrimshaw.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
