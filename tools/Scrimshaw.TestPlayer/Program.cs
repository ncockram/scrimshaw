// Scrimshaw TestPlayer: a synthetic "sheet-music video" for capture tests.
// Shows generated score pages in a borderless window and turns them on a script, logging exactly when each
// page was fully on screen, and writing a reference PNG of each page for content checks.
//
//   scrimshaw-testplayer --log run.json [--refs dir] [--style cut|half|fade|slide|mixed] [--pages 5]
//                        [--hold 3] [--turn 0.6] [--seed 1] [--x 100 --y 100] [--w 1280 --h 720]
//                        [--title "Scrimshaw TestPlayer"]
// Position and size are physical pixels. The window closes itself after the last page.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Scrimshaw.TestPlayer;

internal sealed record PlayerOptions(
    string Log, string? Refs, string Style, int Pages, double Hold, double Turn, int Seed, int X, int Y, int W, int H, string Title)
{
    public static PlayerOptions Parse(string[] args)
    {
        string? Get(string name)
        {
            int i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        int Int(string n, int d) => Get(n) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : d;
        double Dbl(string n, double d) => Get(n) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : d;
        return new PlayerOptions(
            Get("log") ?? "testplayer-log.json", Get("refs"), Get("style") ?? "mixed", Int("pages", 5),
            Dbl("hold", 3), Dbl("turn", 0.6), Int("seed", 1), Int("x", 100), Int("y", 100), Int("w", 1280), Int("h", 720),
            Get("title") ?? "Scrimshaw TestPlayer");
    }
}

/// <summary>One thing the player shows: a page, or a half-turned page.</summary>
internal sealed record PlayerState(string Id, BitmapSource Image, string TurnIn);

internal sealed record StateLog(string Id, string TurnIn, DateTimeOffset ShownAt, DateTimeOffset HiddenAt, string? Reference);

internal sealed record RunLog(PlayerOptions Options, IReadOnlyList<StateLog> States);

internal static partial class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        PlayerOptions o = PlayerOptions.Parse(args);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var back = new Image { Stretch = Stretch.Fill };
        var front = new Image { Stretch = Stretch.Fill, RenderTransform = new TranslateTransform() };
        foreach (Image img in new[] { back, front })
        {
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            img.SnapsToDevicePixels = true;
        }
        var window = new Window
        {
            Title = o.Title,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = true,
            Background = Brushes.White,
            UseLayoutRounding = true,
            Content = new Grid { ClipToBounds = true, Children = { back, front } },
        };
        window.SourceInitialized += (_, _) =>
            SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, o.X, o.Y, o.W, o.H, 0x0040 /* SWP_SHOWWINDOW */);
        window.Loaded += async (_, _) => await PlayAsync(o, window, back, front);
        return app.Run(window);
    }

    private static async Task PlayAsync(PlayerOptions o, Window window, Image back, Image front)
    {
        List<PlayerState> states = BuildStates(o);
        var log = new List<StateLog>();
        if (o.Refs is not null) Directory.CreateDirectory(o.Refs);

        DateTimeOffset shownAt = default;
        for (int i = 0; i < states.Count; i++)
        {
            PlayerState s = states[i];
            if (i == 0)
            {
                front.Source = s.Image;
            }
            else
            {
                log.Add(LogEntry(states[i - 1], shownAt, DateTimeOffset.UtcNow, o));
                await TurnAsync(s, o, window, back, front);
            }
            await NextFrameAsync();
            shownAt = DateTimeOffset.UtcNow;
            await Task.Delay(TimeSpan.FromSeconds(o.Hold));
        }
        log.Add(LogEntry(states[^1], shownAt, DateTimeOffset.UtcNow, o));

        File.WriteAllText(o.Log, JsonSerializer.Serialize(new RunLog(o, log), Json));
        window.Close();
    }

    private static StateLog LogEntry(PlayerState s, DateTimeOffset shown, DateTimeOffset hidden, PlayerOptions o)
    {
        string? reference = null;
        if (o.Refs is not null)
        {
            reference = Path.GetFullPath(Path.Combine(o.Refs, $"{s.Id}.png"));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(s.Image));
            using FileStream fs = File.Create(reference);
            enc.Save(fs);
        }
        return new StateLog(s.Id, s.TurnIn, shown, hidden, reference);
    }

    private static async Task TurnAsync(PlayerState next, PlayerOptions o, Window window, Image back, Image front)
    {
        var duration = new Duration(TimeSpan.FromSeconds(o.Turn));
        var move = (TranslateTransform)front.RenderTransform;
        switch (next.TurnIn)
        {
            case "fade":
                back.Source = front.Source;
                front.Opacity = 0;
                front.Source = next.Image;
                await AnimateAsync(front, UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration));
                break;
            case "slide":
                back.Source = front.Source;
                front.Source = next.Image;
                back.RenderTransform = new TranslateTransform();
                double width = window.ActualWidth;
                var outgoing = new DoubleAnimation(0, -width, duration);
                ((TranslateTransform)back.RenderTransform).BeginAnimation(TranslateTransform.XProperty, outgoing);
                await AnimateAsync(move, TranslateTransform.XProperty, new DoubleAnimation(width, 0, duration));
                back.RenderTransform = new TranslateTransform();
                break;
            default: // cut, and both steps of a half turn
                front.Source = next.Image;
                break;
        }
        back.Source = null;
    }

    private static Task AnimateAsync(IAnimatable target, DependencyProperty property, AnimationTimeline animation)
    {
        var done = new TaskCompletionSource();
        animation.FillBehavior = FillBehavior.HoldEnd;
        animation.Completed += (_, _) => done.TrySetResult();
        target.BeginAnimation(property, animation);
        return done.Task;
    }

    /// <summary>Waits until WPF has rendered the current visual state.</summary>
    private static Task NextFrameAsync()
    {
        var done = new TaskCompletionSource();
        int frames = 0;
        void OnRendering(object? s, EventArgs e)
        {
            if (++frames < 2) return; // the frame after the change has been composed
            CompositionTarget.Rendering -= OnRendering;
            done.TrySetResult();
        }
        CompositionTarget.Rendering += OnRendering;
        return done.Task;
    }

    private static List<PlayerState> BuildStates(PlayerOptions o)
    {
        var rng = new Random(o.Seed);
        string[] styles = ["cut", "half", "fade", "slide"];
        var states = new List<PlayerState> { new("p1", RenderPage(1, o), "start") };
        BitmapSource previous = states[0].Image;
        for (int p = 2; p <= o.Pages; p++)
        {
            BitmapSource page = RenderPage(p, o);
            string turn = o.Style == "mixed" ? styles[rng.Next(styles.Length)] : o.Style;
            if (turn == "half")
            {
                states.Add(new($"p{p}-top", Composite(page, previous, o), "half"));
                states.Add(new($"p{p}", page, "half"));
            }
            else
            {
                states.Add(new($"p{p}", page, turn));
            }
            previous = page;
        }
        return states;
    }

    /// <summary>A score-like page: four two-stave systems with noteheads, stems, beams and a big page number.</summary>
    private static RenderTargetBitmap RenderPage(int number, PlayerOptions o)
    {
        int w = o.W, h = o.H;
        var rng = new Random(number * 7919 + o.Seed);
        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            var line = new Pen(Brushes.Black, Math.Max(1, h / 540.0));
            var stem = new Pen(Brushes.Black, Math.Max(1.5, h / 400.0));
            double gap = h / 90.0, systemHeight = (h - 80) / 4.0, margin = w * 0.05;
            for (int sys = 0; sys < 4; sys++)
            {
                for (int staff = 0; staff < 2; staff++)
                {
                    double top = 40 + sys * systemHeight + staff * gap * 8;
                    for (int l = 0; l < 5; l++) dc.DrawLine(line, new Point(margin, top + l * gap), new Point(w - margin, top + l * gap));
                    int notes = (int)((w - 2 * margin) / (gap * 3.2));
                    for (int n = 1; n < notes; n++)
                    {
                        double x = margin + n * gap * 3.2, y = top + rng.Next(-3, 12) * gap / 2;
                        dc.DrawEllipse(Brushes.Black, null, new Point(x, y), gap * 0.65, gap * 0.5);
                        bool up = rng.Next(2) == 0;
                        double sx = up ? x + gap * 0.6 : x - gap * 0.6;
                        dc.DrawLine(stem, new Point(sx, y), new Point(sx, up ? y - gap * 3.5 : y + gap * 3.5));
                        if (n % 4 == 0) dc.DrawLine(new Pen(Brushes.Black, gap * 0.5), new Point(sx, up ? y - gap * 3.5 : y + gap * 3.5), new Point(sx + gap * 3.2, up ? y - gap * 3.5 : y + gap * 3.5));
                    }
                }
            }
            var label = new FormattedText($"Page {number}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), h / 30.0, Brushes.Black, 1.0);
            dc.DrawText(label, new Point((w - label.Width) / 2, h - label.Height - 8));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Top half of the new page over the bottom half of the old one.</summary>
    private static RenderTargetBitmap Composite(BitmapSource top, BitmapSource bottom, PlayerOptions o)
    {
        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawImage(bottom, new Rect(0, 0, o.W, o.H));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, o.W, o.H / 2)));
            dc.DrawImage(top, new Rect(0, 0, o.W, o.H));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(o.W, o.H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
