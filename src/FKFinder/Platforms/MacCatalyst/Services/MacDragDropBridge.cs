using System.Runtime.InteropServices;
using FKFinder.Services;
using FKFinder.ViewModels;
using ObjCRuntime;
using WebKit;

namespace FKFinder.Platforms.MacCatalyst.Services;

/// <summary>
/// Mac Catalyst implementation of IDragDropBridge.
/// Follows the NavigationBridge pattern with weak references to manage multiple window ViewModels.
/// File move logic for external drops is handled here (not in ViewModels) to avoid broadcast race conditions.
/// </summary>
public class MacDragDropBridge : IDragDropBridge
{
    private readonly List<WeakReference<FileListViewModel>> _viewModels = new();
    private WeakReference<FileListViewModel>? _activeVm;

    /// <summary>Maps NSWindow handle to the corresponding ViewModel for window-specific operations.</summary>
    private readonly Dictionary<IntPtr, WeakReference<FileListViewModel>> _windowToViewModel = new();

    private readonly IFileService _fileService;
    private readonly IDirectoryChangeNotifier _directoryChangeNotifier;

    public MacDragDropBridge(IFileService fileService, IDirectoryChangeNotifier directoryChangeNotifier)
    {
        _fileService = fileService;
        _directoryChangeNotifier = directoryChangeNotifier;
    }

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
        
        // Also register window mapping using current keyWindow
        // This is called from Home.razor.OnInitialized when the window is active
        RegisterViewModelForCurrentWindow(vm);
    }

    /// <summary>
    /// Registers the NSWindow to ViewModel mapping for window-specific drag-drop operations.
    /// Call this to associate the window with this window's ViewModel.
    /// </summary>
    public void RegisterWindow(IntPtr nsWindow, FileListViewModel vm)
    {
        if (nsWindow == IntPtr.Zero) return;
        
        lock (_windowToViewModel)
        {
            _windowToViewModel[nsWindow] = new WeakReference<FileListViewModel>(vm);
        }
    }

    /// <summary>
    /// Registers the current window's ViewModel for window-specific drag-drop operations.
    /// This uses the keyWindow to determine which window is calling this method.
    /// </summary>
    public void RegisterViewModelForCurrentWindow(FileListViewModel vm)
    {
        // Get the current key window's NSWindow
        var nsWindow = GetCurrentNSWindow();
        if (nsWindow == IntPtr.Zero) return;

        // Register the mapping directly using NSWindow
        lock (_windowToViewModel)
        {
            _windowToViewModel[nsWindow] = new WeakReference<FileListViewModel>(vm);
        }
    }

    private static IntPtr GetCurrentNSWindow()
    {
        try
        {
            var nsAppClass = ObjCRuntime.Class.GetHandle("NSApplication");
            if (nsAppClass == IntPtr.Zero) return IntPtr.Zero;

            var sharedApp = objc_msgSend(nsAppClass, Selector.GetHandle("sharedApplication"));
            if (sharedApp == IntPtr.Zero) return IntPtr.Zero;

            var keyWindow = objc_msgSend(sharedApp, Selector.GetHandle("keyWindow"));
            return keyWindow;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

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
        
        lock (_windowToViewModel)
        {
            var keysToRemove = _windowToViewModel
                .Where(kv => !kv.Value.TryGetTarget(out var target) || ReferenceEquals(target, vm))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in keysToRemove)
            {
                _windowToViewModel.Remove(key);
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

    public string GetCurrentDirectoryForWindow(string windowId)
    {
        // windowId is the NSWindow pointer value as string
        if (!IntPtr.TryParse(windowId, out var nsWindow))
            return GetCurrentDirectory();

        // Get the ViewModel associated with this NSWindow
        lock (_windowToViewModel)
        {
            if (_windowToViewModel.TryGetValue(nsWindow, out var weakRef))
            {
                if (weakRef.TryGetTarget(out var vm))
                    return vm.CurrentPath ?? "";
            }
        }

        // Fallback to active ViewModel if no mapping found
        return GetCurrentDirectory();
    }

    public async void HandleExternalDrop(string[] sourcePaths, string targetDirectory, IntPtr nsWindow)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (string.IsNullOrEmpty(targetDirectory)) return;

            var affectedDirs = new HashSet<string> { targetDirectory };
            int moved = 0;

            foreach (var sourcePath in sourcePaths)
            {
                var sourceDir = Path.GetDirectoryName(sourcePath);
                if (!string.IsNullOrEmpty(sourceDir)) affectedDirs.Add(sourceDir);
                if (sourceDir == targetDirectory) continue;

                try { await _fileService.MoveAsync(sourcePath, targetDirectory); moved++; }
                catch (Exception ex) { Console.WriteLine($"[FKFinder] Drop move failed: {ex.Message}"); }
            }

            if (moved > 0)
                _directoryChangeNotifier.NotifyChanged(affectedDirs.ToArray());
        });
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
