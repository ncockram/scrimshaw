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

/// <summary>Runs work on the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
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
