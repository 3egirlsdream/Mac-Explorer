using MacExplorer.Indexing;
using MacExplorer.ViewModels;
using Microsoft.Extensions.Logging;
using Avalonia.Threading;

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
    private readonly Dictionary<string, HashSet<FileListViewModel>> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingChanges = new(StringComparer.Ordinal);
    private readonly HashSet<FileListViewModel> _excludedVms = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, DateTimeOffset> _suppressedRefreshUntil = new(StringComparer.Ordinal);
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
            foreach (var path in _expanded.Keys.ToArray())
            {
                _expanded[path].Remove(vm);
                if (_expanded[path].Count == 0) _expanded.Remove(path);
            }
        }
    }

    public void RegisterExpandedDirectory(FileListViewModel vm, string path)
    {
        path = NormalizeDirectoryPath(path);
        lock (_lock)
        {
            if (!_expanded.TryGetValue(path, out var subscribers))
                _expanded[path] = subscribers = new HashSet<FileListViewModel>(ReferenceEqualityComparer.Instance);
            subscribers.Add(vm);
        }
    }

    public void UnregisterExpandedDirectory(FileListViewModel vm, string path)
    {
        path = NormalizeDirectoryPath(path);
        lock (_lock)
        {
            if (!_expanded.TryGetValue(path, out var subscribers)) return;
            subscribers.Remove(vm);
            if (subscribers.Count == 0) _expanded.Remove(path);
        }
    }

    public bool IsExpandedDirectoryWatched(string path)
    {
        lock (_lock) return _expanded.ContainsKey(NormalizeDirectoryPath(path));
    }

    public IReadOnlyList<string> GetExpandedDirectoriesUnder(string path)
    {
        var prefix = NormalizeDirectoryPath(path).TrimEnd('/') + '/';
        lock (_lock)
            return _expanded.Keys.Where(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
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
            var now = DateTimeOffset.UtcNow;
            foreach (var dir in normalizedPaths)
            {
                if (_suppressedRefreshUntil.TryGetValue(dir, out var suppressedUntil))
                {
                    if (suppressedUntil > now)
                        continue;

                    _suppressedRefreshUntil.Remove(dir);
                }

                _pendingChanges.Add(dir);
            }

            if (_pendingChanges.Count == 0)
                return;

            if (excludeVm != null)
                _excludedVms.Add(excludeVm);

            // Reset the debounce timer (200ms)
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(OnDebounceElapsed, null, 200, Timeout.Infinite);
        }
    }

    public void SuppressRefresh(string[] directoryPaths, TimeSpan duration)
    {
        if (directoryPaths.Length == 0 || duration <= TimeSpan.Zero) return;

        var normalizedPaths = directoryPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizeDirectoryPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedPaths.Length == 0) return;

        var until = DateTimeOffset.UtcNow.Add(duration);
        lock (_lock)
        {
            foreach (var dir in normalizedPaths)
                _suppressedRefreshUntil[dir] = until;
        }
    }

    private void InvalidateIndex(string[] directoryPaths)
    {
        if (_fileIndexWriter == null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _fileIndexWriter.InvalidateDirectoriesAsync(directoryPaths);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to invalidate directory index");
            }
        });
    }

    private void OnDebounceElapsed(object? state)
    {
        HashSet<string> dirs;
        HashSet<FileListViewModel> excluded;
        List<(FileListViewModel Vm, string Path)> targets = new();

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

                if (vm.IsHomePage) continue;
                if (string.IsNullOrEmpty(vm.CurrentPath)) continue;
                var currentPath = NormalizeDirectoryPath(vm.CurrentPath);
                if (dirs.Contains(currentPath) && !excluded.Contains(vm))
                {
                    // Root refresh reloads open branches; avoid submitting those paths twice.
                    targets.Add((vm, currentPath));
                    continue;
                }
                foreach (var path in dirs)
                    if (_expanded.TryGetValue(path, out var subscribers) && subscribers.Contains(vm))
                        targets.Add((vm, path));
            }
        }

        if (targets.Count == 0) return;

        Dispatcher.UIThread.Post(async () =>
        {
            foreach (var (vm, path) in targets)
            {
                try
                {
                    if (string.Equals(path, NormalizeDirectoryPath(vm.CurrentPath), StringComparison.Ordinal))
                        await vm.RefreshFromNotification();
                    else
                        await vm.RefreshExpandedDirectoryFromNotificationAsync(path);
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
        if (VirtualPath.IsRemotePath(path))
        {
            var (serverId, remotePath) = VirtualPath.ParseRemotePath(path);
            var normalizedRemotePath = remotePath == "/"
                ? "/"
                : remotePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return VirtualPath.BuildRemotePath(serverId, normalizedRemotePath);
        }

        if (path == "/") return path;
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
