using Avalonia.Controls;

namespace MacExplorer.Controls;

internal readonly record struct ResponsiveWorkspaceLayout(
    bool IsCompact,
    SplitViewDisplayMode SidebarDisplayMode,
    double SidebarOpenPaneLength,
    double SidebarCompactPaneLength,
    SplitViewDisplayMode InfoPanelDisplayMode)
{
    internal const double CompactBreakpoint = 1180;

    internal static ResponsiveWorkspaceLayout Resolve(double workspaceWidth, bool forceCompact, int preferredSidebarWidth = 0)
    {
        var compact = forceCompact || workspaceWidth < CompactBreakpoint;
        var maximumSidebarWidth = Math.Max(0, Math.Min(420, Math.Floor(workspaceWidth - 64)));
        var defaultSidebarWidth = compact ? 220 : 240;
        var sidebarWidth = Math.Clamp(preferredSidebarWidth > 0 ? preferredSidebarWidth : defaultSidebarWidth,
            Math.Min(150, maximumSidebarWidth), maximumSidebarWidth);
        return compact
            ? new ResponsiveWorkspaceLayout(
                true,
                SplitViewDisplayMode.CompactOverlay,
                sidebarWidth,
                48,
                SplitViewDisplayMode.Overlay)
            : new ResponsiveWorkspaceLayout(
                false,
                SplitViewDisplayMode.Inline,
                sidebarWidth,
                0,
                SplitViewDisplayMode.Inline);
    }
}
