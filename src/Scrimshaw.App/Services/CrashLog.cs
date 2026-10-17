using System.Globalization;
using System.Reflection;

namespace Scrimshaw.App.Services;

/// <summary>Writes unexpected errors to <c>%LOCALAPPDATA%\Scrimshaw\logs</c> so they can be reported and diagnosed.</summary>
public static class CrashLog
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scrimshaw", "logs");

    /// <returns>The log file written, or null if even that failed.</returns>
    public static string? Write(Exception exception, string context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, string.Create(CultureInfo.InvariantCulture, $"error-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt"));
            File.WriteAllText(path, string.Join(Environment.NewLine,
                $"Scrimshaw {Version}",
                $"Windows {Environment.OSVersion.Version}, {Environment.ProcessorCount} cores, .NET {Environment.Version}",
                $"When: {DateTime.Now:O}",
                $"Where: {context}",
                "",
                exception.ToString()));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string Version =>
        typeof(CrashLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CrashLog).Assembly.GetName().Version?.ToString() ?? "?";
}
