using FKFinder.Models;

namespace FKFinder.Services;

public interface INativeContextMenuService
{
    void ShowContextMenu(IReadOnlyList<ContextMenuAction> actions, double webViewX, double webViewY);
}
