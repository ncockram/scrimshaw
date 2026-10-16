using System.Globalization;
using Scrimshaw.Capture.Sources;
using Scrimshaw.Capture.Targets;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Imaging;

namespace Scrimshaw.Harness;

/// <summary>
/// Records every capture tick of a window (no de-duplication) as a sample for <see cref="ReplayCommand"/>.
/// Stops after <c>--max</c> seconds, when the window closes, or when its title changes (e.g. YouTube autoplay).
/// <code>record &lt;outDir&gt; --window "title text" [--interval 1] [--max 900]</code>
/// </summary>
internal static class RecordCommand
{
    public static async Task<int> RunAsync(CommandLine cl)
    {
        string outDir = Path.GetFullPath(cl.Arg(0, "outDir"));
        string match = cl.Get("window") ?? throw new ArgumentException("--window is required");
        double interval = cl.Double("interval", 1);
        double max = cl.Double("max", 900);

        WindowInfo? window = CaptureTargets.GetWindows().FirstOrDefault(w => w.Title.Contains(match, StringComparison.OrdinalIgnoreCase));
        if (window is null)
        {
            Console.Error.WriteLine($"no window title contains '{match}'");
            return 2;
        }
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"recording '{window.Title}' ({window.Bounds}) every {interval}s to {outDir}");

        var codec = new WicImageCodec();
        using IFrameSource source = FrameSources.Create(new WindowTarget(window), includeCursor: false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
        var started = DateTime.UtcNow;
        int n = 0;
        string stopReason = "time limit";
        while (await timer.WaitForNextTickAsync())
        {
            if ((DateTime.UtcNow - started).TotalSeconds > max) break;
            if (source.IsClosed || !CaptureTargets.IsWindowAlive(window.Handle))
            {
                stopReason = "window closed";
                break;
            }
            string title = CaptureTargets.GetWindows().FirstOrDefault(w => w.Handle == window.Handle)?.Title ?? "";
            if (title != window.Title)
            {
                stopReason = $"title changed to '{title}'";
                break;
            }
            if (source.Grab() is not { } image) continue;
            n++;
            codec.Encode(image, Path.Combine(outDir, string.Create(CultureInfo.InvariantCulture, $"frame_{n:D5}.png")), ImageFormat.Png, 0);
            if (n % 30 == 0) Console.WriteLine($"  {n} frames");
        }
        Console.WriteLine($"stopped ({stopReason}) after {n} frames, {(DateTime.UtcNow - started).TotalSeconds:0} s");
        return 0;
    }
}
