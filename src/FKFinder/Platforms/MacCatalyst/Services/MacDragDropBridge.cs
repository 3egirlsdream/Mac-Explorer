using FKFinder.Services;
using FKFinder.ViewModels;

namespace FKFinder.Platforms.MacCatalyst.Services;

/// <summary>
/// Mac Catalyst implementation of IDragDropBridge.
/// Follows the NavigationBridge pattern with weak references to manage multiple window ViewModels.
/// </summary>
public class MacDragDropBridge : IDragDropBridge
{
    private readonly List<WeakReference<FileListViewModel>> _viewModels = new();
    private WeakReference<FileListViewModel>? _activeVm;

    public event Action<string[], string>? ExternalDropReceived;

    public void Register(FileListViewModel vm)
    {
        lock (_viewModels)
        {
            _viewModels.RemoveAll(wr => !wr.TryGetTarget(out _));
            if (!_viewModels.Any(wr => wr.TryGetTarget(out var existing) && ReferenceEquals(existing, vm)))
            {
                _viewModels.Add(new WeakReference<FileListViewModel>(vm));
            }
            _activeVm = new WeakReference<FileListViewModel>(vm);
        }
    }

    public void SetActive(FileListViewModel vm)
    {
        lock (_viewModels)
        {
            _activeVm = new WeakReference<FileListViewModel>(vm);
        }
    }

    public void Unregister(FileListViewModel vm)
    {
        lock (_viewModels)
        {
            _viewModels.RemoveAll(wr => !wr.TryGetTarget(out var t) || ReferenceEquals(t, vm));
            if (_activeVm != null && _activeVm.TryGetTarget(out var active) && ReferenceEquals(active, vm))
            {
                _activeVm = _viewModels.LastOrDefault();
            }
        }
    }

    public string[] GetDragFilePaths()
    {
        var vm = GetActiveViewModel();
        if (vm == null) return [];

        return vm.SelectedEntries
            .Where(e => !e.IsVirtual)
            .Select(e => e.FullPath)
            .ToArray();
    }

    public string GetCurrentDirectory()
    {
        var vm = GetActiveViewModel();
        return vm?.CurrentPath ?? "";
    }

    public void NotifyExternalDrop(string[] sourcePaths, string targetDirectory)
    {
        ExternalDropReceived?.Invoke(sourcePaths, targetDirectory);
    }

    private FileListViewModel? GetActiveViewModel()
    {
        lock (_viewModels)
        {
            if (_activeVm?.TryGetTarget(out var active) == true)
                return active;

            for (int i = _viewModels.Count - 1; i >= 0; i--)
            {
                if (_viewModels[i].TryGetTarget(out var vm))
                    return vm;
            }
        }
        return null;
    }
}
