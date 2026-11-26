using Avalonia.Platform.Storage;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    internal bool TryExtractOnArchiveDoubleClick(FileSystemEntry entry)
    {
        if (IsBrowseOnly || IsArchiveView || IsTrashActive || entry.IsDirectory || entry.IsVirtual
            || !Path.IsPathFullyQualified(entry.FullPath) || _archiveService?.IsArchiveFile(entry.FullPath) != true
            || _settingsService?.Get(ArchivePreferences.ExtractOnDoubleClick, false) != true) return false;
        ExtractToNamedFolder(entry);
        return true;
    }

    private Task ExtractArchiveItemAsync(FileSystemEntry entry)
    {
        var (path, key) = ArchivePathHelper.Parse(entry.FullPath);
        return QueueArchiveExtraction(path, new(Path.GetDirectoryName(path)!, ArchiveExtractionMode.NamedFolder, [key]), entry.Name);
    }

    internal async Task ExtractArchiveSelectionAsync(IReadOnlyList<FileSystemEntry> selected)
    {
        if (IsBrowseOnly || !IsArchiveView || _navigation.CurrentArchivePath is not { } path || selected.Count == 0) return;
        var relativeTo = _navigation.CurrentArchiveInternalPath ?? "";
        var keys = selected.Where(entry => ArchivePathHelper.IsArchivePath(entry.FullPath))
            .Select(entry => ArchivePathHelper.Parse(entry.FullPath))
            .Where(entry => entry.archivePath == path).Select(entry => entry.internalPath).ToArray();
        if (keys.Length > 0 && await PickArchiveDestinationAsync() is { } directory)
            await QueueArchiveExtraction(path, new(directory, ArchiveExtractionMode.CurrentFolder, keys, relativeTo));
    }

    private async Task<string?> PickArchiveDestinationAsync()
    {
        if (IsBrowseOnly || _topLevelWindow?.StorageProvider is not { CanPickFolder: true } storage) return null;
        var folders = await storage.OpenAuthorizedFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationText.Get("archive.extract_elsewhere"), AllowMultiple = false
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private Task QueueArchiveExtraction(string path, ArchiveExtractionOptions options, string? name = null)
        => IsBrowseOnly ? Task.CompletedTask : _archive.QueueExtraction(path, options,
            async () =>
            {
                _directoryChangeNotifier?.NotifyChanged([options.Directory], this);
                if (Path.IsPathFullyQualified(CurrentPath) && CurrentPath == options.Directory) await RefreshAsync();
            }, PromptPasswordAsync, name);
}
