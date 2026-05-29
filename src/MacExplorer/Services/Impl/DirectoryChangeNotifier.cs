using MacExplorer.Indexing;
using MacExplorer.ViewModels;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

/// <summary>
/// Singleton change router. Collects affected directory paths, debounces 200ms,
/// then dispatches RefreshFromNotification to all registered ViewModels whose
/// CurrentPath matches a changed directory.
/// </summary>
public class DirectoryChangeNotifier : IDirectoryChangeNotifier
{
    private readonly IFileIndexWriter? _fileIndexWriter;
    private readonly ILogger<DirectoryChangeNotifier>? _logger;
    private readonly object _lock = new();
    private readonly List<WeakReference<FileListViewModel>> _viewModels = new();
    private readonly HashSet<string> _pendingChanges = new(StringComparer.Ordinal);
    private readonly HashSet<FileListViewModel> _excludedVms = new(ReferenceEqualityComparer.Instance);
    private Timer? _debounceTimer;

    public DirectoryChangeNotifier(
        IFileIndexWriter? fileIndexWriter = null,
        ILogger<DirectoryChangeNotifier>? logger = null)
    {
        _fileIndexWriter = fileIndexWriter;
        _logger = logger;
    }

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

        var normalizedPaths = directoryPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizeDirectoryPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedPaths.Length == 0) return;

        InvalidateIndex(normalizedPaths);

        lock (_lock)
        {
            foreach (var dir in normalizedPaths)
                _pendingChanges.Add(dir);
            if (excludeVm != null)
                _excludedVms.Add(excludeVm);

            // Reset the debounce timer (200ms)
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(OnDebounceElapsed, null, 200, Timeout.Infinite);
        }
    }

    private void InvalidateIndex(string[] directoryPaths)
    {
        if (_fileIndexWriter == null) return;

        try
        {
            Task.Run(() => _fileIndexWriter.InvalidateDirectoriesAsync(directoryPaths)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to invalidate directory index");
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

    private static string NormalizeDirectoryPath(string path)
    {
        if (path == "/") return path;
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
