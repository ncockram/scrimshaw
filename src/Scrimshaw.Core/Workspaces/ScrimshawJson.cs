using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrimshaw.Core.Workspaces;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
// Optional properties use `set`, not `init`: source-generated deserialisation sets every missing init-only
// property to null/0, which would wipe the defaults of fields added in later versions (Phase 4 finding F27).
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
            throw new WorkspaceException($"'{Path.GetFileName(path)}' is damaged and can't be read.", WorkspaceProblem.Damaged, ex) { DamagedFile = path };
        }

        if (value is null)
            throw new WorkspaceException($"'{Path.GetFileName(path)}' is empty.", WorkspaceProblem.Damaged) { DamagedFile = path };
        if (version(value) > supported)
            throw new WorkspaceException($"'{Path.GetFileName(path)}' was written by a newer version of Scrimshaw.", WorkspaceProblem.NewerVersion);
        return value;
    }
}

/// <summary>Why a workspace couldn't be created or opened, so the app can offer the right fix.</summary>
public enum WorkspaceProblem
{
    Other,
    NotAWorkspace,
    FolderNotEmpty,

    /// <summary>A settings or manifest file can't be read; <see cref="Workspace.OpenWithRepair"/> can rebuild it.</summary>
    Damaged,

    NewerVersion,

    /// <summary>Another Scrimshaw window has the workspace open.</summary>
    InUse,
}

public sealed class WorkspaceException : Exception
{
    public WorkspaceException() { }
    public WorkspaceException(string message) : base(message) { }
    public WorkspaceException(string message, Exception inner) : base(message, inner) { }

    public WorkspaceException(string message, WorkspaceProblem problem, Exception? inner = null) : base(message, inner) => Problem = problem;

    public WorkspaceProblem Problem { get; }

    /// <summary>For <see cref="WorkspaceProblem.Damaged"/>: the file that couldn't be read.</summary>
    public string? DamagedFile { get; init; }
}
