using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.ViewModels;

public partial class NavigationViewModel
{
    // The event is owned by this scoped VM, so no application-lifetime publisher retains a closed tab.
    internal void TrackHomeUsageWith(HomeWorkspaceService history)
    {
        string? lastArchive = null;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(CurrentPath)) return;
            if (ArchivePathHelper.IsArchivePath(CurrentPath))
            {
                var (archivePath, _) = ArchivePathHelper.Parse(CurrentPath);
                if (!string.Equals(lastArchive, archivePath, StringComparison.Ordinal))
                    _ = history.RecordUseAsync(archivePath);
                lastArchive = archivePath;
                return;
            }
            lastArchive = null;
            _ = history.RecordUseAsync(CurrentPath, directoriesOnly: true);
        };
    }
}
