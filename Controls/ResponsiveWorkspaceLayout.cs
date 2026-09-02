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

    internal static ResponsiveWorkspaceLayout Resolve(double workspaceWidth, bool forceCompact)
        => forceCompact || workspaceWidth < CompactBreakpoint
            ? new ResponsiveWorkspaceLayout(
                true,
                SplitViewDisplayMode.CompactOverlay,
                220,
                48,
                SplitViewDisplayMode.Overlay)
            : new ResponsiveWorkspaceLayout(
                false,
                SplitViewDisplayMode.Inline,
                260,
                0,
                SplitViewDisplayMode.Inline);
}
