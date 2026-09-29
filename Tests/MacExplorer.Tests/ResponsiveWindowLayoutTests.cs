using Avalonia.Controls;
using MacExplorer.Controls;
using Xunit;

namespace MacExplorer.Tests;

public class ResponsiveWorkspaceLayoutTests
{
    [Fact]
    public void WidthBelowBreakpointUsesCompactOverlay()
    {
        var layout = ResponsiveWorkspaceLayout.Resolve(1179, forceCompact: false);

        Assert.True(layout.IsCompact);
        Assert.Equal(SplitViewDisplayMode.CompactOverlay, layout.SidebarDisplayMode);
        Assert.Equal(220, layout.SidebarOpenPaneLength);
        Assert.Equal(48, layout.SidebarCompactPaneLength);
        Assert.Equal(SplitViewDisplayMode.Overlay, layout.InfoPanelDisplayMode);
    }

    [Fact]
    public void WidthAtOrAboveBreakpointUsesWideInlineLayout()
    {
        var layout = ResponsiveWorkspaceLayout.Resolve(1180, forceCompact: false);

        Assert.False(layout.IsCompact);
        Assert.Equal(SplitViewDisplayMode.Inline, layout.SidebarDisplayMode);
        Assert.Equal(240, layout.SidebarOpenPaneLength);
        Assert.Equal(0, layout.SidebarCompactPaneLength);
        Assert.Equal(SplitViewDisplayMode.Inline, layout.InfoPanelDisplayMode);
    }

    [Fact]
    public void ForceCompactOverridesAnyWorkspaceWidth()
    {
        var layout = ResponsiveWorkspaceLayout.Resolve(1600, forceCompact: true);

        Assert.True(layout.IsCompact);
        Assert.Equal(SplitViewDisplayMode.CompactOverlay, layout.SidebarDisplayMode);
        Assert.Equal(220, layout.SidebarOpenPaneLength);
        Assert.Equal(48, layout.SidebarCompactPaneLength);
        Assert.Equal(SplitViewDisplayMode.Overlay, layout.InfoPanelDisplayMode);
    }
}
