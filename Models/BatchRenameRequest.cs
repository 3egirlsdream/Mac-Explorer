namespace MacExplorer.Models;

public sealed class BatchRenameOptions
{
    public RenameSort Sort { get; set; }
    public bool Descending { get; set; }
    public bool RestartPerDirectory { get; set; }
    public bool ResolveConflicts { get; set; }
    public bool PhotoSortFallbackToModified { get; set; }
    public BatchRenameOptions Clone() => (BatchRenameOptions)MemberwiseClone();
}

public sealed class BatchRenameRequest
{
    public IReadOnlyList<FileSystemEntry> Entries { get; init; } = [];
    public IReadOnlyList<BatchRenameRule> Rules { get; init; } = [];
    public BatchRenameOptions Options { get; init; } = new();
    public IReadOnlySet<string> ExcludedPaths { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> ManualNames { get; init; } = new Dictionary<string, string>();
    public DateTime CapturedAt { get; init; } = DateTime.Now;
    public BatchRenameRequest Snapshot() => new()
    {
        Entries = Entries.ToArray(), Rules = Rules.Select(rule => rule.Clone()).ToArray(),
        Options = Options.Clone(), ExcludedPaths = new HashSet<string>(ExcludedPaths, StringComparer.Ordinal),
        ManualNames = new Dictionary<string, string>(ManualNames, StringComparer.Ordinal), CapturedAt = CapturedAt
    };
}

public sealed class BatchRenamePlan
{
    public required BatchRenameRequest Request { get; init; }
    public required IReadOnlyList<BatchRenamePreviewItem> Items { get; init; }
    public bool CanExecute => Items.Any(item => item.IsIncluded && item.IsChanged)
        && Items.All(item => !item.IsIncluded || !item.HasError && !item.HasConflict);
}

public sealed class BatchRenamePreset
{
    public string Name { get; set; } = "";
    public List<BatchRenameRule> Rules { get; set; } = [];
    public BatchRenameOptions Options { get; set; } = new();
    public override string ToString() => Name;
}
