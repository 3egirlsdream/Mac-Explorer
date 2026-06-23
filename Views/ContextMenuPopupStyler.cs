using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MacExplorer.Views;

internal static class ContextMenuPopupStyler
{
    private const string SubmenuSurfaceClass = "context-submenu-surface";

    public static void Attach(MenuItem item)
    {
        item.AttachedToVisualTree += (_, _) => Apply(item);
        item.SubmenuOpened += (_, _) =>
            Dispatcher.UIThread.Post(() => Apply(item), DispatcherPriority.Loaded);
    }

    private static void Apply(MenuItem item)
    {
        item.ApplyTemplate();
        foreach (var popup in item.GetVisualDescendants().OfType<Popup>())
        {
            popup.WindowManagerAddShadowHint = false;
            ApplySurfaceClass(popup);
        }

        foreach (var popup in item.GetTemplateDescendants().OfType<Popup>())
        {
            popup.WindowManagerAddShadowHint = false;
            ApplySurfaceClass(popup);
        }
    }

    private static void ApplySurfaceClass(Popup popup)
    {
        switch (popup.Child)
        {
            case Border border:
                border.Classes.Add(SubmenuSurfaceClass);
                break;
            case Control child:
                var surface = new Border { Child = child };
                surface.Classes.Add(SubmenuSurfaceClass);
                popup.Child = surface;
                break;
        }
    }
}
