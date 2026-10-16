// Phase 0 spike: Windows.Graphics.Capture on this machine.
// Usage:
//   CaptureSpike list
//   CaptureSpike monitor <index> [--interval 1] [--count 10] [--cursor] [--out dir]
//   CaptureSpike window <title substring> [...]
//   CaptureSpike all [...]                      (every monitor, stitched into one virtual-desktop image)
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CaptureSpike;
using Windows.Graphics.Capture;

if (args.Length == 0) { Console.WriteLine("usage: list | monitor <i> | window <title> | all  [--interval s] [--count n] [--cursor] [--out dir]"); return 1; }

var monitors = Native.GetMonitors();
if (args[0] == "list")
{
    Console.WriteLine($"GraphicsCaptureSession.IsSupported = {GraphicsCaptureSession.IsSupported()}");
    Console.WriteLine("Monitors (physical pixels):");
    for (int i = 0; i < monitors.Count; i++)
        Console.WriteLine($"  [{i}] {monitors[i].DeviceName} {monitors[i].Bounds} dpi={monitors[i].Dpi} ({monitors[i].Dpi * 100 / 96}%){(monitors[i].Primary ? " primary" : "")}");
    Console.WriteLine("Windows:");
    foreach (var w in Native.GetWindows())
        Console.WriteLine($"  {w.Process,-20} {w.Bounds,-26} {w.Title}");
    return 0;
}

double interval = double.Parse(Opt("--interval") ?? "1", CultureInfo.InvariantCulture);
int count = int.Parse(Opt("--count") ?? "10", CultureInfo.InvariantCulture);
bool cursor = args.Contains("--cursor");
string outDir = Path.GetFullPath(Opt("--out") ?? Path.Combine("out", $"{args[0]}_{DateTime.Now:HHmmss}"));
Directory.CreateDirectory(outDir);

using var d3d = new D3DContext();
var targets = new List<(WgcCapturer Cap, Rect32 Bounds)>();
switch (args[0])
{
    case "monitor":
        var m = monitors[int.Parse(args[1], CultureInfo.InvariantCulture)];
        Console.WriteLine($"Capturing monitor {m.DeviceName} {m.Bounds} dpi={m.Dpi}");
        targets.Add((new WgcCapturer(d3d, WgcCapturer.ForMonitor(m.Handle), cursor), m.Bounds));
        break;
    case "window":
        var w = Native.GetWindows().FirstOrDefault(x => x.Title.Contains(args[1], StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No window title contains '{args[1]}'");
        Console.WriteLine($"Capturing window [{w.Process}] '{w.Title}' {w.Bounds}");
        targets.Add((new WgcCapturer(d3d, WgcCapturer.ForWindow(w.Handle), cursor), w.Bounds));
        break;
    case "all":
        foreach (var mon in monitors)
        {
            Console.WriteLine($"Capturing monitor {mon.DeviceName} {mon.Bounds} dpi={mon.Dpi}");
            targets.Add((new WgcCapturer(d3d, WgcCapturer.ForMonitor(mon.Handle), cursor), mon.Bounds));
        }
        break;
    default:
        throw new ArgumentException(args[0]);
}

// Let the first frames arrive.
await Task.Delay(500);

var proc = Process.GetCurrentProcess();
TimeSpan cpuStart = proc.TotalProcessorTime;
var wall = Stopwatch.StartNew();
var ticks = new List<double>();
var grabMs = new List<double>();
var encodeMs = new List<double>();
using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
for (int i = 1; i <= count; i++)
{
    await timer.WaitForNextTickAsync();
    ticks.Add(wall.Elapsed.TotalMilliseconds);
    if (targets.Any(t => t.Cap.Closed)) { Console.WriteLine("Target closed - stopping."); break; }

    var sw = Stopwatch.StartNew();
    RawImage? img = targets.Count == 1 ? targets[0].Cap.Grab() : Stitch(targets);
    grabMs.Add(sw.Elapsed.TotalMilliseconds);
    if (img is null) { Console.WriteLine($"  tick {i}: no frame yet"); continue; }

    sw.Restart();
    string path = Path.Combine(outDir, $"frame_{i:D5}.png");
    SavePng(img, path);
    encodeMs.Add(sw.Elapsed.TotalMilliseconds);
    Console.WriteLine($"  tick {i}: {img.Width}x{img.Height} grab={grabMs[^1]:F1}ms png={encodeMs[^1]:F1}ms");
}
proc.Refresh();
double cpuPct = (proc.TotalProcessorTime - cpuStart).TotalMilliseconds / wall.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;

var gaps = ticks.Zip(ticks.Skip(1), (a, b) => b - a).ToList();
string summary = string.Join(Environment.NewLine,
    $"mode={args[0]} interval={interval}s captures={ticks.Count} cursor={cursor}",
    $"frames arrived per target: {string.Join(", ", targets.Select(t => t.Cap.FramesArrived))}",
    gaps.Count > 0 ? $"tick gap ms: mean={gaps.Average():F1} min={gaps.Min():F1} max={gaps.Max():F1} (target {interval * 1000:F0})" : "tick gap: n/a",
    grabMs.Count > 0 ? $"grab ms: mean={grabMs.Average():F1} max={grabMs.Max():F1}" : "",
    encodeMs.Count > 0 ? $"png ms: mean={encodeMs.Average():F1} max={encodeMs.Max():F1}" : "",
    $"cpu: {cpuPct:F1}% of all {Environment.ProcessorCount} logical cores",
    $"output: {outDir}");
Console.WriteLine(summary);
File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary);
foreach (var t in targets) t.Cap.Dispose();
return 0;

string? Opt(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static RawImage? Stitch(List<(WgcCapturer Cap, Rect32 Bounds)> targets)
{
    int left = targets.Min(t => t.Bounds.Left), top = targets.Min(t => t.Bounds.Top);
    int right = targets.Max(t => t.Bounds.Right), bottom = targets.Max(t => t.Bounds.Bottom);
    int w = right - left, h = bottom - top;
    var canvas = new byte[w * h * 4];
    foreach (var (cap, b) in targets)
    {
        RawImage? part = cap.Grab();
        if (part is null) return null;
        int cw = Math.Min(part.Width, b.Width), ch = Math.Min(part.Height, b.Height);
        for (int y = 0; y < ch; y++)
            Buffer.BlockCopy(part.Bgra, y * part.Width * 4, canvas, ((b.Top - top + y) * w + (b.Left - left)) * 4, cw * 4);
    }
    return new RawImage(w, h, canvas);
}

static void SavePng(RawImage img, string path)
{
    var src = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgr32, null, img.Bgra, img.Width * 4);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(src));
    using var fs = File.Create(path);
    enc.Save(fs);
}
