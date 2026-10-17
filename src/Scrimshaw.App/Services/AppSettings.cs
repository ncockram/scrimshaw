using System.Text.Json;

namespace Scrimshaw.App.Services;

/// <summary>Where the main window was, in device-independent units.</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>Per-user app settings (not per workspace): recent workspaces (FR-W4) and the window position.</summary>
public sealed record AppSettings
{
    public const int MaxRecent = 8;

    public IReadOnlyList<string> Recent { get; set; } = [];
    public WindowPlacement? Window { get; set; }

    /// <summary>Puts <paramref name="folder"/> first, without duplicates, keeping at most <see cref="MaxRecent"/>.</summary>
    public AppSettings WithRecent(string folder) => this with
    {
        Recent = [folder, .. Recent.Where(r => !string.Equals(r, folder, StringComparison.OrdinalIgnoreCase)).Take(MaxRecent - 1)],
    };

    public AppSettings WithoutRecent(string folder) => this with
    {
        Recent = Recent.Where(r => !string.Equals(r, folder, StringComparison.OrdinalIgnoreCase)).ToList(),
    };
}

public interface IAppSettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

/// <summary>
/// Keeps <see cref="AppSettings"/> in <c>%LOCALAPPDATA%\Scrimshaw\settings.json</c>. These are conveniences:
/// a missing or damaged file just means starting with defaults, and a failed save is ignored.
/// </summary>
public sealed class AppSettingsStore(string path) : IAppSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scrimshaw", "settings.json");

    public AppSettings Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a recent-list update or window position is harmless.
        }
    }
}

/// <summary>Keeps settings in memory only (tests, or when no store is supplied).</summary>
public sealed class InMemoryAppSettingsStore : IAppSettingsStore
{
    private AppSettings _settings = new();

    public AppSettings Load() => _settings;

    public void Save(AppSettings settings) => _settings = settings;
}
