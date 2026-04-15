namespace MacExplorer.Services;

/// <summary>
/// Singleton bridge that allows platform-level code (e.g. Dock menu)
/// to navigate within the active window's ViewModel.
/// Each ViewModel instance registers/unregisters itself; the bridge
/// dispatches navigation to the most recently active instance.
/// </summary>
public class NavigationBridge
{
    private readonly List<WeakReference<ViewModels.FileListViewModel>> _viewModels = new();
    private WeakReference<ViewModels.FileListViewModel>? _activeVm;

    /// <summary>
    /// When true, the next window that initializes on the home page should
    /// auto-focus the navigation input. Set by "快速访达" Dock menu action.
    /// </summary>
    public bool PendingQuickAccessFocus { get; set; }

    /// <summary>
    /// When set, the next window that initializes should navigate to this path.
    /// Set by clicking a frequent folder in the Dock menu.
    /// </summary>
    public string? PendingNavigationPath { get; set; }

    /// <summary>
    /// Register a ViewModel instance (call on creation / page init).
    /// Note: Does NOT auto-navigate on pending path — that is handled
    /// exclusively by Home.razor.OnInitialized to avoid race conditions.
    /// </summary>
    public void Register(ViewModels.FileListViewModel vm)
    {
        lock (_viewModels)
        {
            // Remove stale references
            _viewModels.RemoveAll(wr => !wr.TryGetTarget(out _));
            // Add if not already present
            if (!_viewModels.Any(wr => wr.TryGetTarget(out var existing) && ReferenceEquals(existing, vm)))
            {
                _viewModels.Add(new WeakReference<ViewModels.FileListViewModel>(vm));
            }
            _activeVm = new WeakReference<ViewModels.FileListViewModel>(vm);
        }

        Console.WriteLine($"[MacExplorer] NavigationBridge.Register: PendingNavigationPath='{PendingNavigationPath}' (not consuming here, OnInitialized will handle)");
    }

    /// <summary>
    /// Mark a ViewModel as the currently active one (call on window focus).
    /// </summary>
    public void SetActive(ViewModels.FileListViewModel vm)
    {
        lock (_viewModels)
        {
            _activeVm = new WeakReference<ViewModels.FileListViewModel>(vm);
        }
    }

    /// <summary>
    /// Unregister a ViewModel instance (call on disposal).
    /// </summary>
    public void Unregister(ViewModels.FileListViewModel vm)
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

    /// <summary>
    /// Navigate to a folder in the active ViewModel (or the first available one).
    /// If no ViewModel is registered yet, stores the path for later consumption.
    /// </summary>
    public async Task NavigateAsync(string path)
    {
        Console.WriteLine($"[MacExplorer] NavigationBridge.NavigateAsync: path='{path}'");

        ViewModels.FileListViewModel? target = null;
        lock (_viewModels)
        {
            Console.WriteLine($"[MacExplorer] NavigationBridge.NavigateAsync: registered VMs={_viewModels.Count}, hasActiveVm={_activeVm != null}");

            // Prefer the active VM
            if (_activeVm?.TryGetTarget(out var active) == true)
            {
                target = active;
            }
            else
            {
                // Fall back to the last registered VM
                for (int i = _viewModels.Count - 1; i >= 0; i--)
                {
                    if (_viewModels[i].TryGetTarget(out var vm))
                    {
                        target = vm;
                        break;
                    }
                }
            }
        }

        if (target != null)
        {
            Console.WriteLine($"[MacExplorer] NavigationBridge.NavigateAsync: found target VM, navigating to '{path}'");
            // 成功导航后清除 PendingNavigationPath，避免 Home.razor.OnInitialized 重复导航
            PendingNavigationPath = null;
            await target.NavigateToCommand.ExecuteAsync(path);
        }
        else
        {
            Console.WriteLine($"[MacExplorer] NavigationBridge.NavigateAsync: no VM registered, queuing path='{path}'");
            // No ViewModel registered yet — queue the path for later
            PendingNavigationPath = path;
        }
    }
}
