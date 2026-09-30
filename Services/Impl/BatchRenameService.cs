using System.Collections.Concurrent;
using MacExplorer.Indexing;
using MacExplorer.Models;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public class BatchRenameService : IBatchRenameService
{
    private readonly IFileService _fileService;
    private readonly IFileIndexWriter? _fileIndexWriter;
    private readonly IAiTagService? _aiTagService;
    private readonly IPinnedFolderService? _pinnedFolderService;
    private readonly IDirectoryChangeNotifier? _directoryChangeNotifier;
    private readonly IFileTagService? _fileTagService;
    private readonly ILogger<BatchRenameService>? _logger;
    private readonly IMetadataService? _metadata;
    private readonly ConcurrentDictionary<(string, DateTime), DateTime?> _photoDates = new();
    private readonly SemaphoreSlim _execution = new(1, 1);

    public BatchRenameService(IFileService fileService, IFileIndexWriter? fileIndexWriter = null,
        IAiTagService? aiTagService = null, IPinnedFolderService? pinnedFolderService = null,
        IDirectoryChangeNotifier? directoryChangeNotifier = null, IFileTagService? fileTagService = null,
        ILogger<BatchRenameService>? logger = null, IMetadataService? metadata = null)
    {
        _fileService = fileService; _fileIndexWriter = fileIndexWriter; _aiTagService = aiTagService;
        _pinnedFolderService = pinnedFolderService; _directoryChangeNotifier = directoryChangeNotifier;
        _fileTagService = fileTagService; _logger = logger; _metadata = metadata;
    }

    public List<BatchRenamePreviewItem> GeneratePreview(IReadOnlyList<FileSystemEntry> entries,
        IReadOnlyList<BatchRenameRule> rules)
    {
        var request = new BatchRenameRequest { Entries = entries, Rules = rules }.Snapshot();
        var items = BatchRenameRuleEngine.Generate(request, new Dictionary<string, DateTime?>(), default);
        foreach (var group in items.GroupBy(item => item.DirectoryPath))
        {
            var names = Directory.Exists(group.Key)
                ? Directory.EnumerateFileSystemEntries(group.Key).Select(Path.GetFileName).OfType<string>().ToArray() : [];
            ValidateDirectory(group.ToArray(), names, request.Options, default);
        }
        return items;
    }

    public async Task<BatchRenamePlan> GeneratePreviewAsync(BatchRenameRequest request, CancellationToken cancellationToken = default)
    {
        request = request.Snapshot();
        var current = new ConcurrentDictionary<string, FileSystemEntry>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(request.Entries.Where(e => !request.ExcludedPaths.Contains(e.FullPath)),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (entry, token) =>
            {
                var found = await _fileService.GetEntryAsync(entry.FullPath);
                if (found != null) current[entry.FullPath] = found;
            });
        request = new BatchRenameRequest
        {
            Entries = request.Entries.Select(e => current.GetValueOrDefault(e.FullPath) ?? e).ToArray(),
            Rules = request.Rules, Options = request.Options, ExcludedPaths = request.ExcludedPaths,
            ManualNames = request.ManualNames, CapturedAt = request.CapturedAt
        };
        var photos = new ConcurrentDictionary<string, DateTime?>(StringComparer.Ordinal);
        if (_metadata != null && BatchRenameRuleEngine.NeedsPhotoDates(request))
        {
            if (_photoDates.Count > 10000) _photoDates.Clear();
            await Parallel.ForEachAsync(request.Entries.Where(e => !e.IsDirectory && !request.ExcludedPaths.Contains(e.FullPath)),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (entry, token) =>
                {
                    var key = (entry.FullPath, entry.LastModified);
                    if (!_photoDates.TryGetValue(key, out var date))
                    {
                        try { date = (await _metadata.GetMetadataAsync(entry.FullPath)).ImageInfo?.PhotoTakenDate; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { _logger?.LogWarning(ex, "Could not read rename metadata for {Path}", entry.FullPath); date = null; }
                        _photoDates[key] = date;
                    }
                    photos[entry.FullPath] = date;
                });
        }
        var items = await Task.Run(() => BatchRenameRuleEngine.Generate(request, photos, cancellationToken), cancellationToken);
        foreach (var item in items.Where(i => i.IsIncluded))
        {
            item.HasSourceSnapshot = current.ContainsKey(item.OriginalPath);
            if (!item.HasSourceSnapshot) { item.HasError = true; item.ErrorReason = "源项目已不存在，请重新选择"; }
            if (request.Options.Sort == RenameSort.PhotoTaken && !request.Options.PhotoSortFallbackToModified && !photos.GetValueOrDefault(item.OriginalPath).HasValue)
            { item.HasError = true; item.ErrorReason = "缺少用于排序的拍摄时间"; }
        }
        foreach (var group in items.GroupBy(i => i.DirectoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var names = Directory.Exists(group.Key)
                    ? await Task.Run(() => Directory.EnumerateFileSystemEntries(group.Key).Select(Path.GetFileName).OfType<string>().ToArray(), cancellationToken)
                    : (await _fileService.GetDirectoryContentsAsync(group.Key, cancellationToken)).Select(e => e.Name).ToArray();
                ValidateDirectory(group.ToArray(), names, request.Options, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                foreach (var item in group.Where(i => i.IsIncluded)) { item.HasError = true; item.ErrorReason = ex.Message; }
            }
        }
        return new BatchRenamePlan { Request = request, Items = items.AsReadOnly() };
    }

    private static void ValidateDirectory(BatchRenamePreviewItem[] items, IEnumerable<string> existing,
        BatchRenameOptions options, CancellationToken token)
    {
        if (items.Length == 0) return;
        var comparer = RenameNameComparer.ForDirectory(items[0].DirectoryPath);
        var occupied = new HashSet<string>(existing, comparer);
        foreach (var item in items)
            if (item.IsIncluded && item.IsChanged && !item.HasError) occupied.Remove(item.OriginalName);
            else occupied.Add(item.OriginalName);
        var targets = new Dictionary<string, List<BatchRenamePreviewItem>>(comparer);
        foreach (var item in items.Where(i => i.IsIncluded && i.IsChanged && !i.HasError))
        {
            token.ThrowIfCancellationRequested();
            if (options.ResolveConflicts)
            {
                var (stem, ext) = BatchRenameRuleEngine.Split(item.NewName, item.SourceIsDirectory);
                var suffix = 2;
                var name = item.NewName;
                while (occupied.Contains(name) || targets.ContainsKey(name)) name = $"{stem} ({suffix++}){ext}";
                item.NewName = name; item.NewPath = Path.Combine(item.DirectoryPath, name);
                var invalid = BatchRenameRuleEngine.ValidateName(name);
                if (invalid != null) { item.HasError = true; item.ErrorReason = invalid; continue; }
            }
            if (!targets.TryGetValue(item.NewName, out var sharing)) targets[item.NewName] = sharing = [];
            sharing.Add(item);
            if (occupied.Contains(item.NewName)) { item.HasConflict = true; item.ErrorReason = "目标名称已存在"; }
        }
        foreach (var sharing in targets.Values.Where(list => list.Count > 1))
            foreach (var item in sharing) { item.HasConflict = true; item.ErrorReason = "批次内目标名称重复"; }
        foreach (var repeated in items.Where(i => i.IsIncluded).GroupBy(i => i.OriginalName, comparer).Where(g => g.Count() > 1))
            foreach (var item in repeated) { item.HasError = true; item.ErrorReason = "源项目重复或名称等价"; }
    }

    public Task<BatchRenameResult> ExecuteAsync(BatchRenamePlan plan, IProgress<BatchRenameProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!plan.CanExecute) throw new InvalidOperationException("预览有错误、冲突或没有变化，请调整规则。");
        return ExecuteAsync(plan.Items.ToList(), progress, cancellationToken);
    }

    public async Task<BatchRenameResult> ExecuteAsync(List<BatchRenamePreviewItem> previewItems,
        IProgress<BatchRenameProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _execution.WaitAsync(cancellationToken);
        try { return await ExecuteCoreAsync(previewItems, progress, cancellationToken); }
        finally { _execution.Release(); }
    }

    private async Task<BatchRenameResult> ExecuteCoreAsync(List<BatchRenamePreviewItem> items,
        IProgress<BatchRenameProgress>? progress, CancellationToken token)
    {
        var result = new BatchRenameResult { TotalCount = items.Count };
        var candidates = items.Where(i => i.IsIncluded && i.IsChanged && !i.HasError && !i.HasConflict).ToArray();
        result.SkippedCount = items.Count - candidates.Length;
        // Preflight the complete accepted plan before changing any path.
        foreach (var item in candidates)
        {
            token.ThrowIfCancellationRequested();
            var invalid = BatchRenameRuleEngine.ValidateName(item.NewName);
            if (invalid != null || item.NewPath != Path.Combine(item.DirectoryPath, item.NewName))
                throw new InvalidOperationException(invalid ?? "目标路径不属于原目录");
            if (item.HasSourceSnapshot)
            {
                var entry = await _fileService.GetEntryAsync(item.OriginalPath);
                if (entry == null || entry.Size != item.SourceSize || entry.LastModified != item.SourceModified
                    || entry.Created != item.SourceCreated || entry.IsDirectory != item.SourceIsDirectory
                    || entry.IsSymbolicLink != item.SourceIsSymbolicLink)
                    throw new InvalidOperationException($"源项目已变化，请重新预览：{item.OriginalName}");
            }
        }
        foreach (var directory in candidates.GroupBy(i => i.DirectoryPath))
        {
            var comparer = RenameNameComparer.ForDirectory(directory.Key);
            var sources = directory.Select(i => i.OriginalName).ToHashSet(comparer);
            var names = new HashSet<string>(comparer);
            foreach (var item in directory)
            {
                if (!names.Add(item.NewName)) throw new InvalidOperationException("批次内目标名称重复，请重新预览。");
                if (!sources.Contains(item.NewName) && await _fileService.ExistsAsync(item.NewPath))
                    throw new InvalidOperationException($"目标名称已存在，请重新预览：{item.NewName}");
            }
        }
        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in candidates.GroupBy(i => i.DirectoryPath))
        {
            var comparer = RenameNameComparer.ForDirectory(directory.Key);
            var bySource = directory.ToDictionary(i => i.OriginalName, comparer);
            var byTarget = directory.ToDictionary(i => i.NewName, comparer);
            var remaining = directory.ToHashSet();
            // Dependency components limit temporary renames and cancellation latency.
            while (remaining.Count > 0 && !token.IsCancellationRequested)
            {
                var component = new HashSet<BatchRenamePreviewItem>();
                var queue = new Queue<BatchRenamePreviewItem>();
                queue.Enqueue(remaining.First());
                while (queue.TryDequeue(out var item))
                {
                    if (!component.Add(item)) continue;
                    if (bySource.TryGetValue(item.NewName, out var next)) queue.Enqueue(next);
                    if (byTarget.TryGetValue(item.OriginalName, out var previous)) queue.Enqueue(previous);
                }
                remaining.ExceptWith(component);
                var group = component.ToArray();
                var locations = group.ToDictionary(i => i, i => i.OriginalPath);
                try
                {
                    if (group.Length > 1 || comparer.Equals(group[0].OriginalName, group[0].NewName))
                    {
                        foreach (var item in group)
                        {
                            var temporary = $".macexplorer-rename-{Guid.NewGuid():N}";
                            await _fileService.RenameAsync(item.OriginalPath, temporary);
                            locations[item] = Path.Combine(item.DirectoryPath, temporary);
                        }
                    }
                    foreach (var item in group)
                    {
                        await _fileService.RenameAsync(locations[item], item.NewName);
                        locations[item] = item.NewPath;
                    }
                    await SyncComponentAsync(group, locations, result.Warnings);
                    foreach (var item in group)
                    { result.SuccessCount++; result.SuccessfulItems.Add(item); affected.Add(item.DirectoryPath); }
                }
                catch (Exception ex)
                {
                    // Stage changed locations again before restoring a cycle's original names.
                    var restoring = group.Where(i => locations[i] != i.OriginalPath).ToArray();
                    foreach (var item in restoring)
                    {
                        try
                        {
                            var temporary = $".macexplorer-restore-{Guid.NewGuid():N}";
                            await _fileService.RenameAsync(locations[item], temporary);
                            locations[item] = Path.Combine(item.DirectoryPath, temporary);
                        }
                        catch (Exception recovery) { result.Warnings.Add($"恢复暂存失败：{locations[item]}：{recovery.Message}"); }
                    }
                    foreach (var item in restoring)
                    {
                        try { await _fileService.RenameAsync(locations[item], item.OriginalName); locations[item] = item.OriginalPath; }
                        catch (Exception recovery)
                        {
                            result.Warnings.Add($"项目保留在 {locations[item]}：{recovery.Message}");
                            var retained = new BatchRenamePreviewItem { OriginalPath = item.OriginalPath, OriginalName = item.OriginalName,
                                NewPath = locations[item], NewName = Path.GetFileName(locations[item]), SourceIsDirectory = item.SourceIsDirectory };
                            result.SuccessfulItems.Add(retained);
                            affected.Add(item.DirectoryPath);
                        }
                    }
                    foreach (var item in group)
                    { item.ExecutionError = ex.Message; result.FailedCount++; result.FailedItems.Add(item); result.Errors.Add($"{item.OriginalName}：{ex.Message}"); }
                    _logger?.LogError(ex, "Batch rename component failed");
                }
                progress?.Report(new BatchRenameProgress { CompletedCount = result.SuccessCount + result.FailedCount + result.SkippedCount,
                    TotalCount = items.Count, CurrentPath = group[^1].OriginalPath });
            }
            if (token.IsCancellationRequested) break;
        }
        result.WasCancelled = token.IsCancellationRequested;
        result.SkippedCount += candidates.Length - result.SuccessCount - result.FailedCount;
        if (affected.Count > 0)
        {
            try { _directoryChangeNotifier?.NotifyChanged(affected.ToArray(), null); }
            catch (Exception ex) { result.Warnings.Add($"文件已改名，目录刷新失败：{ex.Message}"); }
        }
        return result;
    }

    private async Task SyncComponentAsync(BatchRenamePreviewItem[] items,
        Dictionary<BatchRenamePreviewItem, string> locations, List<string> warnings)
    {
        if (items.Length == 1)
        {
            await SyncPathAsync(items[0].OriginalPath, locations[items[0]], items[0].SourceIsDirectory, warnings);
            return;
        }
        // Use distinct logical paths too: A↔B must not merge tag/index records.
        var intermediates = items.ToDictionary(i => i, i => Path.Combine(i.DirectoryPath, $".macexplorer-index-{Guid.NewGuid():N}"));
        foreach (var item in items) await SyncPathAsync(item.OriginalPath, intermediates[item], item.SourceIsDirectory, warnings);
        foreach (var item in items) await SyncPathAsync(intermediates[item], locations[item], item.SourceIsDirectory, warnings);
    }

    private async Task SyncPathAsync(string oldPath, string newPath, bool isDirectory, List<string> warnings)
    {
        async Task Sync(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) { warnings.Add($"{Path.GetFileName(newPath)}：文件已改名，信息同步失败：{ex.Message}"); }
        }
        if (_fileIndexWriter != null) await Sync(() => _fileIndexWriter.RenameEntryAsync(oldPath, newPath, Path.GetFileName(newPath)));
        if (_aiTagService != null) await Sync(() => _aiTagService.UpdateFilePathAsync(oldPath, newPath));
        if (_fileTagService != null) await Sync(() => _fileTagService.UpdatePathAsync(oldPath, newPath));
        if (isDirectory && _pinnedFolderService != null) await Sync(() => _pinnedFolderService.UpdateFolderPathAsync(oldPath, newPath, Path.GetFileName(newPath)));
    }
}
