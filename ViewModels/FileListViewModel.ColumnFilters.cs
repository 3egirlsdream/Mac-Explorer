using MacExplorer.Models;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    public FileListFilterState ColumnFilters => _sortFilter.ColumnFilters;
    public bool HasFileListFilters => ColumnFilters.IsActive;

    public string FileNameFilter
    {
        get => ColumnFilters.FileName;
        set => _sortFilter.ColumnFilters = ColumnFilters with { FileName = value ?? string.Empty };
    }

    public IReadOnlyList<FileColumnFilterOption> GetColumnFilterOptions(SortField field)
        => _sortFilter.GetColumnFilterOptions(field);

    public void SetColumnFilter(SortField field, string key, bool selected)
        => _sortFilter.ColumnFilters = ColumnFilters.Set(field, key, selected);

    public void ClearColumnFilter(SortField field)
        => _sortFilter.ColumnFilters = ColumnFilters with
        {
            Columns = ColumnFilters.Columns.Remove(field),
            FileName = field == SortField.Name ? string.Empty : ColumnFilters.FileName
        };

    public void ClearFileListFilters() => _sortFilter.ColumnFilters = FileListFilterState.Empty;
}
