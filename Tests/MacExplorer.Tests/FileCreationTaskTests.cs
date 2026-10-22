using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Smoke", "UI")]
[Trait("Area", "FileTasks")]
public sealed class FileCreationTaskTests : FileListViewModelTestBase
{
    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData(".txt")]
    [InlineData(".docx")]
    public async Task QueuedCreationCompletesAndSelectsTheNewItemOnTheUiThread(string? extension)
    {
        var files = new FakeFileService("/tmp/FKFinderCreateTasks");
        var manager = new BackgroundTaskManager();
        using var vm = CreateViewModel(files, backgroundTaskManager: manager);
        var renamed = new List<FileSystemEntry>();
        vm.RenameRequested += entry =>
        {
            Dispatcher.UIThread.VerifyAccess();
            renamed.Add(entry);
        };

        if (extension == null) await vm.CreateNewFolderAsync();
        else await vm.CreateNewFileAsync(extension);

        var task = Assert.Single(manager.Tasks);
        Assert.True(task.State == BackgroundTaskState.Completed, task.ErrorDetail);
        Assert.Null(task.ErrorMessage);
        Assert.Equal(100, task.Progress);
        Assert.False(task.CanRetry);
        var created = Assert.Single(vm.Entries);
        Assert.True(await files.ExistsAsync(created.FullPath));
        Assert.Equal(extension == null, created.IsDirectory);
        Assert.Same(created, Assert.Single(vm.SelectedEntries));
        Assert.Same(created, Assert.Single(renamed));
        Assert.Equal(1, files.EnumerateDirectoryCallCount);
    }

    [AvaloniaFact]
    public async Task QueuedImagePasteCompletesAndSelectsTheNewImageOnTheUiThread()
    {
        var files = new FakeFileService("/tmp/FKFinderCreateTasks");
        var manager = new BackgroundTaskManager();
        var image = new ClipboardImageData([1, 2, 3], ".png");
        using var vm = CreateViewModel(files, backgroundTaskManager: manager,
            clipboardService: new ImageClipboardService(image));
        vm.PropertyChanged += (_, _) => Dispatcher.UIThread.VerifyAccess();

        await vm.PasteAsync();

        var task = Assert.Single(manager.Tasks);
        Assert.True(task.State == BackgroundTaskState.Completed, task.ErrorDetail);
        Assert.Null(task.ErrorMessage);
        var written = Assert.Single(files.CreatedFiles);
        Assert.Equal(image.Bytes, written.Content);
        var created = Assert.Single(vm.Entries);
        Assert.Equal(written.Path, created.FullPath);
        Assert.Same(created, Assert.Single(vm.SelectedEntries));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshFailureKeepsSuccessfulCreationCompletedAndCannotRetryIt(bool folder)
    {
        var files = new FakeFileService("/tmp/FKFinderCreateTasks");
        var manager = new BackgroundTaskManager();
        var operations = new FileOpsViewModel(fileService: files, taskManager: manager);
        var refreshError = new InvalidOperationException("refresh failed");
        Task Refresh(string _) => throw refreshError;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => folder
            ? operations.CreateNewFolderAsync(files.HomeDirectory, [], refreshCallback: Refresh)
            : operations.CreateNewFileAsync(files.HomeDirectory, [], refreshCallback: Refresh));

        Assert.Same(refreshError, error);
        var task = Assert.Single(manager.Tasks);
        Assert.Equal(BackgroundTaskState.Completed, task.State);
        Assert.Null(task.ErrorMessage);
        Assert.False(task.CanRetry);
        manager.RetryTask(task.Id);
        Assert.Single(await files.GetDirectoryContentsAsync(files.HomeDirectory));
        Assert.Single(manager.Tasks);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationFailureRemainsActionableAndReportsStatusOnTheUiThread(bool folder)
    {
        var root = Path.Combine(Path.GetTempPath(), "fkfinder-create-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var invalidParent = Path.Combine(root, "file-not-folder");
            await File.WriteAllTextAsync(invalidParent, "keep");
            var manager = new BackgroundTaskManager();
            var operations = new FileOpsViewModel(fileService: new MacFileService(), taskManager: manager);
            string? status = null;
            void SetStatus(string value)
            {
                Dispatcher.UIThread.VerifyAccess();
                status = value;
            }

            var error = await Assert.ThrowsAnyAsync<IOException>(() => folder
                ? operations.CreateNewFolderAsync(invalidParent, [], setStatus: SetStatus)
                : operations.CreateNewFileAsync(invalidParent, [], setStatus: SetStatus));

            var task = Assert.Single(manager.Tasks);
            Assert.Equal(BackgroundTaskState.NeedsAttention, task.State);
            Assert.Equal(error.Message, task.ErrorMessage);
            Assert.True(task.CanRetry);
            Assert.Contains(error.Message, status);
            Assert.Equal("keep", await File.ReadAllTextAsync(invalidParent));
            Assert.Single(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
