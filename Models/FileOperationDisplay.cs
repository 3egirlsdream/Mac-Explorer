namespace MacExplorer.Models;

internal enum FileOperationPresentation { Copy, Paste, Move, Trash, Delete, EmptyTrash }

/// <summary>Progress for one user gesture, including time spent waiting for an execution slot.</summary>
internal sealed class FileOperationDisplay(FileOperationPresentation kind, TimeProvider? time = null)
{
    private readonly TimeProvider _clock = time ?? TimeProvider.System;
    private readonly long _start = (time ?? TimeProvider.System).GetTimestamp();
    private readonly object _gate = new();
    private long? _confirmation;
    private TimeSpan _excluded;

    public FileOperationPresentation Kind { get; } = kind;
    public bool Preparing { get; set; } = true;
    public bool FinderRunning { get; set; }
    public bool ByItems { get; set; }
    public int TotalItems { get; set; }
    public int DoneItems { get; set; }
    public long TotalBytes { get; set; }
    public long DoneBytes { get; set; }
    public bool Confirming { get { lock (_gate) return _confirmation.HasValue; } }
    public bool ShouldShow(BackgroundTaskState state)
    {
        if (state is BackgroundTaskState.Completed or BackgroundTaskState.Cancelled) return false;
        if (state is BackgroundTaskState.Failed or BackgroundTaskState.NeedsAttention) return true;
        lock (_gate) return !_confirmation.HasValue && _clock.GetElapsedTime(_start) - _excluded >= TimeSpan.FromMilliseconds(800);
    }
    public void BeginConfirmation() { lock (_gate) _confirmation = _clock.GetTimestamp(); }
    public void EndConfirmation()
    {
        lock (_gate)
        {
            if (_confirmation is { } start) _excluded += _clock.GetElapsedTime(start);
            _confirmation = null;
        }
    }
}
