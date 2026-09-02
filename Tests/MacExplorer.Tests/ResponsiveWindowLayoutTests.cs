using Avalonia.Controls;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

public class ResponsiveWorkspaceLayoutTests
{
    [Theory]
    [InlineData(1000)]
    [InlineData(1179)]
    public void WidthBelowBreakpointUsesCompactOverlay(double width)
    {
        var layout = ResponsiveWorkspaceLayout.Resolve(width, forceCompact: false);

        Assert.True(layout.IsCompact);
        Assert.Equal(SplitViewDisplayMode.CompactOverlay, layout.SidebarDisplayMode);
        Assert.Equal(220, layout.SidebarOpenPaneLength);
        Assert.Equal(48, layout.SidebarCompactPaneLength);
        Assert.Equal(SplitViewDisplayMode.Overlay, layout.InfoPanelDisplayMode);
    }

    [Theory]
    [InlineData(1180)]
    [InlineData(1280)]
    [InlineData(1600)]
    public void WidthAtOrAboveBreakpointUsesWideInlineLayout(double width)
    {
        var layout = ResponsiveWorkspaceLayout.Resolve(width, forceCompact: false);

        Assert.False(layout.IsCompact);
        Assert.Equal(SplitViewDisplayMode.Inline, layout.SidebarDisplayMode);
        Assert.Equal(260, layout.SidebarOpenPaneLength);
        Assert.Equal(0, layout.SidebarCompactPaneLength);
        Assert.Equal(SplitViewDisplayMode.Inline, layout.InfoPanelDisplayMode);
    }

    [Theory]
    [InlineData(1180)]
    [InlineData(1600)]
    [InlineData(4000)]
    public void ForceCompactOverridesAnyWorkspaceWidth(double width)
    {
        var layout = ResponsiveWorkspaceLayout.Resolve(width, forceCompact: true);

        Assert.True(layout.IsCompact);
        Assert.Equal(SplitViewDisplayMode.CompactOverlay, layout.SidebarDisplayMode);
        Assert.Equal(220, layout.SidebarOpenPaneLength);
        Assert.Equal(48, layout.SidebarCompactPaneLength);
        Assert.Equal(SplitViewDisplayMode.Overlay, layout.InfoPanelDisplayMode);
    }
}
