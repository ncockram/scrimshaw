using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scrimshaw.App.ViewModels;
using Scrimshaw.Core.Annotation;

namespace Scrimshaw.App.Views;

/// <summary>
/// Shows a frame with its boxes and lets the user draw, select, move and resize them (FR-A2–A4).
/// Drag on empty space to draw; drag a box or its handles to move or resize it. Mouse wheel zooms around
/// the pointer, right- or middle-drag pans, and <see cref="FitToView"/> (F key) fits the frame. All box
/// coordinates are frame pixels (FR-A10); only drawing converts to screen space.
/// </summary>
public sealed class AnnotationSurface : FrameworkElement
{
    public static readonly DependencyProperty ImagePathProperty = DependencyProperty.Register(
        nameof(ImagePath), typeof(string), typeof(AnnotationSurface), new PropertyMetadata(null, OnImageChanged));

    public static readonly DependencyProperty ImageVersionProperty = DependencyProperty.Register(
        nameof(ImageVersion), typeof(string), typeof(AnnotationSurface), new PropertyMetadata(null, OnImageChanged));

    public static readonly DependencyProperty BoxesProperty = DependencyProperty.Register(
        nameof(Boxes), typeof(IEnumerable), typeof(AnnotationSurface), new PropertyMetadata(null, OnBoxesChanged));

