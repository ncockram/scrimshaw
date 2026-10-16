using System.Text.Json;

namespace Scrimshaw.Harness;

/// <summary>
/// Ground truth for a recorded sequence (<c>labels.json</c> next to the frames). Frame numbers are the
/// numbers in the file names (<c>frame_00019.png</c> = 19).
/// <list type="bullet">
/// <item><c>pages</c>: each page's run of clean frames. Exactly one frame per page should be saved.</item>
/// <item><c>ignore</c>: runs where saving is acceptable either way (e.g. YouTube's end screen).</item>
/// </list>
/// Frames in neither list are transitions; saving one is an error.
/// </summary>
internal sealed record Labels(string? Description, IReadOnlyList<PageLabel> Pages, IReadOnlyList<RangeLabel>? Ignore)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static Labels Load(string path) =>
        JsonSerializer.Deserialize<Labels>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} is empty");
}

internal sealed record PageLabel(string Id, int First, int Last);

internal sealed record RangeLabel(int First, int Last, string? Reason);
