using MacExplorer.Models;
using MacExplorer.ViewModels;

namespace MacExplorer.Services;

internal sealed record DirectoryPreferences(SortField Sort, bool Ascending, SortPriority Priority,
    FileListFilterState Filters, double[] Columns, bool PreviewPane, bool MetadataPanel, bool InfoPanel);

internal interface IDirectoryPreferencesService
{
    Task<DirectoryPreferences?> ReadAsync(string path);
    Task SaveAsync(string path, DirectoryPreferences preferences);
    Task RemoveAsync(string path);
}
