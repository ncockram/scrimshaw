using System.ComponentModel;
using System.Windows;
using Scrimshaw.App.Services;
using Scrimshaw.App.ViewModels;

namespace Scrimshaw.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The hotkey is released in OnClosing, the window's end of life.")]
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private GlobalHotkey? _hotkey;
    private bool _closingCleanly;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        if (!RestorePlacement(viewModel.AppSettings.Load().Window)) FitToWorkArea();
        SourceInitialized += (_, _) =>
        {
            _hotkey = new GlobalHotkey(this, 'S', viewModel.ToggleCapture);
            if (!_hotkey.IsRegistered)
                HotkeyHint.Text = "Ctrl+Alt+S is in use by another app, so the capture shortcut is off.";
        };
    }

    /// <summary>
    /// The default size is too tall for a 1080p screen at 125% (Phase 3 UI test), so shrink to the work area
    /// (screen minus taskbar) and centre the window on it.
    /// </summary>
    private void FitToWorkArea()
    {
        Rect area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width * 0.95);
        Height = Math.Min(Height, area.Height * 0.95);
        MinHeight = Math.Min(MinHeight, Height);
        MinWidth = Math.Min(MinWidth, Width);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    /// <summary>Puts the window back where it was last time, if that's still mostly on a connected screen.</summary>
    private bool RestorePlacement(WindowPlacement? p)
    {
        if (p is null || p.Width < 300 || p.Height < 200) return false;
        var saved = new Rect(p.Left, p.Top, p.Width, p.Height);
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Rect visible = Rect.Intersect(saved, desktop);
        if (visible.IsEmpty || visible.Width * visible.Height < saved.Width * saved.Height * 0.5) return false;

        Left = p.Left;
        Top = p.Top;
        Width = p.Width;
        Height = p.Height;
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
        if (p.Maximized) WindowState = WindowState.Maximized;
        return true;
    }

    private void SavePlacement()
    {
        Rect r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.IsEmpty) return;
        AppSettings settings = _viewModel.AppSettings.Load();
        _viewModel.AppSettings.Save(settings with
        {
            Window = new WindowPlacement(r.Left, r.Top, r.Width, r.Height, WindowState == WindowState.Maximized),
        });
    }

    private void OnAbout(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    protected override async void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (_closingCleanly) return;

        // Stop capture and finish writing before the window goes (FR-C3).
        e.Cancel = true;
        SavePlacement();
        IsEnabled = false;
        await _viewModel.DisposeAsync();
        _hotkey?.Dispose();
        _closingCleanly = true;
        Close();
    }
}
