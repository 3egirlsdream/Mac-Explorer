using MacExplorer.ViewModels;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MacExplorer.Models;

/// <summary>Immutable so an in-flight directory snapshot keeps its original filters.</summary>
public sealed record FileListFilterState
{
    public static FileListFilterState Empty { get; } = new();

    [JsonIgnore]
    public string FileName { get => StoredFileName; init => StoredFileName = value; }
    [JsonIgnore]
    public ImmutableDictionary<SortField, ImmutableHashSet<string>> Columns { get => StoredColumns; init => StoredColumns = value; }

    // Source-generated setters preserve omitted keys without making snapshots mutable to callers.
    [JsonInclude, JsonPropertyName(nameof(FileName))]
    internal string StoredFileName { get; set; } = string.Empty;
    [JsonInclude, JsonPropertyName(nameof(Columns))]
    internal ImmutableDictionary<SortField, ImmutableHashSet<string>> StoredColumns { get; set; }
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
