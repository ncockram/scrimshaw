using System.Globalization;
using System.Text.RegularExpressions;
using Scrimshaw.Core.Detection;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Imaging;

namespace Scrimshaw.Harness;

/// <summary>
/// Feeds a recorded frame sequence through <see cref="ChangeDetector"/> and, given labels, scores the result:
/// pages missed, duplicate saves, and transition frames saved.
/// </summary>
internal static partial class ReplayCommand
{
    public static int Run(CommandLine cl)
    {
        string dir = cl.Arg(0, "framesDir");
        var defaults = new ChangeDetectionSettings();
        var settings = new ChangeDetectionSettings
        {
            AreaThreshold = cl.Double("threshold", defaults.AreaThreshold),
            PixelTolerance = cl.Int("tol", defaults.PixelTolerance),
            SettleCount = cl.Int("settle", defaults.SettleCount),
            SettleEnabled = cl.Int("settle", defaults.SettleCount) > 0,
            SettleTolerance = cl.Int("settle-tol", defaults.SettleTolerance),
            SettleAreaThreshold = cl.Double("settle-area", defaults.SettleAreaThreshold),
            KeepLongestShownVersion = !cl.Has("no-longest"),
        };
        bool verbose = cl.Has("verbose");
        string? labelsPath = cl.Get("labels") ?? (File.Exists(Path.Combine(dir, "labels.json")) ? Path.Combine(dir, "labels.json") : null);

        var frames = Directory.EnumerateFiles(dir)
            .Select(f => (Path: f, Match: FrameName().Match(Path.GetFileName(f))))
            .Where(x => x.Match.Success)
            .Select(x => (x.Path, Number: int.Parse(x.Match.Groups[1].Value, CultureInfo.InvariantCulture)))
            .OrderBy(x => x.Number)
            .ToList();
        if (frames.Count == 0)
        {
            Console.Error.WriteLine($"no frame_*.png/jpg files in {dir}");
            return 2;
        }

        Console.WriteLine($"replay {dir}: {frames.Count} frames");
        Console.WriteLine($"  {settings}");
        var codec = new WicImageCodec();
        var detector = new ChangeDetector(settings);
        Labels? labels = labelsPath is null ? null : Labels.Load(labelsPath);
        CaptureRegion? region = (cl.Get("region") ?? labels?.Region) is { } regionText ? CaptureRegion.Parse(regionText) : null;
        if (region is not null) Console.WriteLine($"  music area (FR-C12): {region}");
        var saved = new List<int>();
        foreach (var (path, number) in frames)
        {
            RawImage frame = codec.Decode(path);
            if (region is not null) frame = ImageOps.Crop(frame, region.ToPixels(frame.Width, frame.Height));
            DetectionResult r = detector.Process(frame);
            if (r.Decision == DetectionDecision.Save) saved.Add(number);
            if (r.Decision == DetectionDecision.Replace && saved.Count > 0) saved[^1] = number; // the page's image is now this frame
            if (verbose || r.Decision is DetectionDecision.Save or DetectionDecision.Replace)
                Console.WriteLine($"  {number,5}: {r.Decision,-7} changed={r.ChangedFraction * 100,6:F2}%");
        }
        Console.WriteLine($"saved {saved.Count}: [{string.Join(", ", saved)}]");

        if (labels is null) return 0;
        return Score(labels, saved) ? 0 : 1;
    }

    /// <returns>True when every page was saved exactly once and no transition frame was saved.</returns>
    private static bool Score(Labels labels, List<int> saved)
    {
        var credited = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<int>();
        var transitions = new List<int>();
        int ignored = 0;

        foreach (int n in saved)
        {
            PageLabel? page = labels.Pages.FirstOrDefault(p => n >= p.First && n <= p.Last);
            if (page is not null)
            {
                if (!credited.Add(page.Id)) duplicates.Add(n);
            }
            else if (labels.Ignore?.Any(r => n >= r.First && n <= r.Last) == true)
            {
                ignored++;
            }
            else
            {
                transitions.Add(n);
            }
        }

        var missed = labels.Pages.Where(p => !credited.Contains(p.Id)).Select(p => p.Id).ToList();
        bool ok = missed.Count == 0 && duplicates.Count == 0 && transitions.Count == 0;
        Console.WriteLine($"score: pages {credited.Count}/{labels.Pages.Count}" +
            $" | missed [{string.Join(", ", missed)}]" +
            $" | duplicate saves [{string.Join(", ", duplicates)}]" +
            $" | transition frames saved [{string.Join(", ", transitions)}]" +
            $" | saves in ignored ranges {ignored}" +
            $" => {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    [GeneratedRegex(@"^frame_(\d+)\.(png|jpg)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrameName();
}
