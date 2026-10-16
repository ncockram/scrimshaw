using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrimshaw.Core.Workspaces;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkspaceSettings))]
[JsonSerializable(typeof(Manifest))]
internal sealed partial class ScrimshawJsonContext : JsonSerializerContext;

internal static class ScrimshawJson
{
    public static byte[] Serialize(WorkspaceSettings settings) =>
        JsonSerializer.SerializeToUtf8Bytes(settings, ScrimshawJsonContext.Default.WorkspaceSettings);

    public static byte[] Serialize(Manifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, ScrimshawJsonContext.Default.Manifest);

    public static WorkspaceSettings ReadSettings(string path) =>
        Read(path, ScrimshawJsonContext.Default.WorkspaceSettings, WorkspaceSettings.CurrentSchemaVersion, s => s.SchemaVersion);

    public static Manifest ReadManifest(string path) =>
        Read(path, ScrimshawJsonContext.Default.Manifest, Manifest.CurrentSchemaVersion, m => m.SchemaVersion);

    private static T Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, int supported, Func<T, int> version)
    {
        T? value;
        try
        {
            using FileStream fs = File.OpenRead(path);
            value = JsonSerializer.Deserialize(fs, type);
        }
        catch (JsonException ex)
        {
            throw new WorkspaceException($"'{Path.GetFileName(path)}' is damaged and can't be read.", ex);
        }

        if (value is null)
            throw new WorkspaceException($"'{Path.GetFileName(path)}' is empty.");
        if (version(value) > supported)
            throw new WorkspaceException($"'{Path.GetFileName(path)}' was written by a newer version of Scrimshaw.");
        return value;
    }
}

public sealed class WorkspaceException : Exception
{
    public WorkspaceException() { }
    public WorkspaceException(string message) : base(message) { }
    public WorkspaceException(string message, Exception inner) : base(message, inner) { }
}
