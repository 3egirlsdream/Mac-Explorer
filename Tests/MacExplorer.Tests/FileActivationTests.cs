using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using MacExplorer.Models;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivatedFileKeepsItsNameAndIsSelectedInItsParentFolder(bool alreadyInFolder)
    {
        var root = Directory.CreateTempSubdirectory("MacExplorer_Activation_");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "文件夹 with spaces"));
            var path = Path.Combine(folder.FullName, "定位 target #1.txt");
            await File.WriteAllTextAsync(path, "test", TestContext.Current.CancellationToken);
            var fileService = new FakeFileService(alreadyInFolder ? folder.FullName : root.FullName);
            var target = new FileSystemEntry { FullPath = path, Name = Path.GetFileName(path) };
            fileService.Seed(target);
            using var viewModel = CreateViewModel(fileService);
            if (alreadyInFolder) viewModel.Entries.Add(target);
            var scrollRequested = false;
            viewModel.ScrollToSelectionRequested += () => scrollRequested = true;

            using var storageItem = await new Window().StorageProvider.TryGetFileFromPathAsync(new Uri(path));
            Assert.NotNull(storageItem);
            var activatedPath = App.GetActivatedPath(new FileActivatedEventArgs([storageItem]));
            Assert.Equal(path, activatedPath);
            await viewModel.RevealFileAsync(new FileSystemEntry
            {
                FullPath = activatedPath!, Name = Path.GetFileName(activatedPath)!
            });

            Assert.Equal(folder.FullName, viewModel.CurrentPath);
            Assert.Equal(path, Assert.Single(viewModel.SelectedEntries).FullPath);
            if (alreadyInFolder)
            {
                Assert.Equal(0, fileService.EnumerateDirectoryCallCount);
                Assert.True(scrollRequested);
            }
            else
            {
                Assert.Equal(FileListViewModel.ScrollMode.ScrollToSelected, viewModel.ScrollBehaviorAfterLoad);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void OrdinaryActivationDoesNotNavigate()
    {
        Assert.Null(App.GetActivatedPath(new ActivatedEventArgs(ActivationKind.Reopen)));
        Assert.Null(App.GetActivatedPath(new FileActivatedEventArgs([])));
    }
}
