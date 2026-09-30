namespace MacExplorer.Models;

/// <summary>Preview item showing old name → new name and conflict status.</summary>
public class BatchRenamePreviewItem
{
    public FileSystemEntry? Entry { get; set; }
    public bool IsIncluded { get; set; } = true;
    public bool IsManual { get; set; }
    public bool HasSourceSnapshot { get; set; }
    public long SourceSize { get; set; }
    public DateTime SourceModified { get; set; }
    public DateTime SourceCreated { get; set; }
    public bool SourceIsDirectory { get; set; }
    public bool SourceIsSymbolicLink { get; set; }
    public List<string> StepNames { get; set; } = [];
    public string DirectoryPath => Path.GetDirectoryName(OriginalPath) ?? "";
    public string Status => !IsIncluded ? "已排除" : HasError || HasConflict ? ErrorReason
        : IsManual ? "手动修改" : IsChanged ? "将重命名" : "未变化";
    public string OriginalPath { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string NewName { get; set; } = "";
    public string NewPath { get; set; } = "";
    public bool HasConflict { get; set; }
    public bool HasError { get; set; }
    public string ErrorReason { get; set; } = "";
    public string ExecutionError { get; set; } = "";
    public bool IsChanged => !string.Equals(OriginalName, NewName, StringComparison.Ordinal);
}

/// <summary>Result of a batch rename operation.</summary>
public class BatchRenameResult
{
    public Guid? HistoryBatchId { get; set; }
    public bool WasCancelled { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<BatchRenamePreviewItem> FailedItems { get; set; } = [];
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<BatchRenamePreviewItem> SuccessfulItems { get; set; } = [];
}

/// <summary>Progress update emitted while applying a batch rename.</summary>
public class BatchRenameProgress
{
    public int CompletedCount { get; set; }
    public int TotalCount { get; set; }
    public string CurrentPath { get; set; } = "";
    public double Percent => TotalCount <= 0 ? 0 : CompletedCount * 100d / TotalCount;
}
