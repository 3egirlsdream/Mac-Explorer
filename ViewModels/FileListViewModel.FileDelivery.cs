using MacExplorer.Models;
using Microsoft.Extensions.Logging;
using MacExplorer.Services.Impl;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    private FileDeliveryService? _deliveryStaging;
    internal bool IsDeliveryStaging => _deliveryStaging != null && CurrentPath == FileDeliveryService.StagingPath;
    internal void ConfigureDeliveryStaging(FileDeliveryService delivery)
    {
        if (!IsBrowseOnly) throw new InvalidOperationException("File delivery requires browse-only mode.");
        _deliveryStaging = delivery;
    }

    private async Task LoadDeliveryStagingAsync(bool recordHistory)
    {
        if (_deliveryStaging == null) return;
        if (recordHistory)
        {
            await _navigation.NavigateToAsync(FileDeliveryService.StagingPath);
            ClearSelection();
        }
        _navigation.SetWatchedDirectory(null);
        _ai.Reset();
        _search.Reset();
        _navigation.IsHomePage = false;
        _navigation.IsArchiveView = _navigation.IsAiView = _navigation.IsRemoteView = _navigation.IsSearchMode = false;
        _navigation.CurrentArchivePath = _navigation.CurrentRemoteServerId = _navigation.CurrentAiContextLabel = null;
        _navigation.CurrentFaceClusterId = null;
        _navigation.CurrentArchiveInternalPath = _navigation.SearchQuery = string.Empty;
        _navigation.UpdateBreadcrumbs();
        var work = BeginDirectoryWork();
        var paths = _deliveryStaging.StagedPaths.ToArray();
        IsLoading = true;
        ReadErrorMessage = string.Empty;
        ScrollBehaviorAfterLoad = recordHistory ? ScrollMode.ResetToTop : ScrollMode.PreservePosition;
        try
        {
            var entries = await Task.Run(async () =>
            {
                var result = new List<FileSystemEntry>(paths.Length);
                foreach (var path in paths)
                {
                    work.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var entry = await _fileService.GetEntryAsync(path);
                        if (entry != null) result.Add(entry);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                return result;
            }, work.Token);
            if (!IsCurrentDirectoryWork(work) || !IsDeliveryStaging) return;
            ApplyEntries(entries);
            ResolveRealEntries(entries, work);
            StatusText = paths.Length == entries.Count
                ? LocalizationText.Get("delivery.count", entries.Count)
                : LocalizationText.Get("delivery.unavailable", entries.Count, paths.Length - entries.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsCurrentDirectoryWork(work) || !IsDeliveryStaging) return;
            ReadErrorMessage = LocalizationText.Get("delivery.load_failed", ex.Message);
            StatusText = ReadErrorMessage;
            _logger?.LogError(ex, "Unable to load staging references");
        }
        finally
        {
            if (IsCurrentDirectoryWork(work)) IsDirectoryLoading = IsLoading = false;
        }
    }
}
