using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacDragDropBridge : IDragDropService, IDragDropBridge
{
    private readonly IFileService _fileService;
    private readonly IDirectoryChangeNotifier _directoryChangeNotifier;
    private readonly IRemoteFileEditService? _remoteFileEditService;
    private readonly IRemoteConnectionService? _connectionService;
    private readonly List<WeakReference<FileListViewModel>> _viewModels = [];
    private WeakReference<FileListViewModel>? _activeViewModel;
    private string? _dragState;

    public MacDragDropBridge(
        IFileService fileService,
        IDirectoryChangeNotifier directoryChangeNotifier,
        IRemoteFileEditService? remoteFileEditService = null,
        IRemoteConnectionService? connectionService = null)
    {
        _fileService = fileService;
        _directoryChangeNotifier = directoryChangeNotifier;
        _remoteFileEditService = remoteFileEditService;
        _connectionService = connectionService;
    }

    public string? GetDragState() => _dragState;
    public void SetDragState(string? state) => _dragState = state;
    public Task InitializeAsync(FileListViewModel viewModel) => Task.CompletedTask;
    public void Register(FileListViewModel vm)
    {
        lock (_viewModels)
        {
            _viewModels.RemoveAll(reference => !reference.TryGetTarget(out _));
            if (!_viewModels.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, vm)))
                _viewModels.Add(new WeakReference<FileListViewModel>(vm));
            _activeViewModel = new WeakReference<FileListViewModel>(vm);
        }
    }

    public void SetActive(FileListViewModel vm)
    {
        lock (_viewModels)
            _activeViewModel = new WeakReference<FileListViewModel>(vm);
    }

    public void Unregister(FileListViewModel vm)
    {
        lock (_viewModels)
        {
            _viewModels.RemoveAll(reference => !reference.TryGetTarget(out var target) || ReferenceEquals(target, vm));
            if (_activeViewModel?.TryGetTarget(out var active) == true && ReferenceEquals(active, vm))
                _activeViewModel = _viewModels.LastOrDefault();
        }
    }

    public string GetCurrentDirectory() => GetActiveViewModel()?.CurrentPath ?? "";
    public string GetCurrentDirectoryForWindow(string windowId) => GetCurrentDirectory();
    public async void HandleExternalDrop(string[] sourcePaths, string targetDirectory, IntPtr nsWindow)
        => await HandleDropSafelyAsync(sourcePaths, targetDirectory, forceMove: false);
    public async void HandleInternalDrop(string[] sourcePaths, string targetDirectory, IntPtr nsWindow)
        => await HandleDropSafelyAsync(sourcePaths, targetDirectory, forceMove: true);
    public string? GetDropTargetPathFromPoint(double x, double y, string currentDirectory) => null;
    public string[] GetDragFilePaths()
    {
        var vm = GetActiveViewModel();
        if (vm == null) return [];

        var paths = vm.SelectedEntries.Select(entry => entry.FullPath).ToArray();

        // For remote files, download to temp for native drag
        var remotePaths = paths.Where(p => VirtualPath.IsRemotePath(p)).ToArray();
        if (remotePaths.Length == 0) return paths;

        var result = new List<string>();
        foreach (var path in paths)
        {
            if (!VirtualPath.IsRemotePath(path))
            {
                result.Add(path);
                continue;
            }

            // Download remote file to temp for drag
            try
            {
                var (serverId, remotePath) = VirtualPath.ParseRemotePath(path);
                if (_remoteFileEditService != null)
                {
                    var localPath = _remoteFileEditService.DownloadForEditAsync(remotePath, serverId).GetAwaiter().GetResult();
                    result.Add(localPath);
                }
            }
            catch
            {
                // Skip files that can't be downloaded
            }
        }
        return result.ToArray();
    }

    public async Task<bool> DropFilesAsync(string[] sourcePaths, string targetDirectory, bool forceCopy, bool forceMove)
    {
        if (sourcePaths.Length == 0) return false;

        // For remote targets, skip Directory.Exists check
        var isRemoteTarget = VirtualPath.IsRemotePath(targetDirectory);
        if (!isRemoteTarget && !Directory.Exists(targetDirectory)) return false;

        var sourceEntries = new List<FileSystemEntry>();
        foreach (var sourcePath in sourcePaths.Distinct(StringComparer.Ordinal))
        {
            var entry = await _fileService.GetEntryAsync(sourcePath);
            if (entry != null) sourceEntries.Add(entry);
        }
        if (sourceEntries.Count == 0) return false;

        // Remote targets or remote sources always copy (never move)
        var hasRemoteSource = sourceEntries.Any(e => VirtualPath.IsRemotePath(e.FullPath));
        var shouldCopy = forceCopy || isRemoteTarget || hasRemoteSource || (!forceMove && !IsSameVolume(sourceEntries[0].FullPath, targetDirectory));
        if (shouldCopy)
        {
            foreach (var entry in sourceEntries)
                await _fileService.CopyAsync(entry.FullPath, targetDirectory);
            _directoryChangeNotifier.NotifyChanged([targetDirectory], null);
            return true;
        }

        var targetEntry = await _fileService.GetEntryAsync(targetDirectory)
            ?? new FileSystemEntry
            {
                FullPath = targetDirectory,
                Name = Path.GetFileName(targetDirectory),
                IsDirectory = true
            };
        var vm = GetActiveViewModel();
        if (vm != null)
        {
            await vm.MoveEntriesAsync(sourceEntries, targetEntry);
            return true;
        }

        return false;
    }

    public bool IsSameVolume(string path1, string path2) => !_fileService.IsCrossVolume(path1, path2);

    private async Task HandleDropSafelyAsync(string[] sourcePaths, string targetDirectory, bool forceMove)
    {
        try
        {
            await DropFilesAsync(sourcePaths, targetDirectory, forceCopy: false, forceMove: forceMove);
        }
        catch
        {
            // Native drag callbacks are async void entry points; exceptions must not escape
            // to Avalonia's UI synchronization context.
        }
    }

    private FileListViewModel? GetActiveViewModel()
    {
        lock (_viewModels)
        {
            if (_activeViewModel?.TryGetTarget(out var active) == true) return active;
            for (var index = _viewModels.Count - 1; index >= 0; index--)
                if (_viewModels[index].TryGetTarget(out var vm)) return vm;
            return null;
        }
    }
    public void Dispose() { }
}
