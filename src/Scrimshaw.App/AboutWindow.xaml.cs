using System.Globalization;
using System.Windows;
using Scrimshaw.App.Services;

namespace Scrimshaw.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        // Informational version is "1.0.0+<commit>"; show the commit short.
        string version = CrashLog.Version;
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        VersionText.Text = plus < 0
            ? $"Version {version}"
            : string.Create(CultureInfo.InvariantCulture, $"Version {version[..plus]} (build {version[(plus + 1)..Math.Min(version.Length, plus + 8)]})");
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(CrashLog.Folder);
        new ShellService().Open(CrashLog.Folder);
    }
}
