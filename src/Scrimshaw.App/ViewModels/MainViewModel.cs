using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scrimshaw.App.Services;
using Scrimshaw.Capture;
using Scrimshaw.Capture.Targets;
using Scrimshaw.Core.Imaging;
using Scrimshaw.Core.Workspaces;

namespace Scrimshaw.App.ViewModels;

/// <summary>An entry in the target picker.</summary>
public sealed record TargetOption(CaptureTarget Target)
{
    public string DisplayName => Target.DisplayName;
    public string Group => Target is WindowTarget ? "Windows" : "Screens";
}

/// <summary>
/// The main window: workspace open/create and the capture panel (FR-W1, FR-C1–C9, FR-D3).
/// Review and export are added in Phases 3 and 4.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    public const double MinThresholdPercent = 1, MaxThresholdPercent = 30;

    /// <summary>Below this much free space capture won't start (about 100 4K PNG frames).</summary>
    public const long MinFreeBytes = 200L * 1024 * 1024;

    /// <summary>Below this much free space capture starts with a warning.</summary>
    public const long LowFreeBytes = 1024L * 1024 * 1024;

    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly ITargetProvider _targets;
    private readonly ICaptureService _capture;
    private readonly IThumbnailService _thumbnails;
    private readonly IShellService _shell;
    private readonly IAppSettingsStore _appSettings;
    private Workspace? _workspace;
    private CaptureSession? _session;
    private int _framesAtStart;

    public MainViewModel(IDialogService dialogs, IUiDispatcher ui, ITargetProvider targets, ICaptureService capture,
        IThumbnailService? thumbnails = null, IShellService? shell = null, IAppSettingsStore? appSettings = null)
    {
        _dialogs = dialogs;
        _ui = ui;
        _targets = targets;
        _capture = capture;
        _thumbnails = thumbnails ?? NoThumbnails.Instance;
        _shell = shell ?? new ShellService();
        _appSettings = appSettings ?? new InMemoryAppSettingsStore();
        foreach (string folder in _appSettings.Load().Recent.Where(Directory.Exists)) RecentWorkspaces.Add(folder);
        IntervalText = FormatInterval(1.0);
        ThresholdPercent = 3;
        JpegQuality = 90;
        RefreshTargets();
    }

    public ObservableCollection<TargetOption> Targets { get; } = [];

    /// <summary>Recently opened workspaces, newest first (FR-W4).</summary>
    public ObservableCollection<string> RecentWorkspaces { get; } = [];

    public bool HasRecent => RecentWorkspaces.Count > 0;

    /// <summary>The app-wide settings store, for the window to save and restore its position.</summary>
    public IAppSettingsStore AppSettings => _appSettings;

    public Workspace? Workspace => _workspace;

    public bool HasWorkspace => _workspace is not null;

    public bool CanEditSettings => HasWorkspace && !IsCapturing;

    /// <summary>The image format is fixed once a workspace has frames.</summary>
    public bool CanChangeFormat => CanEditSettings && FrameCount == 0;

    [ObservableProperty]
    public partial string? WorkspacePath { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureCommand))]
    [NotifyPropertyChangedFor(nameof(CanStopOnTitleChange))]
    public partial TargetOption? SelectedTarget { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureCommand))]
    public partial string IntervalText { get; set; }

    [ObservableProperty]
    public partial string? IntervalError { get; private set; }

    [ObservableProperty]
    public partial double ThresholdPercent { get; set; }

    [ObservableProperty]
    public partial bool IsJpeg { get; set; }

    [ObservableProperty]
    public partial int JpegQuality { get; set; }

    [ObservableProperty]
    public partial bool IncludeCursor { get; set; }

    /// <summary>FR-C14: stop when the captured window's title changes.</summary>
    [ObservableProperty]
    public partial bool StopOnTitleChange { get; set; }

    /// <summary>The title option only applies to windows, and can't change mid-capture.</summary>
    public bool CanStopOnTitleChange => CanEditSettings && SelectedTarget?.Target is WindowTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings), nameof(CanChangeFormat), nameof(CanStopOnTitleChange))]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureCommand), nameof(StopCaptureCommand), nameof(NewWorkspaceCommand), nameof(OpenWorkspaceCommand), nameof(OpenRecentCommand))]
    public partial bool IsCapturing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeFormat))]
    public partial int FrameCount { get; private set; }

    [ObservableProperty]
    public partial string? LatestFramePath { get; private set; }

    [ObservableProperty]
    public partial string? LatestFrameSize { get; private set; }

    [ObservableProperty]
    public partial CaptureStats Stats { get; private set; } = CaptureStats.Empty;

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    /// <summary>The Review tab for the open workspace (Phase 3).</summary>
    [ObservableProperty]
    public partial ReviewViewModel? Review { get; private set; }

    /// <summary>The Export PDF tab for the open workspace (Phase 4).</summary>
    [ObservableProperty]
    public partial ExportViewModel? ExportTab { get; private set; }

    /// <summary>0 = Capture, 1 = Review, 2 = Export.</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    public string ElapsedText => Stats.Elapsed.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture);

    /// <summary>The live "how much changed" readout, as a percentage (FR-D3 helper).</summary>
    public double? LastChangePercent => Stats.LastChangedFraction is { } f ? Math.Round(f * 100, 1) : null;

    public string LastChangeText => LastChangePercent is { } p
        ? string.Create(CultureInfo.CurrentCulture, $"{p:0.0}% changed (new page at {ThresholdPercent:0.#}%)")
        : "—";

    public string TimingText => string.Create(CultureInfo.CurrentCulture, $"±{Stats.MeanDriftMs:0} ms average, {Stats.MaxDriftMs:0} ms worst");

    private bool CanStart => HasWorkspace && !IsCapturing && SelectedTarget is not null && TryParseInterval(IntervalText, out _);

    [RelayCommand(CanExecute = nameof(CanSwitchWorkspace))]
    private void NewWorkspace()
    {
        string? folder = _dialogs.PickFolder("Choose an empty folder for the new workspace");
        if (folder is null) return;
        try
        {
            SwitchTo(Workspace.Create(folder));
            Remember(folder);
            StatusMessage = "New workspace created. Choose what to capture, then start.";
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _dialogs.ShowError("Can't create workspace", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSwitchWorkspace))]
    private void OpenWorkspace()
    {
        string? folder = _dialogs.PickFolder("Open a Scrimshaw workspace folder");
        if (folder is null) return;
        Open(folder);
    }

    [RelayCommand(CanExecute = nameof(CanSwitchWorkspace))]
    private void OpenRecent(string? folder)
    {
        if (folder is null) return;
        if (!Directory.Exists(folder))
        {
            Forget(folder);
            _dialogs.ShowError("Can't open workspace", $"{folder} no longer exists, so it has been removed from the recent list.");
            return;
        }
        Open(folder);
    }

    /// <summary>Opens a workspace by path (also used for recent workspaces and tests).</summary>
    public void Open(string folder)
    {
        if (_workspace is not null && string.Equals(Path.GetFullPath(folder), _workspace.Root, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "That workspace is already open.";
            return;
        }
        try
        {
            Opened(Workspace.Open(folder, _capture.Codec));
        }
        catch (WorkspaceException ex) when (ex.Problem == WorkspaceProblem.Damaged)
        {
            bool manifest = string.Equals(Path.GetFileName(ex.DamagedFile), Workspace.ManifestFileName, StringComparison.OrdinalIgnoreCase);
            string consequence = manifest
                ? "Your captured frames are kept, but the boxes and labels stored in the damaged file can't be recovered."
                : "The workspace's settings go back to their defaults; frames, boxes and labels are kept.";
            if (!_dialogs.Confirm("Repair workspace?",
                    $"{ex.Message}\n\nScrimshaw can rebuild it. {consequence} The damaged file is kept next to it as a backup.\n\nRepair now?"))
                return;
            try
            {
                Opened(Workspace.OpenWithRepair(folder, _capture.Codec));
            }
            catch (Exception repairError) when (repairError is WorkspaceException or IOException or UnauthorizedAccessException)
            {
                _dialogs.ShowError("Can't repair workspace", repairError.Message);
            }
        }
        catch (WorkspaceException ex) when (ex.Problem == WorkspaceProblem.InUse)
        {
            _dialogs.ShowError("Workspace already open", $"{ex.Message} Close it there first, or switch to that window.");
        }
        catch (WorkspaceException ex) when (ex.Problem == WorkspaceProblem.NotAWorkspace)
        {
            _dialogs.ShowError("Can't open workspace", $"{ex.Message} To start a new one there, use New workspace… with an empty folder.");
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Can't open workspace", ex.Message);
        }
    }

    private void Opened(Workspace ws)
    {
        SwitchTo(ws);
        Remember(ws.Root);
        RecoveryReport r = ws.Recovery;
        if (r.SetAsideFiles.Count > 0)
            StatusMessage = string.Create(CultureInfo.CurrentCulture,
                $"Workspace repaired: {FrameCount} frames available. Damaged files kept as {string.Join(", ", r.SetAsideFiles.Select(Path.GetFileName))}.");
        else if (r.IsClean)
            StatusMessage = string.Create(CultureInfo.CurrentCulture, $"Opened workspace with {FrameCount} frames.");
        else
            StatusMessage = string.Create(CultureInfo.CurrentCulture,
                $"Opened workspace. Repaired after an interruption: {r.RecoveredFrames.Count} frames recovered, {r.MissingFiles.Count} image files missing, {r.UnreadableFiles.Count} unreadable.");
    }

    private void Remember(string folder)
    {
        string full = Path.GetFullPath(folder);
        AppSettings settings = _appSettings.Load().WithRecent(full);
        _appSettings.Save(settings);
        SyncRecent(settings);
    }

    private void Forget(string folder)
    {
        AppSettings settings = _appSettings.Load().WithoutRecent(folder);
        _appSettings.Save(settings);
        SyncRecent(settings);
    }

    private void SyncRecent(AppSettings settings)
    {
        RecentWorkspaces.Clear();
        foreach (string f in settings.Recent) RecentWorkspaces.Add(f);
        OnPropertyChanged(nameof(HasRecent));
    }

    [RelayCommand]
    private void RefreshTargets()
    {
        CaptureTargetDescriptor? keep = SelectedTarget?.Target.ToDescriptor() ?? _workspace?.Settings.Target;
        IReadOnlyList<CaptureTarget> all = _targets.GetAll();
        Targets.Clear();
        foreach (CaptureTarget t in all) Targets.Add(new TargetOption(t));
        CaptureTarget? match = CaptureTargets.Resolve(keep, all);
        SelectedTarget = match is null ? null : Targets.First(o => o.Target == match);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void StartCapture()
    {
        if (_workspace is null || SelectedTarget is null || !TryParseInterval(IntervalText, out double interval)) return;
        long? free = _workspace.FreeBytes;
        if (free < MinFreeBytes)
        {
            _dialogs.ShowError("Not enough disk space", string.Create(CultureInfo.CurrentCulture,
                $"Only {free / (1024.0 * 1024):0} MB is free on the workspace's drive. Free up some space, or use a workspace on another drive."));
            return;
        }
        try
        {
            SaveSettings(interval);
            _framesAtStart = FrameCount;
            _session = _capture.Start(_workspace, SelectedTarget.Target);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException or System.Runtime.InteropServices.COMException)
        {
            _dialogs.ShowError("Can't start capture", ex.Message);
            return;
        }

        _session.StatsChanged += OnStatsChanged;
        _session.FrameSaved += OnFrameSaved;
        Stats = CaptureStats.Empty;
        IsCapturing = true;
        StatusMessage = free < LowFreeBytes
            ? string.Create(CultureInfo.CurrentCulture, $"Capturing… note: only {free / (1024.0 * 1024 * 1024):0.0} GB free on this drive. Play the video now.")
            : "Capturing… play the video now. Stop when it ends.";
        _ = WatchAsync(_session);
    }

    [RelayCommand(CanExecute = nameof(IsCapturing))]
    private async Task StopCaptureAsync()
    {
        if (_session is { } s)
        {
            StatusMessage = "Stopping and saving the last frames…";
            await s.StopAsync();
        }
    }

    /// <summary>For the global hotkey (FR-C9): start if possible, otherwise stop.</summary>
    public void ToggleCapture()
    {
        if (IsCapturing) StopCaptureCommand.Execute(null);
        else if (StartCaptureCommand.CanExecute(null)) StartCaptureCommand.Execute(null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is { } s) await s.StopAsync();
        Review?.Dispose();
        Review = null;
        ExportTab?.Dispose();
        ExportTab = null;
        if (_workspace is not null) SaveSettingsQuietly();
        _workspace?.Dispose();
    }

    internal static bool TryParseInterval(string? text, out double seconds) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds)
         || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
        && seconds >= WorkspaceSettings.MinIntervalSeconds && seconds <= 60;

    partial void OnIntervalTextChanged(string value) =>
        IntervalError = TryParseInterval(value, out _)
            ? null
            : string.Create(CultureInfo.CurrentCulture, $"Enter seconds between {WorkspaceSettings.MinIntervalSeconds} and 60, e.g. 0.5 or 1.");

    partial void OnThresholdPercentChanged(double value)
    {
        double clamped = Math.Clamp(value, MinThresholdPercent, MaxThresholdPercent);
        if (clamped != value) ThresholdPercent = clamped;
        OnPropertyChanged(nameof(LastChangeText));
    }

    partial void OnStatsChanged(CaptureStats value)
    {
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(LastChangePercent));
        OnPropertyChanged(nameof(LastChangeText));
        OnPropertyChanged(nameof(TimingText));
    }

    private bool CanSwitchWorkspace() => !IsCapturing;

    private void SwitchTo(Workspace ws)
    {
        if (_workspace is not null) SaveSettingsQuietly();
        Review?.Dispose();
        ExportTab?.Dispose();
        _workspace?.Dispose();
        _workspace = ws;
        WorkspacePath = ws.Root;
        Review = new ReviewViewModel(ws, _thumbnails, _ui);
        Review.Message += (_, message) => StatusMessage = message;
        ExportTab = new ExportViewModel(ws, _ui, _shell, _dialogs);
        WorkspaceSettings s = ws.Settings;
        IntervalText = FormatInterval(s.IntervalSeconds);
        ThresholdPercent = s.Detection.AreaThreshold * 100;
        IsJpeg = s.ImageFormat == ImageFormat.Jpeg;
        JpegQuality = s.JpegQuality;
        IncludeCursor = s.IncludeCursor;
        StopOnTitleChange = s.StopOnTitleChange;
        Stats = CaptureStats.Empty;
        UpdateFrameInfo();
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(CanChangeFormat));
        OnPropertyChanged(nameof(CanStopOnTitleChange));
        StartCaptureCommand.NotifyCanExecuteChanged();
        if (s.Target is not null)
        {
            CaptureTarget? match = CaptureTargets.Resolve(s.Target, Targets.Select(t => t.Target).ToList());
            if (match is not null) SelectedTarget = Targets.First(o => o.Target == match);
        }
    }

    private void SaveSettings(double interval)
    {
        _workspace!.UpdateSettings(s => s with
        {
            IntervalSeconds = interval,
            Target = SelectedTarget?.Target.ToDescriptor() ?? s.Target,
            IncludeCursor = IncludeCursor,
            StopOnTitleChange = StopOnTitleChange,
            Detection = s.Detection with { AreaThreshold = ThresholdPercent / 100 },
            ImageFormat = CanChangeFormat ? (IsJpeg ? ImageFormat.Jpeg : ImageFormat.Png) : s.ImageFormat,
            JpegQuality = Math.Clamp(JpegQuality, 1, 100),
        });
    }

    private void SaveSettingsQuietly()
    {
        try
        {
            SaveSettings(TryParseInterval(IntervalText, out double i) ? i : _workspace!.Settings.IntervalSeconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // Settings are also saved at every capture start; losing an unsaved tweak on close is acceptable.
        }
    }

    private void UpdateFrameInfo()
    {
        IReadOnlyList<FrameRecord> frames = _workspace?.Manifest.Current.Frames ?? [];
        FrameCount = frames.Count;
        FrameRecord? last = frames.Count > 0 ? frames[^1] : null;
        LatestFramePath = last is null ? null : _workspace!.FullPath(last);
        LatestFrameSize = last is null ? null : string.Create(CultureInfo.CurrentCulture, $"{last.Width}×{last.Height}");
    }

    private void OnStatsChanged(object? sender, CaptureStats stats) => _ui.Post(() => Stats = stats);

    private void OnFrameSaved(object? sender, FrameRecord frame) => _ui.Post(() =>
    {
        LatestFramePath = null; // force the preview to reload: a replaced frame keeps its file name
        UpdateFrameInfo();
    });

    private async Task WatchAsync(CaptureSession session)
    {
        CaptureStopReason reason = await session.Completion.ConfigureAwait(false);
        _ui.Post(() =>
        {
            session.StatsChanged -= OnStatsChanged;
            session.FrameSaved -= OnFrameSaved;
            Stats = session.Stats;
            UpdateFrameInfo();
            int added = FrameCount - _framesAtStart;
            StatusMessage = reason switch
            {
                CaptureStopReason.TargetLost => string.Create(CultureInfo.CurrentCulture,
                    $"The capture target went away (window closed or monitor disconnected). Capture stopped; {added} new frames kept."),
                CaptureStopReason.Error => $"Capture stopped because frames couldn't be saved: {session.Error?.Message}",
                CaptureStopReason.TitleChanged => string.Create(CultureInfo.CurrentCulture,
                    $"Capture stopped automatically: the window's title changed to '{session.NewTitle}'. {added} new frames saved."),
                _ => string.Create(CultureInfo.CurrentCulture, $"Capture stopped. {added} new frames saved. Open the Review tab to box the music."),
            };
            _session = null;
            IsCapturing = false;
        });
    }

    private static string FormatInterval(double seconds) => seconds.ToString("0.###", CultureInfo.CurrentCulture);
}

/// <summary>Used when no thumbnail service is supplied (tests): the strip shows no pictures.</summary>
internal sealed class NoThumbnails : IThumbnailService
{
    public static readonly NoThumbnails Instance = new();

    public Task<string?> GetAsync(string framePath, string thumbsFolder, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
