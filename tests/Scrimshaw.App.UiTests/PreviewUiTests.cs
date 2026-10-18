using System.Diagnostics;
using System.Globalization;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.Imaging;

namespace Scrimshaw.App.UiTests;

/// <summary>FR-C13 in the real app, previewing the TestPlayer window (desktop only, coordinate first).</summary>
[Trait("Category", "Desktop")]
public sealed class PreviewUiTests : IDisposable
{
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
    public void Selecting_a_source_previews_it_without_saving()
    {
        string runDir = Path.Combine(AppExe.RepoRoot(), "TestResults", "ui", "preview-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        string ws = Path.Combine(runDir, "workspace");
        Workspace.Create(ws).Dispose();
        string title = "Scrimshaw TestPlayer " + Guid.NewGuid().ToString("N")[..8];
        _player = StartPlayer(title, Path.Combine(runDir, "player-log.json"));

        var psi = AppExe.StartInfo();
        psi.ArgumentList.Add(ws);
        _app = Application.Launch(psi);
        Window window = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(20))!;
        window.SetForeground();

        // Pick the TestPlayer window: the preview starts.
        Window? playerWindow = null;
        for (int i = 0; i < 100 && playerWindow is null; i++)
        {
            playerWindow = _automation.GetDesktop().FindFirstChild(cf => cf.ByName(title))?.AsWindow();
            if (playerWindow is null) Thread.Sleep(100);
        }
        Assert.NotNull(playerWindow);
        Find(window, "RefreshTargets").AsButton().Invoke();
        ComboBox target = Find(window, "Target").AsComboBox();
        target.Expand();
        target.Items.First(i => i.Text.Contains(title, StringComparison.Ordinal)).Select();
        target.Collapse();
        string header = WaitForName(window, "FrameAreaHeader", h => h.Contains("1280×720", StringComparison.Ordinal));
        Assert.Equal("Preview: not saved (1280×720)", header);
        Assert.NotNull(window.FindFirstDescendant(cf => cf.ByAutomationId("PreviewBadge")));
        Thread.Sleep(2500); // a few preview refreshes
        Screenshot(window, runDir, "1-preview.png");
        Assert.Empty(Directory.GetFiles(Path.Combine(ws, "images")));

        // Capture: the area switches to saved frames.
        Find(window, "StartCapture").AsButton().Invoke();
        Thread.Sleep(3000);
        Assert.StartsWith("Latest saved frame", Find(window, "FrameAreaHeader").Name, StringComparison.Ordinal);
        Find(window, "StopCapture").AsButton().Invoke();
        WaitForName(window, "FrameAreaHeader", h => h.Contains("1 frames", StringComparison.Ordinal));
        Screenshot(window, runDir, "2-after-capture.png");

        // Show preview again, then minimise the source.
        Find(window, "ShowPreview").AsButton().Invoke();
        WaitForName(window, "FrameAreaHeader", h => h.StartsWith("Preview", StringComparison.Ordinal));
        playerWindow!.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Minimized);
        string problem = WaitForName(window, "PreviewProblem", p => p.Length > 0);
        Assert.Contains("minimised", problem, StringComparison.Ordinal);
        Screenshot(window, runDir, "3-minimised.png");

        window.Close();
        for (int i = 0; i < 100 && !_app.HasExited; i++) Thread.Sleep(100);
        using Workspace reopened = Workspace.Open(ws, new WicImageCodec());
        Assert.Single(reopened.Manifest.Current.Frames); // only the captured page; previews saved nothing
    }

    private static Process StartPlayer(string title, string log)
    {
        string exe = Path.Combine(AppExe.RepoRoot(), "tools", "Scrimshaw.TestPlayer", "bin", "Debug", "net10.0-windows", "scrimshaw-testplayer.exe");
        if (!File.Exists(exe)) Assert.Skip($"Build the TestPlayer first: {exe} not found.");
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => !s.Primary && s.Bounds.Width >= 1280)
                     ?? System.Windows.Forms.Screen.PrimaryScreen!;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string a in new[]
                 {
                     "--title", title, "--log", log, "--style", "cut", "--pages", "1", "--hold", "60",
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
        for (int i = 0; i < 100; i++)
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
