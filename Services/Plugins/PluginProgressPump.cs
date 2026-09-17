using MacExplorer.PluginSdk;

namespace MacExplorer.Services.Plugins;

/// <summary>Keep at most one queued UI update and reject callbacks after a run ends.</summary>
internal sealed class PluginProgressPump(Action<Action> post, Action<PluginProgress> apply) : IDisposable
{
    private readonly object _gate = new();
    private PluginProgress? _pending;
    private bool _queued;
    private bool _stopped;

    public void Report(PluginProgress progress)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _pending = progress with
            {
                ShowInTaskPanel = progress.ShowInTaskPanel || _pending?.ShowInTaskPanel == true,
                TaskTitle = !string.IsNullOrWhiteSpace(_pending?.TaskTitle) ? _pending.TaskTitle : progress.TaskTitle
            };
            if (_queued) return;
            _queued = true;
        }
        post(Drain);
    }

    private void Drain()
    {
        lock (_gate)
        {
            _queued = false;
            if (_stopped || _pending == null) return;
            var progress = _pending;
            _pending = null;
            apply(progress);
        }
    }

    // Called on the UI thread on success so a fast task's final progress/task-panel
    // request is not lost merely because its queued callback has not run yet.
    public void Complete()
    {
        lock (_gate)
        {
            if (_stopped) return;
            Drain();
            _stopped = true;
        }
    }

    public void Dispose()
    {
        lock (_gate) { _stopped = true; _pending = null; }
    }
}
