using System.Windows;
using System.Windows.Threading;
using Scrimshaw.App.Services;
using Scrimshaw.App.ViewModels;
using Scrimshaw.Imaging;

namespace Scrimshaw.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in OnExit, the application's end of life.")]
public partial class App : Application
{
    private readonly ThumbnailService _thumbnails = new();

    protected override void OnExit(ExitEventArgs e)
    {
        _thumbnails.Dispose();
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write((Exception)args.ExceptionObject, "background thread (app closing)");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception, "background task");
            args.SetObserved();
        };

        MainWindow? window = null;
        var viewModel = new MainViewModel(
            new DialogService(() => window),
            new DispatcherUi(Dispatcher),
            new TargetProvider(),
            new CaptureService(new WicImageCodec()),
            _thumbnails,
            new ShellService(),
            new AppSettingsStore(AppSettingsStore.DefaultPath),
            new PreviewService());
        window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();

        // A workspace folder on the command line is opened straight away (handy for shortcuts and UI tests).
        if (e.Args.Length > 0 && Directory.Exists(e.Args[0])) viewModel.Open(e.Args[0]);
    }

    /// <summary>
    /// Last line of defence: log the error and tell the user, but keep running. Workspace edits are already on
    /// disk (debounced writes), so carrying on is safer than crashing mid-capture.
    /// </summary>
    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string? log = CrashLog.Write(e.Exception, "UI thread");
        MessageBox.Show(MainWindow!,
            "Something went wrong, but Scrimshaw will try to carry on. Your boxes and captured frames are saved as you go." +
            (log is null ? "" : $"\n\nDetails were saved to:\n{log}") + $"\n\n{e.Exception.Message}",
            "Scrimshaw hit a problem", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
