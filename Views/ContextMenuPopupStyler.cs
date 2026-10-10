using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using MacExplorer.Controls;

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
        if (item.GetTemplateDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Name == "PART_InputGestureText") is { } gesture
            && gesture.GetVisualParent() is Grid grid && !grid.Children.OfType<ShortcutText>().Any())
        {
            // Keep Fluent's converter and state brushes as the binding source.
            var label = new ShortcutText { Margin = gesture.Margin,
                HorizontalAlignment = gesture.HorizontalAlignment, VerticalAlignment = gesture.VerticalAlignment };
            label.Bind(TextBlock.TextProperty, gesture.GetObservable(TextBlock.TextProperty));
            label.Bind(TextBlock.FontFamilyProperty, gesture.GetObservable(TextBlock.FontFamilyProperty));
            label.Bind(TextBlock.FontSizeProperty, gesture.GetObservable(TextBlock.FontSizeProperty));
            label.Bind(TextBlock.ForegroundProperty, gesture.GetObservable(TextBlock.ForegroundProperty));
            Grid.SetColumn(label, Grid.GetColumn(gesture));
            Grid.SetRow(label, Grid.GetRow(gesture));
            gesture.IsVisible = false;
            grid.Children.Add(label);
        }
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
