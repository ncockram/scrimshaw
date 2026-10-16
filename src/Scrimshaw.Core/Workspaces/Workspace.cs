using System.Globalization;
using System.Text.RegularExpressions;
using Scrimshaw.Core.Detection;
using Scrimshaw.Core.Imaging;

namespace Scrimshaw.Core.Workspaces;

/// <summary>What <see cref="Workspace.Open"/> had to repair.</summary>
/// <param name="RecoveredFrames">Images on disk that the manifest didn't list (crash between image and manifest write); now added.</param>
/// <param name="MissingFiles">Frames whose image file is gone; left in the manifest.</param>
/// <param name="UnreadableFiles">Image files that couldn't be decoded (e.g. half-written); left alone.</param>
public sealed record RecoveryReport(IReadOnlyList<int> RecoveredFrames, IReadOnlyList<int> MissingFiles, IReadOnlyList<string> UnreadableFiles)
{
    public bool IsClean => RecoveredFrames.Count == 0 && MissingFiles.Count == 0 && UnreadableFiles.Count == 0;
}

/// <summary>
/// One video's folder (FR-W1–W5): settings, frame images, the manifest, a thumbnail cache and exports.
/// </summary>
public sealed partial class Workspace : IDisposable
{
    public const string SettingsFileName = "scrimshaw.json";
    public const string ManifestFileName = "manifest.json";
    public const string ImagesFolder = "images";
    public const string ThumbsFolder = "thumbs";
    public const string ExportsFolder = "exports";

    private Workspace(string root, WorkspaceSettings settings, ManifestStore manifest, RecoveryReport recovery)
    {
        Root = root;
        Settings = settings;
        Manifest = manifest;
        Recovery = recovery;
    }

    public string Root { get; }
    public WorkspaceSettings Settings { get; private set; }
    public ManifestStore Manifest { get; }
    public RecoveryReport Recovery { get; }

    public string ImagesPath => Path.Combine(Root, ImagesFolder);
    public string ThumbsPath => Path.Combine(Root, ThumbsFolder);
    public string ExportsPath => Path.Combine(Root, ExportsFolder);

    public static bool IsWorkspace(string folder) => File.Exists(Path.Combine(folder, SettingsFileName));

    /// <summary>Creates a workspace in a new or empty folder.</summary>
    /// <exception cref="WorkspaceException">The folder has other files in it.</exception>
    public static Workspace Create(string folder, WorkspaceSettings? settings = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string root = Path.GetFullPath(folder);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new WorkspaceException(IsWorkspace(root)
                ? "That folder is already a Scrimshaw workspace. Open it instead."
                : "Choose an empty folder for a new workspace.");

        settings ??= new WorkspaceSettings();
        settings.Validate();

        Directory.CreateDirectory(root);
        foreach (string sub in new[] { ImagesFolder, ThumbsFolder, ExportsFolder })
            Directory.CreateDirectory(Path.Combine(root, sub));
        AtomicFile.WriteAllBytes(Path.Combine(root, SettingsFileName), ScrimshawJson.Serialize(settings));
        var manifest = ManifestStore.CreateNew(Path.Combine(root, ManifestFileName), time);
        return new Workspace(root, settings, manifest, new RecoveryReport([], [], []));
    }

    /// <summary>Opens a workspace, repairing what an earlier crash may have left behind.</summary>
    /// <param name="codec">Used to read any images the manifest doesn't know about.</param>
    /// <exception cref="WorkspaceException">Not a workspace, or its files are damaged.</exception>
    public static Workspace Open(string folder, IImageCodec codec, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(codec);
        string root = Path.GetFullPath(folder);

        string settingsPath = Path.Combine(root, SettingsFileName);
        string manifestPath = Path.Combine(root, ManifestFileName);
        AtomicFile.RecoverIfNeeded(settingsPath, CanRead(ScrimshawJson.ReadSettings));
        AtomicFile.RecoverIfNeeded(manifestPath, CanRead(ScrimshawJson.ReadManifest));
        if (!File.Exists(settingsPath))
            throw new WorkspaceException("That folder isn't a Scrimshaw workspace.");

        WorkspaceSettings settings = ScrimshawJson.ReadSettings(settingsPath);
        foreach (string sub in new[] { ImagesFolder, ThumbsFolder, ExportsFolder })
            Directory.CreateDirectory(Path.Combine(root, sub));

        ManifestStore manifest = File.Exists(manifestPath)
            ? ManifestStore.Load(manifestPath, time)
            : ManifestStore.CreateNew(manifestPath, time);

        RecoveryReport report = Recover(root, manifest, codec);
        manifest.Flush();
        return new Workspace(root, settings, manifest, report);
    }

