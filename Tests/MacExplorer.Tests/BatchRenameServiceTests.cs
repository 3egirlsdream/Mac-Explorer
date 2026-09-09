using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed class BatchRenameServiceTests
{
    [Fact]
    public void GeneratePreview_AddsPrefixWithoutChangingExtension()
    {
        var service = new BatchRenameService(new FakeFileService());
        var entries = new[]
        {
            new FileSystemEntry
            {
                FullPath = "/tmp/photo.jpg",
                Name = "photo.jpg",
                IsDirectory = false
            }
        };

        var preview = service.GeneratePreview(entries, [
            new BatchRenameRule
            {
                Type = BatchRenameRuleType.AddPrefix,
                PrefixText = "edited-"
            }
        ]);

        Assert.Single(preview);
        Assert.Equal("edited-photo.jpg", preview[0].NewName);
        Assert.False(preview[0].HasError);
        Assert.False(preview[0].HasConflict);
    }

    [Fact]
    public void GeneratePreview_FlagsDuplicateNamesInsideBatch()
    {
        var service = new BatchRenameService(new FakeFileService());
        var entries = new[]
        {
            new FileSystemEntry { FullPath = "/tmp/a.txt", Name = "a.txt" },
            new FileSystemEntry { FullPath = "/tmp/b.txt", Name = "b.txt" }
        };

        var preview = service.GeneratePreview(entries, [
            new BatchRenameRule
            {
                Type = BatchRenameRuleType.CaseConversion,
                CaseMode = CaseConversionMode.Lowercase
            },
            new BatchRenameRule
            {
                Type = BatchRenameRuleType.FindReplace,
                FindText = "b",
                ReplaceText = "a"
            }
        ]);

        Assert.Contains(preview, item => item.HasConflict);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsProgressAndSuccessfulItems()
    {
        var fileService = new FakeFileService();
        var service = new BatchRenameService(fileService);
        var progressValues = new List<BatchRenameProgress>();
        var progress = new InlineProgress<BatchRenameProgress>(progressValues.Add);
        var items = new List<BatchRenamePreviewItem>
        {
            new()
            {
                OriginalPath = "/tmp/a.txt",
                OriginalName = "a.txt",
                NewName = "b.txt",
                NewPath = "/tmp/b.txt"
            }
        };

        var result = await service.ExecuteAsync(
            items,
            progress,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.SuccessCount);
        Assert.Single(result.SuccessfulItems);
        Assert.Equal(("/tmp/a.txt", "b.txt"), fileService.Renames.Single());
        Assert.Contains(progressValues, update => update.CompletedCount == 1 && update.TotalCount == 1);
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesFileTagPath()
    {
        var fileService = new FakeFileService();
        var tagService = new TrackingFileTagService();
        var service = new BatchRenameService(fileService, fileTagService: tagService);
        var items = new List<BatchRenamePreviewItem>
        {
            new()
            {
                OriginalPath = "/tmp/a.txt",
                OriginalName = "a.txt",
                NewName = "b.txt",
                NewPath = "/tmp/b.txt"
            }
        };

        var result = await service.ExecuteAsync(
            items,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(("/tmp/a.txt", "/tmp/b.txt"), Assert.Single(tagService.PathUpdates));
    }

    private sealed class TrackingFileTagService : IFileTagService
    {
        public event EventHandler? TagsChanged { add { } remove { } }
        public event EventHandler<TagRenamedEventArgs>? TagRenamed { add { } remove { } }
        public Task<FileTag> CreateTagAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RenameTagAsync(FileTag tag, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTagAsync(FileTag tag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetTagColorAsync(FileTag tag, int color, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetTagPinnedAsync(FileTag tag, bool pinned, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileTag>> GetFileTagsAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TagSyncResult> SetTagAsync(IReadOnlyList<string> paths, FileTag tag, bool applied, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TagSyncResult> RetryPendingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public List<(string OldPath, string NewPath)> PathUpdates { get; } = [];

        public Task<IReadOnlyList<FileTag>> GetSidebarTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FileTag>>([]);

        public Task<IReadOnlyList<string>> FindFilePathsAsync(FileTag tag, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task ReplaceFileTagsAsync(string filePath, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdatePathAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
        {
            PathUpdates.Add((oldPath, newPath));
            return Task.CompletedTask;
        }

        public Task CopyPathAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeletePathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeFileService : IFileService
    {
        public List<(string Path, string NewName)> Renames { get; } = [];
        public string HomeDirectory => "/tmp";
        public string RootDirectory => "/";
        public string TrashDirectory => "/tmp/.Trash";

        public Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FileSystemEntry>>([]);

        public IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryBatchesAsync(string path, int batchSize = 256, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<IReadOnlyList<FileSystemEntry>>();

        public Task<FileSystemEntry?> GetEntryAsync(string path)
            => Task.FromResult<FileSystemEntry?>(null);

        public Task<bool> ExistsAsync(string path)
            => Task.FromResult(false);

        public Task<string> CreateFolderAsync(string parentPath, string name)
            => Task.FromResult(Path.Combine(parentPath, name));

        public Task<string> CreateFileAsync(string parentPath, string name)
            => Task.FromResult(Path.Combine(parentPath, name));

        public Task<string> CreateFileWithContentAsync(string parentPath, string name, byte[] content)
            => Task.FromResult(Path.Combine(parentPath, name));

        public Task DeleteAsync(string path, bool moveToTrash = true)
            => Task.CompletedTask;

        public Task RenameAsync(string path, string newName)
        {
            Renames.Add((path, newName));
            return Task.CompletedTask;
        }

        public Task MoveAsync(string sourcePath, string destinationPath, bool overwrite = false)
            => Task.CompletedTask;

        public Task CopyAsync(string sourcePath, string destinationDirectory)
            => Task.CompletedTask;

        public string GetParentPath(string path)
            => Path.GetDirectoryName(path) ?? "";

        public string CombinePath(string directory, string name)
            => Path.Combine(directory, name);

        public IReadOnlyList<string> GetVolumes()
            => [];

        public Task DeletePermanentlyAsync(string path)
            => Task.CompletedTask;

        public Task EmptyTrashAsync()
            => Task.CompletedTask;

        public Task ResolveAppIconsAsync(
            IEnumerable<FileSystemEntry> entries,
            Action? onBatchResolved = null,
            CancellationToken cancellationToken = default)
        {
            onBatchResolved?.Invoke();
            return Task.CompletedTask;
        }

        public bool IsCrossVolume(string sourcePath, string destinationPath)
            => false;

        public Task MoveWithProgressAsync(
            IReadOnlyList<string> sourcePaths,
            string destinationDirectory,
            IProgress<FileOperationProgress>? progress = null,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
