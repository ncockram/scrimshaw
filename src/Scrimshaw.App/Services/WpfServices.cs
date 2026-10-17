using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Scrimshaw.App.Services;

public sealed class DialogService(Func<Window?> owner) : IDialogService
{
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(owner()) == true ? dialog.FolderName : null;
    }

    public void ShowError(string title, string message)
    {
        Window? w = owner();
        if (w is null) MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(w, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public bool Confirm(string title, string message)
    {
        Window? w = owner();
        MessageBoxResult result = w is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(w, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }
}

public sealed class DispatcherUi(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);
}

/// <summary>
/// A system-wide shortcut (FR-C9) so capture can be started and stopped without leaving the video.
/// Registration fails quietly if another app already owns the combination; <see cref="IsRegistered"/> says which.
/// </summary>
public sealed partial class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000;
    private const int Id = 0x5C21;

    private readonly HwndSource _source;
    private readonly Action _pressed;

    /// <param name="key">A virtual-key code, e.g. 'S'.</param>
    public GlobalHotkey(Window window, uint key, Action pressed)
    {
        ArgumentNullException.ThrowIfNull(window);
        _pressed = pressed;
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(Hook);
        IsRegistered = RegisterHotKey(hwnd, Id, ModControl | ModAlt | ModNoRepeat, key);
    }

    public bool IsRegistered { get; }

    public void Dispose()
    {
        if (IsRegistered) UnregisterHotKey(_source.Handle, Id);
        _source.RemoveHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam == Id)
        {
            _pressed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hwnd, int id);
}
