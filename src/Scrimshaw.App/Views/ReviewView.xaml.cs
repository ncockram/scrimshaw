using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scrimshaw.App.ViewModels;

namespace Scrimshaw.App.Views;

/// <summary>The Review tab. Keyboard shortcuts (FR-A7) live here so they work wherever focus is in the tab, except while typing a label.</summary>
public partial class ReviewView : UserControl
{
    public ReviewView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(() => Surface.Focus());
        };
    }

    private ReviewViewModel? ViewModel => DataContext as ReviewViewModel;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.OriginalSource is TextBox) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        ICommand? command = (e.Key, ctrl, shift) switch
        {
            (Key.Left or Key.PageUp, false, _) => vm.PreviousFrameCommand,
            (Key.Right or Key.PageDown, false, _) => vm.NextFrameCommand,
            (Key.Delete or Key.Back, false, false) => vm.DeleteSelectedBoxCommand,
            (Key.Z, true, false) => vm.UndoCommand,
            (Key.Y, true, false) or (Key.Z, true, true) => vm.RedoCommand,
            (Key.D, true, false) => vm.ToggleFrameDeletedCommand,
            (Key.V, true, true) => vm.CopyFromPreviousCommand,
            (Key.A, true, true) => vm.ApplyToFollowingCommand,
            _ => null,
        };
        if (command is not null)
        {
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.F when !ctrl:
                Surface.FitToView();
                e.Handled = true;
                break;
            case Key.Escape:
                vm.SelectBox(null);
                e.Handled = true;
                break;
            case Key.Enter when vm.HasSelectedBox:
                LabelBox.Focus();
                LabelBox.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void OnLabelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.CommitLabelCommand.Execute(null);
            Surface.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (ViewModel is { } vm) vm.LabelText = vm.SelectedBox?.Label ?? "";
            Surface.Focus();
            e.Handled = true;
        }
    }

    private void OnLabelLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ViewModel?.CommitLabelCommand.Execute(null);

    private void OnFit(object sender, RoutedEventArgs e)
    {
        Surface.FitToView();
        Surface.Focus();
    }
}
