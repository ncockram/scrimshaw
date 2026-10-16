using Scrimshaw.Capture;
using Scrimshaw.Capture.Sources;
using Scrimshaw.Capture.Targets;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Core.Workspaces;

namespace Scrimshaw.App.Services;

/// <summary>Folder pickers and message boxes, behind an interface so view-models can be tested.</summary>
public interface IDialogService
{
    /// <returns>The chosen folder, or null if cancelled.</returns>
    string? PickFolder(string title);

    void ShowError(string title, string message);
}

/// <summary>Opens files and folders with Windows (so tests can check what would be opened).</summary>
public interface IShellService
{
    void Open(string path);
    void ShowInFolder(string path);
}

public sealed class ShellService : IShellService
{
    public void Open(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    public void ShowInFolder(string path) =>
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
}

/// <summary>Runs work on the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Makes thumbnails for the review strip off the UI thread.</summary>
public interface IThumbnailService
{
    /// <returns>The thumbnail path, or null if the frame couldn't be read.</returns>
    Task<string?> GetAsync(string framePath, string thumbsFolder, CancellationToken cancellationToken);
}

public sealed class ThumbnailService : IThumbnailService, IDisposable
{
    // One at a time: thumbnails are cheap, and this keeps decoding from competing with capture.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> GetAsync(string framePath, string thumbsFolder, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Scrimshaw.Imaging.Thumbnails.GetOrCreate(framePath, thumbsFolder);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>Lists capture targets (wraps <see cref="CaptureTargets"/> so tests can supply their own).</summary>
public interface ITargetProvider
{
    IReadOnlyList<CaptureTarget> GetAll();
}

/// <summary>Starts capture sessions (wraps the real WGC sources so tests can fake them).</summary>
public interface ICaptureService
{
    IImageCodec Codec { get; }

    /// <summary>Creates the frame source and starts a session; the source is disposed when the session completes.</summary>
    CaptureSession Start(Workspace workspace, CaptureTarget target);
}

public sealed class TargetProvider : ITargetProvider
{
    public IReadOnlyList<CaptureTarget> GetAll() => CaptureTargets.GetAll();
}

public sealed class CaptureService(IImageCodec codec) : ICaptureService
{
    public IImageCodec Codec { get; } = codec;

    public CaptureSession Start(Workspace workspace, CaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        IFrameSource source = FrameSources.Create(target, workspace.Settings.IncludeCursor);
        try
        {
            var session = new CaptureSession(workspace, source, Codec);
            session.Completion.ContinueWith(_ => source.Dispose(), TaskScheduler.Default);
            session.Start();
            return session;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }
}
