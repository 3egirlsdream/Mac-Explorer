using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Platforms.MacCatalyst.Handlers;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacNativeContextMenuService : INativeContextMenuService
{
    private IReadOnlyList<ContextMenuAction>? _currentActions;
    private int _pendingActionIndex = -1;

    public MacNativeContextMenuService()
    {
        ContextMenuHelper.Register(OnMenuItemClicked);
    }

    public void ShowContextMenu(IReadOnlyList<ContextMenuAction> actions, double webViewX, double webViewY)
    {
        _currentActions = actions;
        _pendingActionIndex = -1;
        var menu = ContextMenuHelper.BuildMenu(actions);
        if (menu != IntPtr.Zero)
            ContextMenuHelper.ShowMenuAtLocation(menu, webViewX, webViewY);

        // popUpMenu returned — menu is fully dismissed. Execute pending action now.
        if (_pendingActionIndex >= 0 && _currentActions != null)
        {
            var idx = _pendingActionIndex;
            _pendingActionIndex = -1;
            var action = _currentActions[idx];
            if (action.IsEnabled && action.Execute != null)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try { await action.Execute(); }
                    catch (Exception ex) { Console.WriteLine($"[MacExplorer] Menu action error: {ex.Message}"); }
                });
            }
        }
    }

    private void OnMenuItemClicked(int actionIndex)
    {
        // Only record the index — do NOT execute within the modal menu loop
        _pendingActionIndex = actionIndex;
    }
}
