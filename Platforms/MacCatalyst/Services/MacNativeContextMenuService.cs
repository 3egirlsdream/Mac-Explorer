using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacNativeContextMenuService : INativeContextMenuService
{
    public void ShowContextMenu(IReadOnlyList<ContextMenuAction> actions, double x, double y) { }
    public void DismissContextMenu() { }
}
