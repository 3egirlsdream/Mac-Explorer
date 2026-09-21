using MacExplorer.ViewModels;

namespace MacExplorer.Services;

/// <summary>Stable file identity and viewport position, independent of list or grid geometry.</summary>
public sealed record FileListScrollAnchor(
    string ItemPath, double ItemViewportY, double AbsoluteOffsetFallback);

internal sealed record FileListTabViewState(
    string Path, ViewMode ViewMode, GroupField GroupField,
    FileListScrollAnchor? Anchor, double OffsetY, string? FocusedPath);
