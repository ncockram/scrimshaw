namespace Scrimshaw.Core.Workspaces;

/// <summary>Contents of <c>manifest.json</c>: every kept frame and its boxes. Immutable; edits produce new records.</summary>
public sealed record Manifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The sequence number the next captured frame will get (FR-C11: numbering continues).</summary>
    public int NextSequence { get; init; } = 1;

    /// <summary>Ordered by <see cref="FrameRecord.Seq"/>.</summary>
    public IReadOnlyList<FrameRecord> Frames { get; init; } = [];
}

public sealed record FrameRecord
{
    public required int Seq { get; init; }

    /// <summary>Path relative to the workspace root, with forward slashes, e.g. <c>images/frame_00001.png</c>.</summary>
    public required string File { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>64-bit dHash as 16 hex digits (FR-D4).</summary>
    public required string DHash { get; init; }

    /// <summary>Hidden from review and export (FR-A9); the image file is kept.</summary>
    public bool Deleted { get; init; }

    public IReadOnlyList<BoxRecord> Boxes { get; init; } = [];
}

/// <summary>A region to export, in the frame's own pixels (FR-A10).</summary>
public sealed record BoxRecord
{
    public required string Id { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int W { get; init; }
    public required int H { get; init; }

    /// <summary>Optional; labelled boxes become PDF bookmarks (FR-A5, FR-P10).</summary>
    public string? Label { get; init; }

    /// <summary>Creation order within the frame; export order follows it (FR-P2).</summary>
    public required int Order { get; init; }
}
