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
        SourceInitialized += (_, _) =>
        {
            _hotkey = new GlobalHotkey(this, 'S', viewModel.ToggleCapture);
            if (!_hotkey.IsRegistered)
                HotkeyHint.Text = "Ctrl+Alt+S is in use by another app, so the capture shortcut is off.";
        };
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (_closingCleanly) return;

        // Stop capture and finish writing before the window goes (FR-C3).
        e.Cancel = true;
        IsEnabled = false;
        await _viewModel.DisposeAsync();
        _hotkey?.Dispose();
        _closingCleanly = true;
        Close();
    }
}
