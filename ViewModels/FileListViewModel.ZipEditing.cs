using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Markdown;
using Microsoft.Extensions.DependencyInjection;
using Icons = MacExplorer.Assets.Icons;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    private ZipRevision? _writableZipRevision;
    private ZipRevision? _pendingArchiveDeleteRevision;

    internal bool CanCopySelection => !IsBrowseOnly && SelectedEntries.Count > 0 && (!IsArchiveView || CanModifyArchive);

    public bool CanModifyArchive => !IsBrowseOnly && IsArchiveView && _writableZipRevision != null;
    public bool CanCreateItems => !IsBrowseOnly && (!IsArchiveView || CanModifyArchive);

    private void SetWritableZipRevision(ZipRevision? revision)
    {
        if (_writableZipRevision == revision) return;
        _writableZipRevision = revision;
        OnPropertyChanged(nameof(CanModifyArchive));
        OnPropertyChanged(nameof(CanCreateItems));
    }

    private Task<ZipRevision?> ProbeWritableZipAsync(string path)
        => IsBrowseOnly || !ZipMutationService.IsCandidate(path)
            ? Task.FromResult<ZipRevision?>(null) : Task.Run(() => ZipMutationService.Probe(path));

    private async Task UpdateZipCapabilitiesAsync(string path, DirectoryWork work, Task<ZipRevision?> probe)
    {
        var revision = await probe;
        if (!IsCurrentDirectoryWork(work)) return;
        var current = revision is { } expected && await Task.Run(() => ZipMutationService.HasRevision(path, expected));
        if (IsCurrentDirectoryWork(work)) SetWritableZipRevision(current ? revision : null);
    }

    internal Func<byte[], byte[], CancellationToken, Task>? GetArchiveTextWriter(FileSystemEntry entry)
    {
        if (!CanModifyArchive || !ArchivePathHelper.IsArchivePath(entry.FullPath)) return null;
        var (path, key) = ArchivePathHelper.Parse(entry.FullPath);
        if (path != CurrentArchivePath || entry.Size > MarkdownDocument.MaxReadBytes) return null;
        return async (expected, data, token) =>
        {
            await _archive.RunZipMutationAsync(path, async (ct, progress) =>
            {
                await ZipMutationService.ReplaceTextAsync(path, key, expected, data, MarkdownDocument.MaxReadBytes, ct, progress);
                return true;
            }, token);
            await RefreshAfterZipUpdateAsync(path);
        };
    }

    internal bool TryGetZipDestination(FileSystemEntry? folder, out string path, out string directory)
    {
        path = CurrentArchivePath ?? "";
        directory = "";
        if (!CanModifyArchive || IsDirectoryLoading) return false;
        if (folder != null)
        {
            if (!folder.IsDirectory || !ArchivePathHelper.IsArchivePath(folder.FullPath)) return false;
            var target = ArchivePathHelper.Parse(folder.FullPath);
            if (target.archivePath != path) return false;
            directory = target.internalPath;
        }
        else directory = _navigation.CurrentArchiveInternalPath ?? "";
        if (directory.Length > 0 && !directory.EndsWith('/')) directory += "/";
        return true;
    }

    private async Task CopyArchiveSelectionAsync(IReadOnlyList<FileSystemEntry> entries)
    {
        if (_clipboardService == null || _archiveService == null || !CanModifyArchive || CurrentArchivePath is not { } path) return;
        var keys = NormalizeTreeOperationEntries(entries)
            .Where(entry => ArchivePathHelper.IsArchivePath(entry.FullPath))
            .Select(entry => ArchivePathHelper.Parse(entry.FullPath))
            .Where(item => item.archivePath == path)
            .Select(item => item.internalPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (keys.Length == 0) return;

        var directory = (_navigation.CurrentArchiveInternalPath ?? "").Trim('/');
        var prefix = directory.Length == 0 ? "" : directory + "/";
        var staging = Path.Combine(RuntimePaths.TestRoot ?? RuntimePaths.TemporaryDirectory,
            "MacExplorer-archive-clipboard", Guid.NewGuid().ToString("N"));
        var published = false;
        try
        {
            var copy = _clipboardService.CopyFilesAsync(async () =>
            {
                if (OperatingSystem.IsMacOS())
                    Directory.CreateDirectory(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                else Directory.CreateDirectory(staging);
                await _archive.ExportClipboardEntriesAsync(path, new ArchiveExtractionOptions(staging,
                    ArchiveExtractionMode.CurrentFolder, keys, directory));
                var paths = keys.Select(key =>
                {
                    if (!key.StartsWith(prefix, StringComparison.Ordinal))
                        throw new IOException(LocalizationText.Get("archive.edit_unsupported"));
                    return Path.Combine(staging, key[prefix.Length..].TrimEnd('/'));
                }).ToArray();
                if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length
                    || !paths.All(item => File.Exists(item) || Directory.Exists(item)))
                    throw new IOException(LocalizationText.Get("archive.edit_unreadable"));
                return paths;
            });
            OnPropertyChanged(nameof(HasPasteableContent));
            published = await copy;
            if (published) StatusText = LocalizationText.Get("已拷贝 {0} 项", keys.Length);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = LocalizationText.Get("archive.copy_failed", ex.Message); }
        finally
        {
            if (!published && Directory.Exists(staging))
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            OnPropertyChanged(nameof(HasPasteableContent));
        }
    }

    private ContextMenuAction BuildArchivePasteAction() => new()
    {
        Label = LocalizationText.Get("粘贴"), IconSvg = Icons.Paste, ShortcutId = ShortcutIds.Paste,
        IsQuickAction = true, IsEnabled = HasPasteableContent, Execute = PasteAsync
    };

    internal async Task<bool> AddFilesToZipAsync(string path, string directory, IReadOnlyList<string> paths)
    {
        try
        {
            var conflicts = App.Services?.GetService<IFileConflictResolver>();
            if (await _archive.RunZipMutationAsync(path,
                    (ct, progress) => ZipMutationService.AddAsync(path, directory, paths, conflicts, ct, progress), sources: paths))
                await RefreshAfterZipUpdateAsync(path);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            StatusText = LocalizationText.Get("archive.edit_write_error", ex.Message);
            return false;
        }
    }

    private async Task PasteIntoZipAsync()
    {
        if (!TryGetZipDestination(null, out var path, out var directory) || _clipboardService == null) return;
        var kind = _clipboardService.GetPasteKind();
        if (kind == ClipboardPasteKind.None) return;
        if (kind == ClipboardPasteKind.Image)
        {
            if (_clipboardService.ReadExternalImage() is { } image)
                await CreateZipItemAsync(path, directory, $"{LocalizationText.Get("archive.new_image_name")} {DateTime.Now:yyyy-MM-dd HH.mm.ss}{image.Extension}", image.Bytes, folder: false);
            else StatusText = LocalizationText.Get("无法读取剪贴板中的图片");
            return;
        }
        if (kind == ClipboardPasteKind.ExternalFiles && !_clipboardService.TryAdoptExternalFiles())
        {
            StatusText = LocalizationText.Get("clipboard.files_unavailable");
            return;
        }
        if (_clipboardService.GetClipboardEntry() is { IsEmpty: false } clipboard)
            await AddFilesToZipAsync(path, directory, clipboard.SourcePaths.ToArray());
    }

    private Task CreateZipItemAsync(string? extension, bool folder)
    {
        if (!TryGetZipDestination(null, out var path, out var directory)) return Task.CompletedTask;
        var ext = extension ?? ".txt";
        var name = folder ? LocalizationText.Get("archive.new_folder_name") : FileOpsViewModel.DefaultNewFileName(ext);
        return CreateZipItemAsync(path, directory, name, folder ? null : FileTemplateProvider.GetTemplate(ext), folder);
    }

    private async Task CreateZipItemAsync(string path, string directory, string name, byte[]? data, bool folder)
    {
        try
        {
            var key = await _archive.RunZipMutationAsync(path,
                (ct, progress) => ZipMutationService.CreateAsync(path, directory, name, data, folder, ct, progress));
            await RefreshAfterZipUpdateAsync(path, ArchivePathHelper.Build(path, folder ? key + "/" : key), rename: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = LocalizationText.Get("archive.edit_write_error", ex.Message); }
    }

    private Task DeleteZipEntriesAsync(IReadOnlyList<FileSystemEntry> entries, ZipRevision expected)
    {
        var items = entries.Select(item => ArchivePathHelper.Parse(item.FullPath)).ToArray();
        var path = items[0].archivePath;
        if (items.Any(item => item.archivePath != path))
            throw new IOException(LocalizationText.Get("archive.edit_unsupported"));
        return DeleteAsync();

        async Task DeleteAsync()
        {
            await _archive.RunZipMutationAsync(path, async (ct, progress) =>
            {
                await ZipMutationService.DeleteAsync(path, items.Select(item => item.internalPath).ToArray(), expected, ct, progress);
                return true;
            });
            await RefreshAfterZipUpdateAsync(path);
        }
    }

    private async Task<bool> RenameNewZipItemAsync(FileSystemEntry entry, string name)
    {
        if (!CanModifyArchive || _writableZipRevision is not { } expected) return false;
        var (path, key) = ArchivePathHelper.Parse(entry.FullPath);
        if (path != CurrentArchivePath) return false;
        try
        {
            var renamed = await _archive.RunZipMutationAsync(path,
                (ct, progress) => ZipMutationService.RenameAsync(path, key, name, expected, ct, progress));
            await RefreshAfterZipUpdateAsync(path, ArchivePathHelper.Build(path, renamed));
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            StatusText = LocalizationText.Get("重命名失败: {0}", ex.Message);
            return false;
        }
    }

    private async Task RefreshAfterZipUpdateAsync(string path, string? select = null, bool rename = false)
    {
        var isVisible = !_disposed && IsArchiveView && CurrentArchivePath == path;
        _directoryChangeNotifier?.NotifyChanged([Path.GetDirectoryName(path)!, ArchivePathHelper.Build(path, "")], this);
        if (!isVisible) return;
        await RefreshAsync();
        if (select != null && CurrentArchivePath == path && Entries.FirstOrDefault(item => item.FullPath == select) is { } item)
        {
            ReplaceSelection([item], item);
            ScrollBehaviorAfterLoad = ScrollMode.ScrollToSelected;
            if (rename) _fileOps.RaiseRequestRename(item);
        }
    }

    private List<ContextMenuAction> BuildArchiveBackgroundActions()
    {
        var actions = new List<ContextMenuAction>();
        if (CanModifyArchive)
        {
            actions.Add(new() { Label = LocalizationText.Get("新建"), IconSvg = Icons.Plus, SubItems = NewItemActions });
            actions.Add(BuildArchivePasteAction());
            actions.Add(ContextMenuAction.Separator);
        }
        actions.Add(new() { Label = LocalizationText.Get("刷新"), IconSvg = Icons.Refresh, ShortcutId = ShortcutIds.Refresh, Execute = RefreshAsync });
        return actions;
    }
}
