using MacExplorer.ViewModels;

namespace MacExplorer.Services.Impl;

/// <summary>
/// Singleton change router. Collects affected directory paths, debounces 200ms,
/// then dispatches RefreshFromNotification to all registered ViewModels whose
/// CurrentPath matches a changed directory.
/// </summary>
public class DirectoryChangeNotifier : IDirectoryChangeNotifier
{
    private readonly object _lock = new();
    private readonly List<WeakReference<FileListViewModel>> _viewModels = new();
    private readonly HashSet<string> _pendingChanges = new(StringComparer.Ordinal);
    private readonly HashSet<FileListViewModel> _excludedVms = new(ReferenceEqualityComparer.Instance);
    private Timer? _debounceTimer;

    public void Subscribe(FileListViewModel vm)
    {
        lock (_lock)
        {
            // Clean up dead references
            _viewModels.RemoveAll(wr => !wr.TryGetTarget(out _));
            if (!_viewModels.Any(wr => wr.TryGetTarget(out var existing) && ReferenceEquals(existing, vm)))
                _viewModels.Add(new WeakReference<FileListViewModel>(vm));
        }
    }

    public void Unsubscribe(FileListViewModel vm)
    {
        lock (_lock)
        {
            _viewModels.RemoveAll(wr => !wr.TryGetTarget(out var t) || ReferenceEquals(t, vm));
            _excludedVms.Remove(vm);
        }
    }

    public void NotifyChanged(string[] directoryPaths, FileListViewModel? excludeVm = null)
    {
        if (directoryPaths.Length == 0) return;

        lock (_lock)
        {
            foreach (var dir in directoryPaths)
            {
                if (!string.IsNullOrEmpty(dir))
                    _pendingChanges.Add(dir);
            }
            if (excludeVm != null)
                _excludedVms.Add(excludeVm);

            // Reset the debounce timer (200ms)
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(OnDebounceElapsed, null, 200, Timeout.Infinite);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        HashSet<string> dirs;
        HashSet<FileListViewModel> excluded;
        List<FileListViewModel> targets = new();

        lock (_lock)
        {
            // Snapshot and clear
            dirs = new HashSet<string>(_pendingChanges, StringComparer.Ordinal);
            excluded = new HashSet<FileListViewModel>(_excludedVms, ReferenceEqualityComparer.Instance);
            _pendingChanges.Clear();
            _excludedVms.Clear();
            _debounceTimer?.Dispose();
            _debounceTimer = null;

            // Find matching VMs
            for (int i = _viewModels.Count - 1; i >= 0; i--)
            {
                if (!_viewModels[i].TryGetTarget(out var vm))
                {
                    _viewModels.RemoveAt(i);
                    continue;
                }

                if (excluded.Contains(vm)) continue;
                if (vm.IsHomePage || vm.IsArchiveView || vm.IsAiView || vm.IsCollectionView) continue;
                if (string.IsNullOrEmpty(vm.CurrentPath)) continue;
                if (!dirs.Contains(vm.CurrentPath)) continue;

                targets.Add(vm);
            }
        }

        if (targets.Count == 0) return;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            foreach (var vm in targets)
            {
                try
                {
                    await vm.RefreshFromNotification();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MacExplorer] DirectoryChangeNotifier refresh failed: {ex.Message}");
                }
            }
        });
    }
}
