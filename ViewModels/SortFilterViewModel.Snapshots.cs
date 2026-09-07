using System.Collections.ObjectModel;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.ViewModels;

public partial class SortFilterViewModel
{
    public FileListQuery CaptureQuery() => new(SortField, SortAscending, GroupField,
        HideSystemFiles, HideDotFiles, HideDotFolders);

    // Reuse the exact filtering/comparison/group ordering rules. This detached object
    // has no settings service or UI subscriptions; never call this on the live VM.
    internal static SortFilterViewModel CreateSnapshotProcessor(FileListQuery query) => new()
    {
        _sortField = query.SortField,
        _sortAscending = query.SortAscending,
        _groupField = query.GroupField,
        _hideSystemFiles = query.HideSystemFiles,
        _hideDotFiles = query.HideDotFiles,
        _hideDotFolders = query.HideDotFolders
    };

    internal bool PassesSnapshotFilter(FileSystemEntry entry) => PassesFilter(entry);

    internal IReadOnlyList<FileListGroupSnapshot> BuildSnapshotGroups(IReadOnlyList<FileSystemEntry> entries)
        => GroupField == GroupField.None
            ? Array.Empty<FileListGroupSnapshot>()
            : Array.AsReadOnly(BuildGroups(entries.ToList())
                .Select(group => new FileListGroupSnapshot(group.Name, group.Entries.AsReadOnly()))
                .ToArray());

    /// <summary>UI thread only: apply precomputed groups without sorting or per-file insertion.</summary>
    internal void ApplySnapshotMetadata(FileListSnapshot snapshot)
    {
        _rawEntries = snapshot.RawEntries;
        // FileGroup is the existing UI contract. No entry/group is mutated by the producer.
        if (snapshot.Groups.Count == 0 && Groups.Count == 0) return;
        Groups = new ObservableCollection<FileGroup>(snapshot.Groups.Select(group => new FileGroup
        {
            Name = group.Name,
            Entries = group.Entries.ToList()
        }));
    }
}
