using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scrimshaw.Core.Annotation;
using Scrimshaw.Core.Imaging;

namespace Scrimshaw.App.Views;

/// <summary>
/// Sits over an <c>Image</c> with <c>Stretch="Uniform"</c> and shows the music area (FR-C12): outside it is dimmed
/// and the area is outlined. When <see cref="CanDraw"/> is on, dragging marks a new area and runs
/// <see cref="DrawnCommand"/> with the <see cref="CaptureRegion"/> (a click, or a sliver, is ignored).
/// </summary>
public sealed class RegionOverlay : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(RegionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RegionProperty = DependencyProperty.Register(
        nameof(Region), typeof(CaptureRegion), typeof(RegionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CanDrawProperty = DependencyProperty.Register(
        nameof(CanDraw), typeof(bool), typeof(RegionOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnCanDrawChanged));

    public static readonly DependencyProperty DrawnCommandProperty = DependencyProperty.Register(
        nameof(DrawnCommand), typeof(ICommand), typeof(RegionOverlay));

    private static readonly Brush Outside = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0x20, 0x20, 0x20)));
    private static readonly Pen AreaPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x27, 0xAE, 0x60)), 2));
    private static readonly Pen DraftPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)), 2) { DashStyle = DashStyles.Dash });
    private static readonly Brush LabelBack = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, 0x27, 0xAE, 0x60)));

    private Point? _dragStart;
    private Point _dragNow;

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public CaptureRegion? Region
    {
        get => (CaptureRegion?)GetValue(RegionProperty);
        set => SetValue(RegionProperty, value);
    }

    public bool CanDraw
    {
        get => (bool)GetValue(CanDrawProperty);
        set => SetValue(CanDrawProperty, value);
    }

    public ICommand? DrawnCommand
    {
        get => (ICommand?)GetValue(DrawnCommandProperty);
        set => SetValue(DrawnCommandProperty, value);
    }

    /// <summary>The source's size in pixels, or null before there's a picture.</summary>
    private Size? PixelSize => Source switch
    {
        BitmapSource b when b.PixelWidth > 0 && b.PixelHeight > 0 => new Size(b.PixelWidth, b.PixelHeight),
        { Width: > 0, Height: > 0 } s => new Size(s.Width, s.Height),
        _ => null,
    };

    /// <summary>Where the picture is drawn: fitted and centred, as <c>Stretch="Uniform"</c> does.</summary>
    private Rect? PictureRect
    {
        get
        {
            if (PixelSize is not { } px || ActualWidth <= 0 || ActualHeight <= 0) return null;
            double scale = Math.Min(ActualWidth / px.Width, ActualHeight / px.Height);
            double w = px.Width * scale, h = px.Height * scale;
            return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new OverlayPeer(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        // A transparent fill makes the whole area hit-testable for dragging.
        if (CanDraw) drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (PictureRect is not { } picture) return;

        if (_dragStart is { } start)
        {
            drawingContext.DrawRectangle(null, DraftPen, Clamp(new Rect(start, _dragNow), picture));
            return;
        }
        if (Region is not { } region) return;

        var area = new Rect(picture.X + region.X * picture.Width, picture.Y + region.Y * picture.Height,
            region.W * picture.Width, region.H * picture.Height);
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(picture), new RectangleGeometry(area));
        drawingContext.DrawGeometry(Outside, null, outside);
        drawingContext.DrawRectangle(null, AreaPen, area);

        var label = new FormattedText("Music area", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var labelRect = new Rect(area.X, Math.Max(picture.Y, area.Y - label.Height - 4), label.Width + 8, label.Height + 4);
        drawingContext.DrawRectangle(LabelBack, null, labelRect);
        drawingContext.DrawText(label, new Point(labelRect.X + 4, labelRect.Y + 2));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!CanDraw || PictureRect is null) return;
        _dragStart = _dragNow = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is null) return;
        _dragNow = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragStart is not { } start) return;
        _dragStart = null;
        ReleaseMouseCapture();
        InvalidateVisual();
        e.Handled = true;
        if (PictureRect is not { } picture || PixelSize is not { } px) return;

        // To source pixels (box coordinates are always frame pixels), then fractions.
        Rect drawn = Clamp(new Rect(start, e.GetPosition(this)), picture);
        double k = px.Width / picture.Width;
        var pixels = new PixelRect((int)Math.Round((drawn.X - picture.X) * k), (int)Math.Round((drawn.Y - picture.Y) * k),
            (int)Math.Round(drawn.Width * k), (int)Math.Round(drawn.Height * k));
        CaptureRegion? region = CaptureRegion.FromPixels(pixels, (int)px.Width, (int)px.Height);
        if (region is not null && DrawnCommand is { } command && command.CanExecute(region)) command.Execute(region);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragStart is null) return;
        _dragStart = null;
        InvalidateVisual();
    }

    private static void OnCanDrawChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var overlay = (RegionOverlay)d;
        overlay.Cursor = (bool)e.NewValue ? Cursors.Cross : null;
        if (!(bool)e.NewValue && overlay._dragStart is not null) overlay.ReleaseMouseCapture();
    }

    private static Rect Clamp(Rect r, Rect bounds)
    {
        r.Intersect(bounds);
        return r.IsEmpty ? new Rect(bounds.TopLeft, new Size(0, 0)) : r;
    }

    private sealed class OverlayPeer(RegionOverlay owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(RegionOverlay);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
