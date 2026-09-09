using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    internal Task<IReadOnlyList<BreadcrumbSegment>> GetBreadcrumbDirectoriesAsync(
        string path, CancellationToken cancellationToken)
    {
        var filter = SortFilterViewModel.CreateSnapshotProcessor(_sortFilter.CaptureQuery());
        return Task.Run<IReadOnlyList<BreadcrumbSegment>>(async () =>
        {
            var entries = new List<FileSystemEntry>();
            if (ArchivePathHelper.IsArchivePath(path))
            {
                if (_archiveService == null)
                    throw new InvalidOperationException("Archive service is unavailable.");
                var (archivePath, internalPath) = ArchivePathHelper.Parse(path);
                entries.AddRange(await _archiveService.GetArchiveContentsAsync(archivePath, internalPath)
                    .WaitAsync(cancellationToken));
            }
            else
            {
                await foreach (var batch in _fileService.EnumerateDirectoryBatchesAsync(
                                   path, cancellationToken: cancellationToken))
                    entries.AddRange(batch.Where(entry => entry.IsDirectory));
            }

            var directories = new List<BreadcrumbSegment>();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.IsDirectory
                    || entry.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                    || !filter.PassesVisibilityFilter(entry))
                    continue;

                var displayName = entry.DisplayName;
                if (_displayNameService != null && Path.IsPathRooted(entry.FullPath))
                {
                    var localized = _displayNameService.GetDisplayName(entry.FullPath);
                    if (!string.IsNullOrWhiteSpace(localized))
                        displayName = localized;
                }
                directories.Add(new BreadcrumbSegment
                {
                    Name = entry.Name,
                    DisplayName = displayName,
                    FullPath = entry.FullPath,
                    HasDropdown = true
                });
            }

            return directories.OrderBy(directory => directory.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }
}
