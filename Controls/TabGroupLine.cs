using Avalonia.Controls;
using Avalonia.Media;
using MacExplorer.ViewModels;

namespace MacExplorer.Controls;

/// <summary>Group accent shared by settled headers and pointer-only drag previews.</summary>
public sealed class TabGroupLine : Border
{
    protected override Type StyleKeyOverride => typeof(TabGroupLine);
    internal TabGroupViewModel? PreviewGroup { get; private set; }

    internal void Update(TabGroupViewModel? group)
    {
        if (PreviewGroup != group || Background == null)
            SetCurrentValue(BackgroundProperty, group?.ColorBrush ?? Brushes.Transparent);
        PreviewGroup = group;
    }
}
