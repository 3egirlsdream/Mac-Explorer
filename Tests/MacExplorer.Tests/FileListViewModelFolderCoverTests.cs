using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Indexing;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public async Task ExplicitRefreshInvalidatesCoversEvenWhenDirectoryRowsAreUnchanged()
    {
        var root = Directory.CreateTempSubdirectory("FolderCoverRefresh_").FullName;
        try
        {
            using var model = CreateViewModel(new FakeFileService(root));
            var refreshes = 0;
            model.FolderCoversRefreshRequested += () => refreshes++;
            await model.RefreshAsync();
            Assert.Equal(1, refreshes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public void FolderCoverSettingIsOffByDefaultAndUpdatesOpenViewsImmediately()
    {
        var root = Directory.CreateTempSubdirectory("FolderCoverSetting_").FullName;
        try
        {
            var databasePath = Path.Combine(root, "index.db");
            using var index = new SqliteFileIndex(databasePath);
            using (var settings = new SettingsService(new DatabaseConnectionFactory(databasePath)))
            {
                var files = new FakeFileService(root);
                using var first = CreateViewModel(files, settingsService: settings,
                    sortFilter: new SortFilterViewModel { ViewMode = ViewMode.Grid });
                using var second = CreateViewModel(files, settingsService: settings,
                    sortFilter: new SortFilterViewModel { ViewMode = ViewMode.Grid });
                var firstView = new FileListView { DataContext = first };
                var secondView = new FileListView { DataContext = second };
                var window = new Window
                {
                    Width = 900, Height = 600,
                    Content = new StackPanel { Children = { firstView, secondView } }
                };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    var firstList = firstView.FindControl<FastFileList>("FastList")!;
                    var secondList = secondView.FindControl<FastFileList>("FastList")!;
                    Assert.False(first.ShowFolderPhotoCovers);
                    Assert.False(firstList.FolderCoversEnabled);

                    settings.Set(FileListViewModel.FolderPhotoCoverSettingKey, true);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(firstList.FolderCoversEnabled);
                    Assert.True(secondList.FolderCoversEnabled);

                    first.SetViewMode(ViewMode.List);
                    Assert.False(firstList.FolderCoversEnabled);
                    settings.Set(FileListViewModel.FolderPhotoCoverSettingKey, false);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(secondList.FolderCoversEnabled);
                }
                finally { window.Close(); }
                settings.Set(FileListViewModel.FolderPhotoCoverSettingKey, true);
            }
            using var reopened = new SettingsService(new DatabaseConnectionFactory(databasePath));
            Assert.True(reopened.Get(FileListViewModel.FolderPhotoCoverSettingKey, false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
