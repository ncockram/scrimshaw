using System.Diagnostics;
using System.Globalization;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Scrimshaw.Core.Workspaces;

namespace Scrimshaw.App.UiTests;

/// <summary>
/// Phase 5 acceptance: a real video, start to finish, driven only through the app's UI. Needs a browser window
/// showing a YouTube video, full screen and paused at the start; its title goes in SCRIMSHAW_TARGET_TITLE.
/// Skipped otherwise. Playback is started from the test; capture stops with the global hotkey when the window
/// title changes (YouTube autoplay) or after SCRIMSHAW_MAX_SECONDS.
/// </summary>
[Trait("Category", "Desktop")]
public sealed class AcceptanceTests : IDisposable
{
    private readonly UIA3Automation _automation = new();
    private Application? _app;

    public void Dispose()
    {
        if (_app is { HasExited: false }) _app.Kill();
        _app?.Dispose();
        _automation.Dispose();
    }

    [Fact]
    public void Real_video_capture_review_and_export_through_the_ui()
    {
        string? targetTitle = Environment.GetEnvironmentVariable("SCRIMSHAW_TARGET_TITLE");
        if (string.IsNullOrEmpty(targetTitle)) Assert.Skip("Set SCRIMSHAW_TARGET_TITLE to a paused, full-screen video window's title.");
        int maxSeconds = int.TryParse(Environment.GetEnvironmentVariable("SCRIMSHAW_MAX_SECONDS"), out int m) ? m : 300;

        string runDir = Path.Combine(AppExe.RepoRoot(), "TestResults", "ui", "acceptance-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        string ws = Path.Combine(runDir, "workspace");
        Workspace.Create(ws).Dispose();

        var psi = AppExe.StartInfo();
        psi.ArgumentList.Add(ws);
        _app = Application.Launch(psi);
        Window window = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(20))!;
        window.SetForeground();

        // Capture tab: pick the video window and start.
        Find(window, "RefreshTargets").AsButton().Invoke();
        ComboBox target = Find(window, "Target").AsComboBox();
        target.Expand();
        Wait.UntilInputIsProcessed();
        ComboBoxItem item = target.Items.First(i => i.Text.Contains(targetTitle, StringComparison.OrdinalIgnoreCase));
        item.Select();
        target.Collapse();
        Find(window, "StartCapture").AsButton().Invoke();
        Wait.UntilInputIsProcessed();
        Screenshot(window, runDir, "1-capturing.png");

        // Play the video (YouTube: K toggles play/pause), then keep the mouse off it.
        Window browser = _automation.GetDesktop()
            .FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window))
            .First(w => w.Name.Contains(targetTitle, StringComparison.OrdinalIgnoreCase)).AsWindow();
        browser.SetForeground();
        Thread.Sleep(300);
        Keyboard.Type("k");
        Mouse.Position = new System.Drawing.Point(window.BoundingRectangle.Left + 40, window.BoundingRectangle.Top + 40);

        // FR-C14: capture should stop by itself when YouTube moves on to the next video.
        // While capturing, the Start button is collapsed and so absent from the automation tree.
        var clock = Stopwatch.StartNew();
        bool Capturing() => window.FindFirstDescendant(cf => cf.ByAutomationId("StartCapture")) is null;
        while (clock.Elapsed.TotalSeconds < maxSeconds && Capturing()) Thread.Sleep(500);
        bool stoppedByItself = !Capturing();
        if (!stoppedByItself)
        {
            // Fallback: stop with the global hotkey (FR-C9) while the browser has focus.
            browser.SetForeground();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_S);
            for (int i = 0; i < 100 && Capturing(); i++) Thread.Sleep(100);
        }
        browser.SetForeground();
        Thread.Sleep(300);
        Keyboard.Type("k"); // pause whatever autoplay started
        window.SetForeground();
        string status = Find(window, "StatusMessage").Name;
        File.WriteAllText(Path.Combine(runDir, "stop.txt"), $"stopped by itself: {stoppedByItself} after {clock.Elapsed.TotalSeconds:0} s{Environment.NewLine}{status}");
        Assert.True(stoppedByItself, "capture should have stopped when the title changed");
        Assert.Contains("title changed", status, StringComparison.Ordinal);
        string saved = Find(window, "StatSaved").Name;
        Screenshot(window, runDir, "2-stopped.png");
        Assert.True(int.Parse(saved, CultureInfo.CurrentCulture) >= 2, $"only {saved} pages saved");

        // Review: box the first page's music and apply it to the rest.
        Find(window, "TabReview").AsTabItem().Select();
        Thread.Sleep(800);
        var r = Find(window, "Surface").BoundingRectangle;
        Mouse.Drag(new System.Drawing.Point(r.Left + r.Width * 20 / 100, r.Top + r.Height * 10 / 100),
            new System.Drawing.Point(r.Left + r.Width * 80 / 100, r.Top + r.Height * 90 / 100), MouseButton.Left);
        Wait.UntilInputIsProcessed();
        Find(window, "ApplyToFollowing").AsButton().Invoke();
        Wait.UntilInputIsProcessed();
        Screenshot(window, runDir, "3-boxed.png");

        // Export.
        Find(window, "TabExport").AsTabItem().Select();
        Thread.Sleep(800);
        Screenshot(window, runDir, "4-export-preview.png");
        Find(window, "ExportPdf").AsButton().Invoke();
        string result = "";
        for (int i = 0; i < 200 && !result.StartsWith("Saved", StringComparison.Ordinal); i++)
        {
            Thread.Sleep(100);
            result = Find(window, "ExportResult").Name;
        }
        Screenshot(window, runDir, "5-exported.png");
        Assert.StartsWith("Saved", result, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(runDir, "result.txt"), $"{saved} pages saved; {result}");
        window.Close();
    }

    [Fact]
    public void About_window_shows_version_and_licences()
    {
        string runDir = Path.Combine(AppExe.RepoRoot(), "TestResults", "ui", "about-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runDir);
        _app = Application.Launch(AppExe.StartInfo());
        Window window = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(20))!;
        window.SetForeground();
        Screenshot(window, runDir, "main-empty.png");

        Find(window, "About").AsButton().Invoke();
        Window? about = null;
        for (int i = 0; i < 50 && about is null; i++)
        {
            about = window.ModalWindows.FirstOrDefault();
            if (about is null) Thread.Sleep(100);
        }
        Assert.NotNull(about);
        Assert.Contains(about!.FindAllDescendants(), e => (e.Name ?? "").StartsWith("Version 1.1.0", StringComparison.Ordinal));
        Capture.Element(about).ToFile(Path.Combine(runDir, "about.png"));
        Find(about, "AboutClose").AsButton().Invoke();
        window.Close();
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
