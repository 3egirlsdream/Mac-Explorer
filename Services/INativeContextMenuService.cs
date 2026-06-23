using MacExplorer.Models;

namespace MacExplorer.Services;

public interface INativeContextMenuService
{
    void ShowContextMenu(IReadOnlyList<ContextMenuAction> actions, double webViewX, double webViewY);
}
