using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MacExplorer.Models;

namespace MacExplorer.Views;

internal static class HomeScriptMenuIcons
{
    public static PathIcon? Create(HomeScriptMenuIcon kind)
    {
        var path = kind switch
        {
            HomeScriptMenuIcon.Start => Assets.Icons.Play,
            HomeScriptMenuIcon.Stop => Assets.Icons.Stop,
            _ => null
        };
        if (path == null) return null;
        return new PathIcon
        {
            Data = Geometry.Parse(path), Width = 16, Height = 16,
            [!PathIcon.ForegroundProperty] = new DynamicResourceExtension(
                kind == HomeScriptMenuIcon.Start ? "SuccessBrush" : "DangerBrush")
        };
    }
}