    /// <summary>Changes and saves the settings.</summary>
    /// <exception cref="ArgumentException">Invalid values.</exception>
    /// <exception cref="InvalidOperationException">Changing the image format after frames exist.</exception>
    public void UpdateSettings(Func<WorkspaceSettings, WorkspaceSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        WorkspaceSettings next = update(Settings);
        next.Validate();
        if (next.ImageFormat != Settings.ImageFormat && Manifest.Current.Frames.Count > 0)
            throw new InvalidOperationException("The image format can't change once a workspace has frames.");
        AtomicFile.WriteAllBytes(Path.Combine(Root, SettingsFileName), ScrimshawJson.Serialize(next));
        Settings = next;
    }

    /// <summary>Relative path (as stored in the manifest) for a new frame in the workspace's format.</summary>
    public string RelativeFramePath(int seq) => FrameRelativePath(seq, Settings.ImageFormat);

    public string FullPath(FrameRecord frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Path.Combine(Root, frame.File.Replace('/', Path.DirectorySeparatorChar));
    }

    public static string FrameRelativePath(int seq, ImageFormat format) =>
        string.Create(CultureInfo.InvariantCulture, $"{ImagesFolder}/frame_{seq:D5}{format.FileExtension()}");

    public void Dispose() => Manifest.Dispose();

    private static RecoveryReport Recover(string root, ManifestStore manifest, IImageCodec codec)
    {
        Manifest m = manifest.Current;
        var known = m.Frames.Select(f => f.Seq).ToHashSet();
        var recovered = new List<int>();
        var unreadable = new List<string>();

        // A ".tmp" image is a frame whose write never finished; the manifest never referenced it.
        foreach (string partial in Directory.EnumerateFiles(Path.Combine(root, ImagesFolder), "*.tmp"))
            File.Delete(partial);

        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, ImagesFolder)).Order(StringComparer.OrdinalIgnoreCase))
        {
            Match match = FrameFileRegex().Match(Path.GetFileName(file));
            if (!match.Success) continue;
            int seq = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (known.Contains(seq)) continue;

            RawImage image;
            try
            {
                image = codec.Decode(file);
            }
#pragma warning disable CA1031 // Any decode failure means "leave this file alone and report it".
            catch (Exception)
#pragma warning restore CA1031
            {
                unreadable.Add(Path.GetFileName(file));
                continue;
            }

            manifest.AddFrame(new FrameRecord
            {
                Seq = seq,
                File = $"{ImagesFolder}/{Path.GetFileName(file)}",
                CapturedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero),
                Width = image.Width,
                Height = image.Height,
                DHash = DHash.ToHex(DHash.Compute(GrayGrid.FromImage(image))),
            });
            known.Add(seq);
            recovered.Add(seq);
        }

        var missing = manifest.Current.Frames
            .Where(f => !File.Exists(Path.Combine(root, f.File.Replace('/', Path.DirectorySeparatorChar))))
            .Select(f => f.Seq)
            .ToList();

        return new RecoveryReport(recovered, missing, unreadable);
    }

    private static Func<string, bool> CanRead<T>(Func<string, T> read) => path =>
    {
        try
        {
            read(path);
            return true;
        }
        catch (WorkspaceException)
        {
            return false;
        }
    };

    [GeneratedRegex(@"^frame_(\d+)\.(png|jpg)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrameFileRegex();
}
