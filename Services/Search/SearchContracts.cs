using MacExplorer.Models;

namespace MacExplorer.Services.Search;

public enum SearchIndexPhase { Building, Ready, Partial, Unavailable }

public sealed record SearchIndexStatus(string Root, SearchIndexPhase Phase, long IndexedEntries = 0,
    int FailedDirectories = 0, string? Error = null, bool IsLiveScan = false)
{
    public bool IsBusy => Phase == SearchIndexPhase.Building;
    public string Description => IsLiveScan
        ? IsBusy ? "正在扫描当前位置，结果仍在补全" : "当前位置扫描完成（按搜索排除规则）"
        : Phase switch
    {
        SearchIndexPhase.Building => $"索引建立中（已处理 {IndexedEntries:N0} 项），结果仍在补全",
        SearchIndexPhase.Partial => $"部分范围未更新（{FailedDirectories} 个目录），结果可能不完整",
        SearchIndexPhase.Unavailable => "此位置暂不可访问，显示已有索引",
        _ => "索引已更新（按索引排除规则）"
    };
}

public sealed record SearchSnapshot(IReadOnlyList<FileSystemEntry> Entries, SearchIndexStatus Status, bool HasMore);

public interface ISearchSessionService
{
    IAsyncEnumerable<SearchSnapshot> SearchSnapshotsAsync(string directory, string input, int limit = 500,
        CancellationToken cancellationToken = default);
}

public interface IPinyinInitials
{
    string GetInitials(string name);
}

public sealed record SearchChange(string Path, bool IsDirectory, bool MustRescan, ulong EventId,
    bool RestartWatcher = false);

public interface ISearchChangeSource
{
    IDisposable Watch(string root, ulong sinceEventId, Action<SearchChange> onChange, bool fullScanFollows);
}

/// <summary>No UI state, icons, thumbnail work or content reads in the indexing path.</summary>
public sealed record SearchEntry(string Path, string Name, string ParentPath, string Extension,
    string NameKey, string InitialsKey, string ParentKey, long Size, bool IsDirectory, bool IsHidden,
    long CreatedTicks, long ModifiedTicks, bool IsSymbolicLink = false)
{
    public FileSystemEntry ToFileSystemEntry() => new()
    {
        FullPath = Path, Name = Name, Extension = Extension, Size = Size,
        IsDirectory = IsDirectory, IsHidden = IsHidden, IsSymbolicLink = IsSymbolicLink,
        Created = new DateTime(CreatedTicks, DateTimeKind.Utc).ToLocalTime(),
        LastModified = new DateTime(ModifiedTicks, DateTimeKind.Utc).ToLocalTime(),
        IconKey = IsDirectory ? MacExplorer.Indexing.SqliteFileIndex.ResolveBundleIconKey(Extension)
            : MacExplorer.Indexing.SqliteFileIndex.ResolveIconKey(Extension)
    };
}
