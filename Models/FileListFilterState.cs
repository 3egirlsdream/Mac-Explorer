using MacExplorer.ViewModels;
using System.Collections.Immutable;

namespace MacExplorer.Models;

/// <summary>Immutable so an in-flight directory snapshot keeps its original filters.</summary>
public sealed record FileListFilterState
{
    public static FileListFilterState Empty { get; } = new();

    public string FileName { get; init; } = string.Empty;
    public ImmutableDictionary<SortField, ImmutableHashSet<string>> Columns { get; init; }
        = ImmutableDictionary<SortField, ImmutableHashSet<string>>.Empty;

    public bool IsActive => !string.IsNullOrWhiteSpace(FileName) || Columns.Count > 0;

    public ImmutableHashSet<string> Selection(SortField field) => Columns.TryGetValue(field, out var values)
        ? values : ImmutableHashSet<string>.Empty;

    public FileListFilterState Set(SortField field, string key, bool selected)
    {
        var values = selected ? Selection(field).Add(key) : Selection(field).Remove(key);
        return this with { Columns = values.IsEmpty ? Columns.Remove(field) : Columns.SetItem(field, values) };
    }
}

public sealed record FileColumnFilterOption(string Key, string Label, int Count, bool IsSelected);
