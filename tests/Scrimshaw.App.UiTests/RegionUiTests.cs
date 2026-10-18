using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.Imaging;

namespace Scrimshaw.App.UiTests;

/// <summary>
/// FR-C12 in the real app: drag a music area on the preview of a TestPlayer whose bottom 20% flickers like a
/// performer video, then capture. Desktop only (moves the mouse): coordinate first.
/// </summary>
[Trait("Category", "Desktop")]
public sealed class RegionUiTests : IDisposable
{
    private const int Pages = 3;
    private readonly UIA3Automation _automation = new();
    private Application? _app;
    private Process? _player;

    public void Dispose()
    {
        if (_app is { HasExited: false }) _app.Kill();
        _app?.Dispose();
        if (_player is { HasExited: false }) _player.Kill();
        _player?.Dispose();
        _automation.Dispose();
    }

    [Fact]
    public void A_music_area_drawn_on_the_preview_keeps_detection_on_the_score()
    {
        string runDir = Path.Combine(AppExe.RepoRoot(), "TestResults", "ui", "region-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        string ws = Path.Combine(runDir, "workspace");
        Workspace.Create(ws, new WorkspaceSettings { IntervalSeconds = 0.5 }).Dispose();

        var psi = AppExe.StartInfo();
        psi.ArgumentList.Add(ws);
        _app = Application.Launch(psi);
        Window window = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(20))!;

        // The player starts turning pages at once, so it's started only when the app is ready.
        string title = "Scrimshaw TestPlayer " + Guid.NewGuid().ToString("N")[..8];
        _player = StartPlayer(title, Path.Combine(runDir, "player-log.json"));
        for (int i = 0; i < 100 && _automation.GetDesktop().FindFirstChild(cf => cf.ByName(title)) is null; i++) Thread.Sleep(100);
        window.SetForeground();
        Find(window, "RefreshTargets").AsButton().Invoke();
        ComboBox target = Find(window, "Target").AsComboBox();
        target.Expand();
        target.Items.First(i => i.Text.Contains(title, StringComparison.Ordinal)).Select();
        target.Collapse();
        WaitForName(window, "FrameAreaHeader", h => h.Contains("1280×720", StringComparison.Ordinal));
        Assert.StartsWith("Optional", Find(window, "RegionText").Name, StringComparison.Ordinal);

        // Drag from near the top-left to the right edge, just above the flickering strip.
        Rectangle picture = Find(window, "PreviewImage").BoundingRectangle;
        Mouse.Drag(At(picture, 0.01, 0.01), At(picture, 0.995, 0.78));
        WaitForName(window, "RegionText", t => t.StartsWith("Music area set", StringComparison.Ordinal));
        Mouse.MoveTo(At(window.BoundingRectangle, 0.1, 0.95)); // off the picture for the screenshot
        Thread.Sleep(1200);
        Screenshot(window, runDir, "1-area-on-preview.png");

        Find(window, "StartCapture").AsButton().Invoke();
        for (int i = 0; i < 400 && !_player.HasExited; i++) Thread.Sleep(100);
        Assert.True(_player.HasExited, "the player should have shown every page");
        Thread.Sleep(1500); // let the session notice the window has gone
        if (window.FindFirstDescendant(cf => cf.ByAutomationId("StopCapture")) is { IsOffscreen: false } stop) stop.AsButton().Invoke();
        WaitForName(window, "FrameAreaHeader", h => h.Contains($"{Pages} frames", StringComparison.Ordinal));
        Screenshot(window, runDir, "2-area-on-latest-frame.png");

        window.Close();
        for (int i = 0; i < 100 && !_app.HasExited; i++) Thread.Sleep(100);
        using Workspace reopened = Workspace.Open(ws, new WicImageCodec());
        Assert.Equal(Pages, reopened.Manifest.Current.Frames.Count);
        Assert.All(reopened.Manifest.Current.Frames, f => Assert.Equal((1280, 720), (f.Width, f.Height)));
        Assert.NotNull(reopened.Settings.Region);
        Assert.InRange(reopened.Settings.Region!.H, 0.74, 0.8);
    }

    private static Point At(Rectangle r, double fx, double fy) =>
        new(r.Left + (int)Math.Round(r.Width * fx), r.Top + (int)Math.Round(r.Height * fy));

    private static Process StartPlayer(string title, string log)
    {
        string exe = Path.Combine(AppExe.RepoRoot(), "tools", "Scrimshaw.TestPlayer", "bin", "Debug", "net10.0-windows", "scrimshaw-testplayer.exe");
        if (!File.Exists(exe)) Assert.Skip($"Build the TestPlayer first: {exe} not found.");
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => !s.Primary && s.Bounds.Width >= 1280)
                     ?? System.Windows.Forms.Screen.PrimaryScreen!;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string a in new[]
                 {
                     "--title", title, "--log", log, "--style", "cut", "--pages", Pages.ToString(CultureInfo.InvariantCulture),
                     "--hold", "8", "--turn", "0", "--distraction",
                     "--x", (screen.Bounds.Left + 40).ToString(CultureInfo.InvariantCulture),
                     "--y", (screen.Bounds.Top + 40).ToString(CultureInfo.InvariantCulture),
                     "--w", "1280", "--h", "720",
                 })
            psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    private static string WaitForName(AutomationElement root, string id, Func<string, bool> condition)
    {
        string name = "";
        for (int i = 0; i < 150; i++)
        {
            name = root.FindFirstDescendant(cf => cf.ByAutomationId(id))?.Name ?? "";
            if (condition(name)) return name;
            Thread.Sleep(100);
        }
        throw new TimeoutException($"'{id}' never matched; last value '{name}'");
    }

    private static AutomationElement Find(AutomationElement root, string automationId)
    {
        for (int i = 0; i < 50; i++)
        {
            if (root.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is { } found) return found;
            Thread.Sleep(100);
        }
        throw new InvalidOperationException($"'{automationId}' not found");
    }

    private static void Screenshot(Window window, string dir, string name)
    {
        window.SetForeground();
        Thread.Sleep(400);
        Capture.Element(window).ToFile(Path.Combine(dir, name));
    }
}
