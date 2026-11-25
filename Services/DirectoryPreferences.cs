using System.Text.Json.Serialization;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;

namespace MacExplorer.Services;

internal sealed record DirectoryPreferences(SortField Sort, bool Ascending, SortPriority Priority,
    FileListFilterState Filters, double[] Columns, bool PreviewPane, bool MetadataPanel, bool InfoPanel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] FileListColumns HiddenColumns = FileListColumns.None,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FileListColumns? ResizedColumns = null);

internal sealed record FolderTabViewState(ViewMode ViewMode, GroupField GroupField, DirectoryPreferences Preferences);

internal interface IDirectoryPreferencesService
{
    Task<DirectoryPreferences?> ReadAsync(string path);
    Task SaveAsync(string path, DirectoryPreferences preferences);
    Task RemoveAsync(string path);
}