    public static readonly DependencyProperty SelectedBoxProperty = DependencyProperty.Register(
        nameof(SelectedBox), typeof(BoxViewModel), typeof(AnnotationSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HostProperty = DependencyProperty.Register(
        nameof(Host), typeof(IAnnotationHost), typeof(AnnotationSurface));

    public static readonly DependencyProperty IsDimmedProperty = DependencyProperty.Register(
        nameof(IsDimmed), typeof(bool), typeof(AnnotationSurface),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double HandleSize = 8, GrabDistance = 7, MinScale = 0.05, MaxScale = 8;

    private static readonly Brush Backdrop = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)));
    private static readonly Brush BoxFill = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x1E, 0x6F, 0xD9)));
    private static readonly Brush SelectedFill = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xE6, 0x7E, 0x22)));
    private static readonly Pen BoxPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x6F, 0xD9)), 2));
    private static readonly Pen SelectedPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)), 2.5));
    private static readonly Pen DraftPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Brush HandleFill = Brushes.White;
    private static readonly Brush LabelBack = Freeze(new SolidColorBrush(Color.FromArgb(0xD0, 0x1E, 0x6F, 0xD9)));
    private static readonly Brush SelectedLabelBack = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, 0xE6, 0x7E, 0x22)));
    private static readonly Brush DimOverlay = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x30, 0x30, 0x30)));

    private BitmapSource? _bitmap;
    private double _scale = 1;
    private Vector _offset;
    private bool _fitMode = true;

    // Interaction state
    private enum Drag { None, Draw, Edit, Pan }
    private Drag _drag;
    private Point _dragStartScreen;
    private Point _dragStartImage;
    private Point _dragNowImage;
    private BoxHandle _handle;
    private BoxViewModel? _editing;
    private PixelRect _editRect;
    private Vector _panStartOffset;

    public AnnotationSurface()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public string? ImagePath
    {
        get => (string?)GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    /// <summary>Bind to something that changes when the image file is rewritten, to force a reload.</summary>
    public string? ImageVersion
    {
        get => (string?)GetValue(ImageVersionProperty);
        set => SetValue(ImageVersionProperty, value);
    }

    public IEnumerable? Boxes
    {
        get => (IEnumerable?)GetValue(BoxesProperty);
        set => SetValue(BoxesProperty, value);
    }

    public BoxViewModel? SelectedBox
    {
        get => (BoxViewModel?)GetValue(SelectedBoxProperty);
        set => SetValue(SelectedBoxProperty, value);
    }

    public IAnnotationHost? Host
    {
        get => (IAnnotationHost?)GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    /// <summary>Greys the frame out (a deleted frame).</summary>
    public bool IsDimmed
    {
        get => (bool)GetValue(IsDimmedProperty);
        set => SetValue(IsDimmedProperty, value);
    }

    /// <summary>Zoom as a percentage of the frame's pixel size (100 = one frame pixel per screen unit).</summary>
    public double ZoomPercent => _scale * 100;

    public event EventHandler? ZoomChanged;

    public void FitToView()
    {
        _fitMode = true;
        Fit();
        InvalidateVisual();
    }

    /// <summary>Maps a point on this control to frame pixels.</summary>
    public Point ToImage(Point screen) => new((screen.X - _offset.X) / _scale, (screen.Y - _offset.Y) / _scale);

    /// <summary>Maps frame pixels to a point on this control.</summary>
    public Point ToScreen(double x, double y) => new(_offset.X + x * _scale, _offset.Y + y * _scale);

    /// <summary>Exposes the surface to UI Automation (screen readers and UI tests); custom elements have no peer by default.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new SurfacePeer(this);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_fitMode) Fit();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        DrawingContext dc = drawingContext;
        dc.DrawRectangle(Backdrop, null, new Rect(RenderSize));
        if (_bitmap is null) return;

        var imageRect = new Rect(ToScreen(0, 0), ToScreen(_bitmap.PixelWidth, _bitmap.PixelHeight));
        dc.DrawImage(_bitmap, imageRect);
        if (IsDimmed) dc.DrawRectangle(DimOverlay, null, imageRect);

        BoxViewModel? selected = SelectedBox;
        foreach (BoxViewModel box in (Boxes ?? Array.Empty<BoxViewModel>()).OfType<BoxViewModel>())
        {
            bool isSelected = selected is not null && box.Id == selected.Id;
            PixelRect r = _drag == Drag.Edit && _editing?.Id == box.Id ? _editRect : box.Rect;
            Rect screen = ScreenRect(r);
            dc.DrawRectangle(isSelected ? SelectedFill : BoxFill, isSelected ? SelectedPen : BoxPen, screen);
            DrawTag(dc, box.Title, screen, isSelected ? SelectedLabelBack : LabelBack);
            if (isSelected)
                foreach (Point p in HandlePoints(screen))
                    dc.DrawRectangle(HandleFill, SelectedPen, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
        }

        if (_drag == Drag.Draw)
        {
            Rect draft = new(ToScreen(_dragStartImage.X, _dragStartImage.Y), ToScreen(_dragNowImage.X, _dragNowImage.Y));
            dc.DrawRectangle(SelectedFill, DraftPen, draft);
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseDown(e);
        Focus();
        if (_bitmap is null) return;
        Point screen = e.GetPosition(this);

        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _drag = Drag.Pan;
            _dragStartScreen = screen;
            _panStartOffset = _offset;
            Cursor = Cursors.ScrollAll;
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            Point image = ToImage(screen);
            (BoxViewModel? box, BoxHandle handle) = HitTest(image);
            if (box is not null)
            {
                Host?.SelectBox(box.Id);
                _drag = Drag.Edit;
                _editing = box;
                _handle = handle;
                _editRect = box.Rect;
            }
            else
            {
                _drag = Drag.Draw;
                _dragNowImage = image;
            }
            _dragStartImage = image;
        }
        else
        {
            return;
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        Point screen = e.GetPosition(this);
        switch (_drag)
        {
            case Drag.Pan:
                _fitMode = false;
                _offset = _panStartOffset + (screen - _dragStartScreen);
                InvalidateVisual();
                break;
            case Drag.Draw:
                _dragNowImage = ClampToImage(ToImage(screen));
                InvalidateVisual();
                break;
            case Drag.Edit when _editing is not null && _bitmap is not null:
                Point image = ToImage(screen);
                _editRect = BoxGeometry.Apply(_editing.Rect, _handle, image.X - _dragStartImage.X, image.Y - _dragStartImage.Y,
                    _bitmap.PixelWidth, _bitmap.PixelHeight);
                InvalidateVisual();
                break;
            default:
                Cursor = CursorFor(HitTest(ToImage(screen)).Handle);
                break;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseUp(e);
        Drag drag = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();
        Cursor = null;

        if (drag == Drag.Draw && _bitmap is not null)
        {
            if (BoxGeometry.FromDrag(_dragStartImage.X, _dragStartImage.Y, _dragNowImage.X, _dragNowImage.Y,
                    _bitmap.PixelWidth, _bitmap.PixelHeight) is { } rect)
                Host?.AddBox(rect);
            else
                Host?.SelectBox(null); // a click on empty space clears the selection
        }
        else if (drag == Drag.Edit && _editing is not null && _editRect != _editing.Rect)
        {
            Host?.UpdateBox(_editing.Id, _editRect);
        }
        _editing = null;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseWheel(e);
        if (_bitmap is null) return;
        Point screen = e.GetPosition(this);
        Point image = ToImage(screen);
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.15 : 1 / 1.15), MinScale, MaxScale);
        _offset = new Vector(screen.X - image.X * _scale, screen.Y - image.Y * _scale); // keep the point under the pointer still
        _fitMode = false;
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _drag != Drag.None)
        {
            _drag = Drag.None;
            _editing = null;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag is Drag.Draw or Drag.Edit)
        {
            _drag = Drag.None;
            _editing = null;
            InvalidateVisual();
        }
    }

    private (BoxViewModel? Box, BoxHandle Handle) HitTest(Point image)
    {
        double grab = GrabDistance / _scale;
        var boxes = (Boxes ?? Array.Empty<BoxViewModel>()).OfType<BoxViewModel>().ToList();
        // The selected box's handles win, then the topmost (last drawn) box.
        if (SelectedBox is { } sel && boxes.FirstOrDefault(b => b.Id == sel.Id) is { } selected)
        {
            BoxHandle h = BoxGeometry.HitTest(selected.Rect, image.X, image.Y, grab);
            if (h != BoxHandle.None) return (selected, h);
        }
        for (int i = boxes.Count - 1; i >= 0; i--)
        {
            BoxHandle h = BoxGeometry.HitTest(boxes[i].Rect, image.X, image.Y, grab);
            if (h != BoxHandle.None) return (boxes[i], h);
        }
        return (null, BoxHandle.None);
    }

    private static Cursor? CursorFor(BoxHandle h) => h switch
    {
        BoxHandle.Move => Cursors.SizeAll,
        BoxHandle.N or BoxHandle.S => Cursors.SizeNS,
        BoxHandle.E or BoxHandle.W => Cursors.SizeWE,
        BoxHandle.NW or BoxHandle.SE => Cursors.SizeNWSE,
        BoxHandle.NE or BoxHandle.SW => Cursors.SizeNESW,
        _ => Cursors.Cross,
    };

    private Rect ScreenRect(PixelRect r) => new(ToScreen(r.X, r.Y), ToScreen(r.Right, r.Bottom));

    private Point ClampToImage(Point p) => _bitmap is null ? p
        : new Point(Math.Clamp(p.X, 0, _bitmap.PixelWidth), Math.Clamp(p.Y, 0, _bitmap.PixelHeight));

    private static IEnumerable<Point> HandlePoints(Rect r)
    {
        double cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        return [r.TopLeft, new(cx, r.Top), r.TopRight, new(r.Right, cy), r.BottomRight, new(cx, r.Bottom), r.BottomLeft, new(r.Left, cy)];
    }

    private void DrawTag(DrawingContext dc, string text, Rect box, Brush background)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double y = box.Top - ft.Height - 2 < 0 ? box.Top + 2 : box.Top - ft.Height - 2;
        var back = new Rect(box.Left, y, ft.Width + 8, ft.Height);
        dc.DrawRectangle(background, null, back);
        dc.DrawText(ft, new Point(back.Left + 4, back.Top));
    }

    private void Fit()
    {
        if (_bitmap is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        const double pad = 12;
        _scale = Math.Clamp(Math.Min((ActualWidth - 2 * pad) / _bitmap.PixelWidth, (ActualHeight - 2 * pad) / _bitmap.PixelHeight), MinScale, MaxScale);
        _offset = new Vector((ActualWidth - _bitmap.PixelWidth * _scale) / 2, (ActualHeight - _bitmap.PixelHeight * _scale) / 2);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void OnImageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (AnnotationSurface)d;
        string? path = s.ImagePath;
        BitmapSource? previous = s._bitmap;
        s._bitmap = path is null || !File.Exists(path) ? null : Load(path);
        // Keep the zoom when the same-sized image is reloaded (e.g. replaced); refit for a different frame.
        bool sameSize = previous is not null && s._bitmap is not null &&
            previous.PixelWidth == s._bitmap.PixelWidth && previous.PixelHeight == s._bitmap.PixelHeight;
        if (e.Property == ImagePathProperty || !sameSize)
        {
            s._fitMode = true;
            s.Fit();
        }
        s.InvalidateVisual();
    }

    private static BitmapImage? Load(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void OnBoxesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (AnnotationSurface)d;
        if (e.OldValue is INotifyCollectionChanged oldList) oldList.CollectionChanged -= s.OnBoxListChanged;
        if (e.NewValue is INotifyCollectionChanged newList) newList.CollectionChanged += s.OnBoxListChanged;
        s.InvalidateVisual();
    }

    private void OnBoxListChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private sealed class SurfacePeer(AnnotationSurface owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(AnnotationSurface);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
