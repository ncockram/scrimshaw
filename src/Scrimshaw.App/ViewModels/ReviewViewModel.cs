using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scrimshaw.App.Services;
using Scrimshaw.Core.Annotation;
using Scrimshaw.Core.Workspaces;

namespace Scrimshaw.App.ViewModels;

/// <summary>A frame in the thumbnail strip (FR-A1, A8, A9).</summary>
public sealed partial class FrameItemViewModel : ObservableObject
{
    public FrameItemViewModel(FrameRecord frame, string path)
    {
        Seq = frame.Seq;
        Path = path;
        Update(frame);
    }

    public int Seq { get; }
    public string Path { get; }

    [ObservableProperty]
    public partial string? ThumbPath { get; set; }

    [ObservableProperty]
    public partial int BoxCount { get; private set; }

    [ObservableProperty]
    public partial bool Deleted { get; private set; }

    [ObservableProperty]
    public partial string Size { get; private set; } = "";

    [ObservableProperty]
    public partial string CapturedAt { get; private set; } = "";

    /// <summary>Changes when the frame's image is replaced, so views reload it.</summary>
    [ObservableProperty]
    public partial string ImageVersion { get; private set; } = "";

    /// <returns>True if the image itself changed.</returns>
    internal bool Update(FrameRecord frame)
    {
        BoxCount = frame.Boxes.Count;
        Deleted = frame.Deleted;
        Size = string.Create(CultureInfo.CurrentCulture, $"{frame.Width}×{frame.Height}");
        CapturedAt = frame.CapturedAt.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
        bool imageChanged = ImageVersion.Length > 0 && ImageVersion != frame.DHash;
        ImageVersion = frame.DHash;
        return imageChanged;
    }
}

/// <summary>A box on the current frame, in frame pixels.</summary>
public sealed record BoxViewModel(string Id, int Number, PixelRect Rect, string? Label)
{
    public string Title => Label is { Length: > 0 }
        ? string.Create(CultureInfo.CurrentCulture, $"{Number}. {Label}")
        : string.Create(CultureInfo.CurrentCulture, $"{Number}. Box");

    public string Detail => string.Create(CultureInfo.CurrentCulture, $"{Rect.W}×{Rect.H} at {Rect.X}, {Rect.Y}");
}

/// <summary>What the drawing surface needs from its owner.</summary>
public interface IAnnotationHost
{
    void AddBox(PixelRect rect);
    void UpdateBox(string id, PixelRect rect);
    void SelectBox(string? id);
}

/// <summary>The Review tab: browse frames, draw and edit boxes (FR-A1–A11).</summary>
public sealed partial class ReviewViewModel : ObservableObject, IAnnotationHost, IDisposable
{
    private readonly Workspace _workspace;
    private readonly IThumbnailService _thumbnails;
    private readonly IUiDispatcher _ui;
    private readonly AnnotationEditor _editor;
    private readonly CancellationTokenSource _closing = new();
    private bool _syncing;

    public ReviewViewModel(Workspace workspace, IThumbnailService thumbnails, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _thumbnails = thumbnails;
        _ui = ui;
        _editor = new AnnotationEditor(workspace.Manifest);
        // Edits happen on the UI thread, so reload straight away (not via the dispatcher): a newly drawn box
        // must be in Boxes before it can be selected.
        _editor.HistoryChanged += (_, _) =>
        {
            RefreshHistory();
            Sync();
        };
        workspace.Manifest.Changed += OnManifestChanged;
        Sync();
        SelectedFrame = Frames.FirstOrDefault(f => !f.Deleted) ?? Frames.FirstOrDefault();
    }

    /// <summary>Short confirmations for the status bar ("Applied boxes to 12 frames").</summary>
    public event EventHandler<string>? Message;

