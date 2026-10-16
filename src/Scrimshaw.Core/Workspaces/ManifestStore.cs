namespace Scrimshaw.Core.Workspaces;

/// <summary>
/// The single owner of <c>manifest.json</c>. Every change is applied in memory straight away and written
/// to disk shortly afterwards (debounced, atomic), so a crash loses at most the last fraction of a second
/// (FR-A6, NFR-5). Thread-safe: the capture writer and the UI can both use it.
/// </summary>
public sealed class ManifestStore : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly Lock _gate = new();
    private readonly Lock _writeGate = new();
    private Manifest _current;
    private long _version;
    private long _writtenVersion;
    private ITimer? _timer;
    private bool _disposed;

    private ManifestStore(string path, Manifest initial, TimeProvider time, TimeSpan debounce, bool alreadyOnDisk)
    {
        _path = path;
        _current = initial;
        _time = time;
        _debounce = debounce;
        _version = alreadyOnDisk ? 0 : 1;
    }

    /// <summary>Raised after any in-memory change (on the thread that made it).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when a background save fails. The store stays dirty and retries on the next change or flush.</summary>
    public event EventHandler<Exception>? SaveFailed;

    public Manifest Current
    {
        get { lock (_gate) return _current; }
    }

    public bool IsDirty
    {
        get { lock (_gate) return _version != _writtenVersion; }
    }

    public static ManifestStore Load(string path, TimeProvider? time = null, TimeSpan? debounce = null) =>
        new(path, ScrimshawJson.ReadManifest(path), time ?? TimeProvider.System, debounce ?? DefaultDebounce, alreadyOnDisk: true);

    /// <summary>Creates a new, empty manifest and writes it immediately.</summary>
    public static ManifestStore CreateNew(string path, TimeProvider? time = null, TimeSpan? debounce = null)
    {
        var store = new ManifestStore(path, new Manifest(), time ?? TimeProvider.System, debounce ?? DefaultDebounce, alreadyOnDisk: false);
        store.Flush();
        return store;
    }

    /// <summary>Allocates the next frame number. Reserved numbers are never reused, even if the frame is never added.</summary>
    public int ReserveSequence()
    {
        int seq = 0;
        Mutate(m =>
        {
            seq = m.NextSequence;
            return m with { NextSequence = seq + 1 };
        });
        return seq;
    }

    public void AddFrame(FrameRecord frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Mutate(m =>
        {
            if (m.Frames.Any(f => f.Seq == frame.Seq))
                throw new InvalidOperationException($"Frame {frame.Seq} already exists.");
            var frames = m.Frames.Append(frame).OrderBy(f => f.Seq).ToList();
            return m with { Frames = frames, NextSequence = Math.Max(m.NextSequence, frame.Seq + 1) };
        });
    }

    /// <exception cref="KeyNotFoundException">No frame has that sequence number.</exception>
    public FrameRecord UpdateFrame(int seq, Func<FrameRecord, FrameRecord> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        FrameRecord? result = null;
        Mutate(m =>
        {
            int i = IndexOf(m, seq);
            result = update(m.Frames[i]);
            if (result.Seq != seq) throw new InvalidOperationException("A frame's sequence number can't change.");
            var frames = m.Frames.ToList();
            frames[i] = result;
            return m with { Frames = frames };
        });
        return result!;
    }

    /// <summary>Applies <paramref name="update"/> to every frame in one change (e.g. "apply boxes to following").</summary>
    public void UpdateFrames(Func<FrameRecord, FrameRecord> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Mutate(m =>
        {
            var frames = m.Frames.Select(f =>
            {
                FrameRecord u = update(f);
                return u.Seq == f.Seq ? u : throw new InvalidOperationException("A frame's sequence number can't change.");
            }).ToList();
            return m with { Frames = frames };
        });
    }

    /// <summary>Writes pending changes now, if there are any.</summary>
    public void Flush()
    {
        lock (_writeGate)
        {
            Manifest snapshot;
            long version;
            lock (_gate)
            {
                if (_version == _writtenVersion) return;
                snapshot = _current;
                version = _version;
            }

            AtomicFile.WriteAllBytes(_path, ScrimshawJson.Serialize(snapshot));

            lock (_gate) _writtenVersion = Math.Max(_writtenVersion, version);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        Flush();
    }

    private void Mutate(Func<Manifest, Manifest> change)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current = change(_current);
            _version++;
            if (_timer is null)
                _timer = _time.CreateTimer(_ => SaveFromTimer(), null, _debounce, Timeout.InfiniteTimeSpan);
            else
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SaveFromTimer()
    {
        try
        {
            Flush();
        }
#pragma warning disable CA1031 // Background save: report rather than crash; the data stays in memory and dirty.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SaveFailed?.Invoke(this, ex);
        }
    }

    private static int IndexOf(Manifest m, int seq)
    {
        for (int i = 0; i < m.Frames.Count; i++)
            if (m.Frames[i].Seq == seq) return i;
        throw new KeyNotFoundException($"Frame {seq} not found.");
    }
}
