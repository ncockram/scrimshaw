using System.Windows;
using Scrimshaw.App.Services;
using Scrimshaw.App.ViewModels;
using Scrimshaw.Imaging;

namespace Scrimshaw.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);

        MainWindow? window = null;
        var viewModel = new MainViewModel(
            new DialogService(() => window),
            new DispatcherUi(Dispatcher),
            new TargetProvider(),
            new CaptureService(new WicImageCodec()));
        window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();

        // A workspace folder on the command line is opened straight away (handy for shortcuts and UI tests).
        if (e.Args.Length > 0 && Directory.Exists(e.Args[0])) viewModel.Open(e.Args[0]);
    }
}
