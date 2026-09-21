using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OmniboxExactFilePathRevealsAndSelectsFile(bool fileUri, bool alreadyInFolder)
    {
        var root = Directory.CreateTempSubdirectory("MacExplorer_Omnibox_");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "文件夹 with spaces"));
            var path = Path.Combine(folder.FullName, "定位 target #1.txt");
            await File.WriteAllTextAsync(path, "test", TestContext.Current.CancellationToken);
            var service = new FakeFileService(alreadyInFolder ? folder.FullName : root.FullName);
            var target = new FileSystemEntry { FullPath = path, Name = Path.GetFileName(path) };
            service.Seed(target);
            using var viewModel = CreateViewModel(service);
            if (alreadyInFolder) viewModel.Entries.Add(target);

            await OmniboxService.ExecuteInputAsync(viewModel, fileUri ? new Uri(path).AbsoluteUri : path);

            Assert.Equal(folder.FullName, viewModel.CurrentPath);
            Assert.Equal(path, Assert.Single(viewModel.SelectedEntries).FullPath);
            Assert.False(viewModel.IsSearchMode);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OmniboxExactPathSuggestionNavigatesFilesAndDirectories(bool directory)
    {
        var root = Directory.CreateTempSubdirectory("MacExplorer_Omnibox_");
        try
        {
            var path = Path.Combine(root.FullName, directory ? "文件夹" : "文件.txt");
            if (directory) Directory.CreateDirectory(path);
            else await File.WriteAllTextAsync(path, "test", TestContext.Current.CancellationToken);
            var service = new FakeFileService(root.FullName);
            service.Seed(new FileSystemEntry { FullPath = path, Name = Path.GetFileName(path), IsDirectory = directory });
            using var viewModel = CreateViewModel(service);

            var suggestions = await new PathOmniboxProvider().GetSuggestionsAsync(viewModel, path,
                TestContext.Current.CancellationToken);
            var suggestion = Assert.Single(suggestions);
            Assert.Equal(path, suggestion.Value);
            Assert.Equal(directory ? Assets.Icons.Folder : Assets.Icons.File, suggestion.IconData);
            await OmniboxService.ExecuteAsync(viewModel, suggestion);

            Assert.Equal(directory ? path : root.FullName, viewModel.CurrentPath);
            if (!directory) Assert.Equal(path, Assert.Single(viewModel.SelectedEntries).FullPath);
            Assert.False(viewModel.IsSearchMode);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task OmniboxEnterUsesExactPathBeforeDefaultSuggestionButHonorsSelection(bool home, bool explicitlySelected)
    {
        var root = Directory.CreateTempSubdirectory("MacExplorer_Omnibox_");
        Window? window = null;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "目标文件夹"));
            var path = Path.Combine(folder.FullName, "target.txt");
            await File.WriteAllTextAsync(path, "test", TestContext.Current.CancellationToken);
            var service = new FakeFileService(root.FullName);
            service.Seed(new FileSystemEntry { FullPath = path, Name = Path.GetFileName(path) });
            using var viewModel = CreateViewModel(service);
            if (home) viewModel.GoHome();
            UserControl view = home ? new HomeView() : new BreadcrumbBar();
            view.DataContext = viewModel;
            window = new Window { Width = 900, Height = 600, Content = view };
            window.Show();
            if (view is BreadcrumbBar bar) bar.FocusPathInput();
            else ((HomeView)view).FocusOmnibox();
            var input = view.FindControl<TextBox>(home ? "HomeSearchBox" : "PathInput")!;
            var list = view.FindControl<ListBox>(home ? "HomeSearchSuggestionList" : "PathSuggestionList")!;
            input.Text = path;
            Dispatcher.UIThread.RunJobs();

            // Model suggestions left over while the asynchronous refresh is still pending.
            var suggestionExecuted = false;
            list.ItemsSource = new[]
            {
                new OmniboxSuggestion(OmniboxSuggestionKind.Command, "Old result", "", "old", "", "",
                    ExecuteAction: () => { suggestionExecuted = true; return Task.CompletedTask; })
            };
            list.SelectedIndex = explicitlySelected ? 0 : -1;
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!suggestionExecuted && viewModel.SelectedEntries.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.Equal(explicitlySelected, suggestionExecuted);
            if (!explicitlySelected)
            {
                Assert.Equal(folder.FullName, viewModel.CurrentPath);
                Assert.Equal(path, Assert.Single(viewModel.SelectedEntries).FullPath);
                Assert.False(viewModel.IsHomePage);
                Assert.False(viewModel.IsSearchMode);
            }
        }
        finally
        {
            window?.Close();
            root.Delete(recursive: true);
        }
    }
}
