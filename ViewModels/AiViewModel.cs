using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.Logging;

namespace MacExplorer.ViewModels;

public partial class AiViewModel : ObservableObject
{
    private readonly IAiTagService? _aiTagService;
    private readonly IThumbnailService? _thumbnailService;
    private readonly IFileIndex? _fileIndex;
    private readonly IImageAnalysisService? _imageAnalysisService;
    private readonly IBackgroundTaskManager? _taskManager;
    private readonly ISettingsService? _settingsService;
    private readonly Microsoft.Extensions.Logging.ILogger<AiViewModel>? _logger;

    private readonly PdfAnalysisService? _pdfAnalysisService;
    private readonly object _analysisLock = new();
    private CancellationTokenSource? _analysisCancellation;

    private const string SettingKeyAiEnabled = "ai_analysis_enabled";

    private string? _currentTextSearchQuery;

    private static readonly HashSet<string> ImageExtensionsForAi = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif",
        ".webp", ".heic", ".heif", ".dng", ".cr2", ".cr3", ".nef", ".arw"
    };

    [ObservableProperty]
    private bool _isAiView;

    [ObservableProperty]
    private AiViewMode _aiViewMode;

    [ObservableProperty]
    private int? _currentFaceClusterId;

    [ObservableProperty]
    private string? _currentAiContextLabel;

    [ObservableProperty]
    private bool _isAiAnalysisEnabled = true;

    public ObservableCollection<FaceCluster> FaceClusters { get; } = [];
    public ObservableCollection<AiCategory> AiCategories { get; } = [];
    public ObservableCollection<AiCategory> TextTokens { get; } = [];

    public AiViewModel(
        IAiTagService? aiTagService = null,
        IThumbnailService? thumbnailService = null,
        IFileIndex? fileIndex = null,
        IImageAnalysisService? imageAnalysisService = null,
        IBackgroundTaskManager? taskManager = null,
        ISettingsService? settingsService = null,
        Microsoft.Extensions.Logging.ILogger<AiViewModel>? logger = null,
        PdfAnalysisService? pdfAnalysisService = null)
    {
        _aiTagService = aiTagService;
        _thumbnailService = thumbnailService;
        _fileIndex = fileIndex;
        _imageAnalysisService = imageAnalysisService;
        _taskManager = taskManager;
        _settingsService = settingsService;
        _logger = logger;
        _pdfAnalysisService = pdfAnalysisService;

        // Load persisted AI analysis enabled state (default: true)
        _isAiAnalysisEnabled = _settingsService?.Get(SettingKeyAiEnabled, true) ?? true;
    }

    partial void OnIsAiAnalysisEnabledChanged(bool value)
    {
        _settingsService?.Set(SettingKeyAiEnabled, value);
        if (!value)
        {
            lock (_analysisLock) _analysisCancellation?.Cancel();
            _pdfAnalysisService?.CancelPending();
        }
    }

    [RelayCommand]
    public async Task NavigateToAiViewAsync(AiViewMode mode)
        => await NavigateToAsync(AiPathHelper.GetTopLevelPath(mode));

    [RelayCommand]
    public async Task NavigateToFaceClusterAsync(int clusterId)
        => await NavigateToAsync($"{VirtualPath.AiFacePrefix}{clusterId}");

    public async Task NavigateToAiCategoryAsync(string tagType, string tagValue)
        => await NavigateToAsync($"{VirtualPath.AiPrefix}{tagType}:{tagValue}");

    public async Task NavigateToAsync(string sentinelPath)
    {
        if (_aiTagService == null) return;
        var info = AiPathHelper.Parse(sentinelPath);

        if (info.Mode == AiViewMode.TextSearch && info.IsTopLevel)
        {
            // TextSearch: clear saved query so we return to hot-words view
            _currentTextSearchQuery = null;
        }
        IsAiView = true;
        AiViewMode = info.Mode;

        try
        {
            if (info.IsTopLevel)
            {
                CurrentFaceClusterId = null;
                CurrentAiContextLabel = null;
                await LoadAiTopLevelAsync(info.Mode);
            }
            else if (info.IsFaceDetail)
            {
                // Ensure FaceClusters is populated (e.g. when navigating directly from search)
                if (FaceClusters.Count == 0 && _aiTagService != null)
                {
                    var clusters = await _aiTagService.GetAllFaceClustersAsync();
                    FaceClusters.Clear();
                    foreach (var c in clusters) FaceClusters.Add(c);
                }
                await LoadFaceClusterEntriesAsync(info.FaceClusterId!.Value);
            }
            else
            {
                CurrentFaceClusterId = null;
                CurrentAiContextLabel = info.TagValue;
                await LoadAiCategoryEntriesAsync(info.TagType!, info.TagValue!);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to navigate AI view");
            throw;
        }
    }

    public async Task HandleAiNavigationAsync(
        string sentinelPath,
        Action<string> setCurrentPath,
        Action updateBreadcrumbs,
        Action<ObservableCollection<FileSystemEntry>> applyEntries,
        Action<string> setStatus,
        Action<bool> setLoading,
        Action? onEntriesUpdated = null)
    {
        if (_aiTagService == null) return;
        var info = AiPathHelper.Parse(sentinelPath);

        if (info.Mode == AiViewMode.TextSearch && info.IsTopLevel)
        {
            _currentTextSearchQuery = null;
        }

        setCurrentPath(sentinelPath);
        IsAiView = true;
        AiViewMode = info.Mode;
        setLoading(true);

        try
        {
            if (info.IsTopLevel)
            {
                CurrentFaceClusterId = null;
                CurrentAiContextLabel = null;
                await LoadAiTopLevelAsync(info.Mode, applyEntries, setStatus, onEntriesUpdated);
            }
            else if (info.IsFaceDetail)
            {
                if (FaceClusters.Count == 0 && _aiTagService != null)
                {
                    var clusters = await _aiTagService.GetAllFaceClustersAsync();
                    FaceClusters.Clear();
                    foreach (var c in clusters) FaceClusters.Add(c);
                }
                await LoadFaceClusterEntriesAsync(info.FaceClusterId!.Value, applyEntries, setStatus);
            }
            else
            {
                CurrentFaceClusterId = null;
                CurrentAiContextLabel = info.TagValue;
                await LoadAiCategoryEntriesAsync(info.TagType!, info.TagValue!, applyEntries, setStatus);
            }

            updateBreadcrumbs();
        }
        catch (Exception ex)
        {
            setStatus($"加载 AI 视图失败: {ex.Message}");
        }
        finally
        {
            setLoading(false);
        }
    }

    public async Task LoadAiTopLevelAsync(AiViewMode mode, Action<ObservableCollection<FileSystemEntry>>? applyEntries = null, Action<string>? setStatus = null, Action? onEntriesUpdated = null)
    {
        switch (mode)
        {
            case AiViewMode.People:
                if (_aiTagService == null) return;
                var clusters = await _aiTagService.GetAllFaceClustersAsync();
                FaceClusters.Clear();
                foreach (var c in clusters) FaceClusters.Add(c);
                var peopleEntries = clusters.Select(CreateVirtualEntry).ToList();
                applyEntries?.Invoke(new ObservableCollection<FileSystemEntry>(peopleEntries));
                setStatus?.Invoke($"{clusters.Count} 个人物");
                _ = ResolveFaceThumbnailsAsync(clusters, peopleEntries, onEntriesUpdated);
                break;

            case AiViewMode.Categories:
                if (_aiTagService == null) return;
                AiCategories.Clear();
                var allCats = new List<AiCategory>();
                foreach (var type in new[] { "scene", "object", "animal" })
                {
                    var cats = await _aiTagService.GetCategoriesByTypeAsync(type);
                    foreach (var c in cats)
                    {
                        AiCategories.Add(c);
                        allCats.Add(c);
                    }
                }
                var catEntries = allCats.Select(CreateVirtualEntry).ToList();
                applyEntries?.Invoke(new ObservableCollection<FileSystemEntry>(catEntries));
                setStatus?.Invoke($"{allCats.Count} 个分类");
                break;

            case AiViewMode.Locations:
                if (_aiTagService == null) return;
                AiCategories.Clear();
                var locations = await _aiTagService.GetCategoriesByTypeAsync("location");
                foreach (var l in locations) AiCategories.Add(l);
                var locEntries = locations
                    .OrderByDescending(l => l.FileCount)
                    .Select(CreateVirtualEntry).ToList();
                applyEntries?.Invoke(new ObservableCollection<FileSystemEntry>(locEntries));
                setStatus?.Invoke($"{locations.Count} 个地点");
                break;

            case AiViewMode.Dates:
                if (_aiTagService == null) return;
                AiCategories.Clear();
                var dates = await _aiTagService.GetCategoriesByTypeAsync("date");
                foreach (var d in dates) AiCategories.Add(d);
                var dateEntries = dates
                    .OrderByDescending(d => d.TagValue)
                    .Select(CreateVirtualEntry).ToList();
                applyEntries?.Invoke(new ObservableCollection<FileSystemEntry>(dateEntries));
                setStatus?.Invoke($"{dates.Count} 个日期");
                break;

            case AiViewMode.TextSearch:
                _currentTextSearchQuery = null;
                applyEntries?.Invoke(new ObservableCollection<FileSystemEntry>());
                TextTokens.Clear();
                if (_aiTagService != null)
                {
                    var tokens = await _aiTagService.GetPopularTextTagsAsync();
                    foreach (var t in tokens) TextTokens.Add(t);
                }
                setStatus?.Invoke(TextTokens.Count > 0 ? $"{TextTokens.Count} 个热门文字标签" : "");
                break;
        }
    }

    public async Task LoadFaceClusterEntriesAsync(int clusterId, Action<ObservableCollection<FileSystemEntry>>? applyEntries = null, Action<string>? setStatus = null)
    {
        if (_aiTagService == null || _fileIndex == null) return;
        var filePaths = await _aiTagService.GetFilePathsForClusterAsync(clusterId);
        var entries = new List<FileSystemEntry>();
        foreach (var p in filePaths)
        {
            var entry = await _fileIndex.GetEntryAsync(p);
            if (entry != null) entries.Add(entry);
        }

        CurrentFaceClusterId = clusterId;
        var cluster = FaceClusters.FirstOrDefault(c => c.Id == clusterId);
        CurrentAiContextLabel = cluster?.DisplayName ?? "未命名";
        applyEntries?.Invoke(new ObservableCollection<FileSystemEntry>(entries));
        setStatus?.Invoke($"{entries.Count} 张照片");
    }

    public async Task LoadAiCategoryEntriesAsync(string tagType, string tagValue, Action<ObservableCollection<FileSystemEntry>>? applyEntries = null, Action<string>? setStatus = null)
    {
        if (_aiTagService == null || _fileIndex == null) return;
        var filePaths = await _aiTagService.GetFilePathsForCategoryAsync(tagType, tagValue);
        var entries = new List<FileSystemEntry>();
        foreach (var p in filePaths)
        {
            var entry = await _fileIndex.GetEntryAsync(p);
            if (entry != null) entries.Add(entry);
        }

        CurrentAiContextLabel = tagValue;
        applyEntries?.Invoke(new ObservableCollection<FileSystemEntry>(entries));
        setStatus?.Invoke($"找到 {entries.Count} 项");
    }

    public async Task RenameFaceClusterAsync(int clusterId, string name)
    {
        if (_aiTagService == null) return;
        try
        {
            await _aiTagService.SetClusterNameAsync(clusterId, name);

            // Update in-memory FaceClusters
            var cluster = FaceClusters.FirstOrDefault(c => c.Id == clusterId);
            if (cluster != null)
                cluster.DisplayName = name;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to rename face cluster {ClusterId}", clusterId);
            throw;
        }
    }

    public async Task SearchAiTagsAsync(string query, Action<ObservableCollection<FileSystemEntry>> applyEntries, Action<string> setStatus)
    {
        if (_aiTagService == null || string.IsNullOrWhiteSpace(query)) return;
        try
        {
            var paths = await _aiTagService.SearchByTagAsync(query);
            var entries = new List<FileSystemEntry>();
            foreach (var path in paths)
            {
                var entry = await _fileIndex!.GetEntryAsync(path);
                if (entry != null) entries.Add(entry);
            }

            _currentTextSearchQuery = query;
            CurrentAiContextLabel = $"搜索: {query}";
            applyEntries(new ObservableCollection<FileSystemEntry>(entries));
            setStatus($"AI 搜索 \"{query}\" — 找到 {entries.Count} 项");
        }
        catch (Exception ex)
        {
            setStatus($"AI 搜索失败: {ex.Message}");
        }
    }

    public void ClearTextSearchQuery() => _currentTextSearchQuery = null;

    public async Task TriggerImageAnalysisAsync(
        IReadOnlyList<FileSystemEntry> entries,
        string currentPath,
        CancellationToken cancellationToken = default)
    {
        if (_aiTagService == null || _taskManager == null || !AnalysisEnabled) return;
        if (!Path.IsPathRooted(currentPath)) return;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_analysisLock)
        {
            _analysisCancellation?.Cancel();
            _analysisCancellation = run;
        }
        var token = run.Token;
        try
        {
            var candidates = entries.Where(e => !e.IsDirectory && !e.IsVirtual
                && Path.IsPathRooted(e.FullPath)
                && (ImageExtensionsForAi.Contains(e.Extension) || IsPdf(e.FullPath))).ToList();
            var toAnalyze = await _aiTagService.GetUnanalyzedFilesAsync(
                candidates.Select(e => e.FullPath).ToList(),
                candidates.Select(e => e.LastModified.Ticks).ToList());
            token.ThrowIfCancellationRequested();

            // Use all directory entries: filtered listings must not erase existing PDF tags.
            var currentPaths = new HashSet<string>(entries.Select(e => e.FullPath), StringComparer.Ordinal);
            var analyzedPaths = await _aiTagService.GetAnalyzedPathsInDirectoryAsync(currentPath);
            var deletedPaths = analyzedPaths.Where(p => !currentPaths.Contains(p) && !File.Exists(p)).ToList();
            token.ThrowIfCancellationRequested();
            if (deletedPaths.Count > 0)
                await _aiTagService.DeleteAnalysisForFilesAsync(deletedPaths);

            await Task.WhenAll(
                RunAnalysisBatchAsync(toAnalyze.Where(f => !IsPdf(f.Path)).ToList(), false, token),
                RunAnalysisBatchAsync(toAnalyze.Where(f => IsPdf(f.Path)).ToList(), true, token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger?.LogError(ex, "AI analysis trigger failed"); }
        finally
        {
            lock (_analysisLock)
                if (ReferenceEquals(_analysisCancellation, run)) _analysisCancellation = null;
        }
    }

    private bool AnalysisEnabled => IsAiAnalysisEnabled && (_settingsService?.Get(SettingKeyAiEnabled, true) ?? true);
    private static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private async Task RunAnalysisBatchAsync(List<(string Path, long ModifiedTicks)> files, bool pdf, CancellationToken ct)
    {
        if (files.Count == 0 || (pdf ? _pdfAnalysisService == null : _imageAnalysisService == null)) return;
        var title = pdf ? "PDF 文字分析" : "AI 图像分析";
        var task = _taskManager!.AddTask($"{title} 0/{files.Count}");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, task.Cts.Token);
        var token = linked.Token;
        var failures = new ConcurrentQueue<string>();
        var completed = 0;
        try
        {
            var queue = new ConcurrentQueue<(string Path, long ModifiedTicks)>(files);
            var workers = Enumerable.Range(0, pdf ? 1 : Math.Min(3, files.Count)).Select(_ => Task.Run(async () =>
            {
                while (queue.TryDequeue(out var file))
                {
                    token.ThrowIfCancellationRequested();
                    if (!AnalysisEnabled) throw new OperationCanceledException();
                    try
                    {
                        if (pdf)
                        {
                            var progress = new PdfTaskProgress(update =>
                            {
                                var label = $"PDF 文字分析 · {Path.GetFileName(file.Path)} · 第 {update.CompletedPages}/{update.TotalPages} 页";
                                _taskManager.UpdateProgress(task.Id,
                                    100.0 * (completed + (double)update.CompletedPages / update.TotalPages) / files.Count,
                                    file.Path, label);
                            });
                            await _pdfAnalysisService!.AnalyzeAsync(file.Path, file.ModifiedTicks, progress, token);
                        }
                        else
                        {
                            var result = await _imageAnalysisService!.AnalyzeImageAsync(file.Path, token);
                            token.ThrowIfCancellationRequested();
                            await _aiTagService!.SaveAnalysisResultAsync(file.Path, file.ModifiedTicks, result, token);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        failures.Enqueue($"{Path.GetFileName(file.Path)}：{ex.Message}");
                        _logger?.LogWarning(ex, "Analysis failed for {Path}", file.Path);
                    }
                    var count = Interlocked.Increment(ref completed);
                    _taskManager.UpdateProgress(task.Id, 100.0 * count / files.Count, file.Path, $"{title} {count}/{files.Count}");
                }
            }, token));
            await Task.WhenAll(workers);
            token.ThrowIfCancellationRequested();
            if (!pdf) await _aiTagService!.RunClusteringAsync();
            if (failures.IsEmpty) _taskManager.CompleteTask(task.Id);
            else _taskManager.FailTask(task.Id, $"{title}：{failures.Count} 个文件失败", string.Join(Environment.NewLine, failures));
        }
        catch (OperationCanceledException) { _taskManager.CancelTask(task.Id); }
        catch (Exception ex) { _taskManager.FailTask(task.Id, ex.Message); }
    }

    // Reports inline, so delayed SynchronizationContext callbacks cannot overwrite a finished task.
    private sealed class PdfTaskProgress(Action<PdfAnalysisProgress> report) : IProgress<PdfAnalysisProgress>
    {
        public void Report(PdfAnalysisProgress value) => report(value);
    }

    private static FileSystemEntry CreateVirtualEntry(FaceCluster cluster) => new()
    {
        FullPath = $"{VirtualPath.AiFacePrefix}{cluster.Id}",
        Name = cluster.DisplayName ?? "未命名",
        IsDirectory = true,
        IconKey = "ai-face",
        ThumbnailUrl = cluster.FaceThumbnailUrl,
        Size = cluster.FaceCount,
        LastModified = cluster.UpdatedAt,
        Created = cluster.CreatedAt,
        IsVirtual = true,
        VirtualFolderType = "face",
        VirtualFolderKey = cluster.Id.ToString(),
        VirtualItemCount = cluster.FaceCount,
    };

    private static FileSystemEntry CreateVirtualEntry(AiCategory category) => new()
    {
        FullPath = $"{VirtualPath.AiPrefix}{category.TagType}:{category.TagValue}",
        Name = category.TagValue,
        IsDirectory = true,
        IconKey = $"ai-{category.TagType}",
        Size = category.FileCount,
        IsVirtual = true,
        VirtualFolderType = category.TagType,
        VirtualFolderKey = $"{category.TagType}:{category.TagValue}",
        VirtualItemCount = category.FileCount,
    };

    private static string GetAiTypeLabel(string virtualFolderType) => virtualFolderType switch
    {
        "face" => "人物",
        "scene" => "场景",
        "object" => "物品",
        "animal" => "动物",
        "location" => "地点",
        "date" => "日期",
        _ => virtualFolderType
    };

    public FileSystemEntry CreateVirtualEntryForRename(FaceCluster cluster, string newName) => new()
    {
        FullPath = $"{VirtualPath.AiFacePrefix}{cluster.Id}",
        Name = newName,
        IsDirectory = true,
        IconKey = "ai-face",
        ThumbnailUrl = cluster.FaceThumbnailUrl,
        Size = cluster.FaceCount,
        LastModified = cluster.UpdatedAt,
        Created = cluster.CreatedAt,
        IsVirtual = true,
        VirtualFolderType = "face",
        VirtualFolderKey = cluster.Id.ToString(),
        VirtualItemCount = cluster.FaceCount,
    };

    private async Task ResolveFaceThumbnailsAsync(IReadOnlyList<FaceCluster> clusters, IReadOnlyList<FileSystemEntry>? entries = null, Action? onUpdated = null)
    {
        if (_thumbnailService == null) return;
        foreach (var cluster in clusters)
        {
            if (string.IsNullOrEmpty(cluster.RepresentativeFacePath) || cluster.BoundingBoxW <= 0)
                continue;
            try
            {
                var bytes = await _thumbnailService.GetFaceCropAsync(
                    cluster.RepresentativeFacePath,
                    cluster.BoundingBoxX, cluster.BoundingBoxY,
                    cluster.BoundingBoxW, cluster.BoundingBoxH);
                if (bytes != null)
                {
                    var url = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
                    cluster.FaceThumbnailUrl = url;
                    // Sync thumbnail back to the corresponding FileSystemEntry
                    var entry = entries?.FirstOrDefault(e => e.VirtualFolderKey == cluster.Id.ToString());
                    if (entry != null)
                    {
                        entry.ThumbnailUrl = url;
                        // Force binding re-evaluation so the converter returns the thumbnail
                        entry.RaiseIconBindingChanged();
                    }
                    onUpdated?.Invoke();
                }
            }
            catch (Exception ex) { _logger?.LogError(ex, "Failed to resolve face thumbnails"); }
        }
    }

    public void Reset()
    {
        IsAiView = false;
        AiViewMode = AiViewMode.TextSearch;
        CurrentFaceClusterId = null;
        CurrentAiContextLabel = null;
        FaceClusters.Clear();
        AiCategories.Clear();
        TextTokens.Clear();
    }
}