    public ObservableCollection<FrameItemViewModel> Frames { get; } = [];
    public ObservableCollection<BoxViewModel> Boxes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FramePosition), nameof(HasFrame))]
    [NotifyCanExecuteChangedFor(nameof(NextFrameCommand), nameof(PreviousFrameCommand), nameof(CopyFromPreviousCommand),
        nameof(ApplyToFollowingCommand), nameof(ToggleFrameDeletedCommand))]
    public partial FrameItemViewModel? SelectedFrame { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedBox))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedBoxCommand))]
    public partial BoxViewModel? SelectedBox { get; set; }

    /// <summary>The label being typed for the selected box; saved by <see cref="CommitLabelCommand"/>.</summary>
    [ObservableProperty]
    public partial string LabelText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    public partial string? UndoText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    public partial string? RedoText { get; private set; }

    public bool HasFrame => SelectedFrame is not null;
    public bool HasSelectedBox => SelectedBox is not null;

    public string FramePosition => SelectedFrame is null
        ? "No frames yet"
        : string.Create(CultureInfo.CurrentCulture,
            $"Frame {SelectedFrame.Seq} · {Frames.IndexOf(SelectedFrame) + 1} of {Frames.Count} · {SelectedFrame.Size} · captured {SelectedFrame.CapturedAt}{(SelectedFrame.Deleted ? " · deleted" : "")}");

    public void AddBox(PixelRect rect)
    {
        if (SelectedFrame is null) return;
        BoxRecord box = _editor.AddBox(SelectedFrame.Seq, rect);
        SelectBox(box.Id);
    }

    public void UpdateBox(string id, PixelRect rect)
    {
        if (SelectedFrame is null) return;
        _editor.UpdateBox(SelectedFrame.Seq, id, rect);
        SelectBox(id);
    }

    public void SelectBox(string? id) => SelectedBox = id is null ? null : Boxes.FirstOrDefault(b => b.Id == id);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextFrame() => Step(+1);

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousFrame() => Step(-1);

    [RelayCommand(CanExecute = nameof(HasSelectedBox))]
    private void DeleteSelectedBox()
    {
        if (SelectedFrame is null || SelectedBox is null) return;
        _editor.DeleteBox(SelectedFrame.Seq, SelectedBox.Id);
    }

    [RelayCommand]
    private void CommitLabel()
    {
        if (SelectedFrame is null || SelectedBox is null) return;
        _editor.SetLabel(SelectedFrame.Seq, SelectedBox.Id, LabelText);
    }

    [RelayCommand(CanExecute = nameof(HasFrame))]
    private void ToggleFrameDeleted()
    {
        if (SelectedFrame is not { } f) return;
        _editor.SetDeleted(f.Seq, !f.Deleted);
        Message?.Invoke(this, f.Deleted
            ? string.Create(CultureInfo.CurrentCulture, $"Frame {f.Seq} deleted. It won't be exported; Ctrl+D restores it.")
            : string.Create(CultureInfo.CurrentCulture, $"Frame {f.Seq} restored."));
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void CopyFromPrevious()
    {
        if (SelectedFrame is null) return;
        int added = _editor.CopyFromPrevious(SelectedFrame.Seq);
        Message?.Invoke(this, added == 0
            ? "No earlier frame has boxes to copy."
            : string.Create(CultureInfo.CurrentCulture, $"Copied {added} boxes from the previous boxed frame."));
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void ApplyToFollowing()
    {
        if (SelectedFrame is null) return;
        if (SelectedFrame.BoxCount == 0)
        {
            Message?.Invoke(this, "Draw boxes on this frame first, then apply them to the following frames.");
            return;
        }
        int frames = _editor.ApplyToFollowing(SelectedFrame.Seq);
        Message?.Invoke(this, frames == 0
            ? "Every following frame already has boxes (or is deleted)."
            : string.Create(CultureInfo.CurrentCulture, $"Applied this frame's boxes to {frames} following frames. Ctrl+Z undoes this in one step."));
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Show(_editor.Undo(), "Undid");

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Show(_editor.Redo(), "Redid");

    public void Dispose()
    {
        _workspace.Manifest.Changed -= OnManifestChanged;
        _closing.Cancel();
        _closing.Dispose();
    }

    private bool CanGoNext() => SelectedFrame is not null && Frames.IndexOf(SelectedFrame) < Frames.Count - 1;
    private bool CanGoPrevious() => SelectedFrame is not null && Frames.IndexOf(SelectedFrame) > 0;
    private bool CanUndo() => UndoText is not null;
    private bool CanRedo() => RedoText is not null;

    private void Step(int delta)
    {
        if (SelectedFrame is null) return;
        int i = Math.Clamp(Frames.IndexOf(SelectedFrame) + delta, 0, Frames.Count - 1);
        SelectedFrame = Frames[i];
    }

    private void Show(AnnotationEdit? edit, string verb)
    {
        if (edit is null) return;
        FrameItemViewModel? target = Frames.FirstOrDefault(f => f.Seq == edit.FocusSeq);
        if (target is not null && target != SelectedFrame) SelectedFrame = target;
        Message?.Invoke(this, $"{verb}: {edit.Description}.");
    }

    partial void OnSelectedFrameChanged(FrameItemViewModel? value)
    {
        LoadBoxes(keepSelection: false);
        OnPropertyChanged(nameof(FramePosition));
    }

    partial void OnSelectedBoxChanged(BoxViewModel? value) => LabelText = value?.Label ?? "";

    private void OnManifestChanged(object? sender, EventArgs e) => _ui.Post(Sync);

    /// <summary>Brings the frame list and current boxes in line with the manifest (edits, new captures, replaced images).</summary>
    private void Sync()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            IReadOnlyList<FrameRecord> frames = _workspace.Manifest.Current.Frames;
            var bySeq = Frames.ToDictionary(f => f.Seq);
            for (int i = 0; i < frames.Count; i++)
            {
                FrameRecord record = frames[i];
                if (bySeq.TryGetValue(record.Seq, out FrameItemViewModel? item))
                {
                    if (item.Update(record)) RequestThumbnail(item);
                }
                else
                {
                    item = new FrameItemViewModel(record, _workspace.FullPath(record));
                    Frames.Insert(Math.Min(i, Frames.Count), item);
                    RequestThumbnail(item);
                }
            }
            SelectedFrame ??= Frames.FirstOrDefault();
            LoadBoxes(keepSelection: true);
            OnPropertyChanged(nameof(FramePosition));
            NextFrameCommand.NotifyCanExecuteChanged();
            ApplyToFollowingCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void LoadBoxes(bool keepSelection)
    {
        string? selected = keepSelection ? SelectedBox?.Id : null;
        Boxes.Clear();
        if (SelectedFrame is null)
        {
            SelectedBox = null;
            return;
        }
        FrameRecord frame = _editor.Frame(SelectedFrame.Seq);
        int n = 1;
        foreach (BoxRecord b in frame.Boxes.OrderBy(b => b.Order))
            Boxes.Add(new BoxViewModel(b.Id, n++, new PixelRect(b.X, b.Y, b.W, b.H), b.Label));
        SelectedBox = selected is null ? null : Boxes.FirstOrDefault(b => b.Id == selected);
    }

    private void RefreshHistory()
    {
        UndoText = _editor.UndoDescription;
        RedoText = _editor.RedoDescription;
    }

    private void RequestThumbnail(FrameItemViewModel item)
    {
        CancellationToken token = _closing.Token;
        _ = Task.Run(async () =>
        {
            string? thumb = await _thumbnails.GetAsync(item.Path, _workspace.ThumbsPath, token).ConfigureAwait(false);
            if (thumb is not null && !token.IsCancellationRequested)
                _ui.Post(() =>
                {
                    item.ThumbPath = null; // a refreshed thumbnail keeps its file name
                    item.ThumbPath = thumb;
                });
        }, token);
    }
}
