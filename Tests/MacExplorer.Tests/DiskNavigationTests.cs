using Avalonia.Headless.XUnit;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "DiskManagement")]
public sealed class DiskNavigationTests : FileListViewModelTestBase
{
    [AvaloniaFact]
    public async Task DiskPageParticipatesInHistoryAndRefreshDoesNotEnumerateDirectory()
    {
        var files = new FakeFileService(RuntimePaths.TestRoot!);
        using var model = CreateViewModel(files, navigation: new NavigationViewModel(files));
        await model.NavigateToAsync(files.HomeDirectory);
        var reads = files.EnumerateDirectoryCallCount;
        await model.NavigateToAsync(VirtualPath.Disks);
        Assert.True(model.IsDiskManagementPage);
        Assert.False(model.IsHomePage);
        Assert.False(model.IsNetworkPage);
        Assert.Empty(model.Entries);
        Assert.Equal(reads, files.EnumerateDirectoryCallCount);
        Assert.False(model.Breadcrumbs.Single().HasDropdown);
        var refreshes = 0;
        model.DiskManagementRefreshRequested += () => refreshes++;
        await model.RefreshAsync();
        Assert.Equal(1, refreshes);
        Assert.Equal(reads, files.EnumerateDirectoryCallCount);
        await model.NavigateBackAsync();
        Assert.False(model.IsDiskManagementPage);
        await model.NavigateForwardAsync();
        Assert.True(model.IsDiskManagementPage);
        await model.NavigateUpAsync();
        Assert.True(model.IsHomePage);
    }
}
