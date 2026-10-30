using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "UnifiedTagInteraction")]
public sealed class TagDeleteInteractionTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData("menu", true, true)]
    [InlineData("toolbar", true, false)]
    [InlineData("overflow", false, false)]
    [InlineData("delete", false, false)]
    [InlineData("shortcut", true, true)]
    [InlineData("request", false, false)]
    public async Task DeleteRemovesOnlyCurrentTagFromSelectedItemsAndRefreshesList(string entrypoint, bool confirm, bool finderColor)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("tag-delete-");
        try
        {
            var path = Path.Combine(root.FullName, "file.txt");
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "folder")).FullName;
            var child = Path.Combine(folder, "child.txt");
            await File.WriteAllTextAsync(path, "original", ct);
            await File.WriteAllTextAsync(child, "child", ct);
            using var tags = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root.FullName, "tags.db")));
            var a = finderColor ? FileTagCatalog.FinderColors[0] : await tags.CreateTagAsync("A", ct);
            var c = await tags.CreateTagAsync("C", ct);
            await tags.SetTagAsync([path, folder, child], a, true, ct);
            await tags.SetTagAsync([path, folder], c, true, ct);
            var files = new FakeFileService(root.FullName);
            files.Seed(new FileSystemEntry { FullPath = path, Name = "file.txt" });
            files.Seed(new FileSystemEntry { FullPath = folder, Name = "folder", IsDirectory = true });
            files.Seed(new FileSystemEntry { FullPath = child, Name = "child.txt" });
            using var vm = CreateViewModel(files, fileTagService: tags);
            vm.ConfirmBeforeTrash = confirm;
            await vm.NavigateToTagAsync(a);
            vm.SetSelection(vm.Entries.Where(entry => entry.FullPath != child).ToArray());
            if (entrypoint == "menu")
            {
                var actions = await vm.LoadCompleteFileContextMenuAsync(vm.Entries.Single(entry => entry.FullPath == path));
                var remove = Assert.Single(actions, action => action.ShortcutId == ShortcutIds.Trash);
                Assert.Equal(LocalizationText.Get("删除"), remove.Label);
                await remove.Execute!();
            }
            else if (entrypoint is "toolbar" or "overflow")
            {
                var toolbar = new FinderToolbar { DataContext = vm };
                toolbar.FindControl<Button>(entrypoint == "toolbar" ? "DeleteButton" : "DeleteOverflowButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (entrypoint is "delete" or "shortcut")
            {
                var view = new FileListView { DataContext = vm };
                Assert.True(view.TryHandleFileShortcut(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = entrypoint == "delete" ? Key.Delete : Key.Back,
                    KeyModifiers = entrypoint == "delete" ? KeyModifiers.None : KeyModifiers.Meta
                }));
            }
            else await vm.RequestDeleteSelectedAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (vm.Entries.Count != 1 || vm.Entries[0].FullPath != child || vm.IsDirectoryLoading)
                await Task.Delay(10, timeout.Token);
            Assert.False(vm.IsDeleteConfirmDialogVisible);
            Assert.Empty(files.DeleteRequests);
            Assert.Empty(vm.SelectedEntries);
            Assert.Equal(child, Assert.Single(vm.Entries).FullPath);
            Assert.Equal(new[] { "C" }, (await tags.GetFileTagsAsync(path, ct)).Select(tag => tag.Name));
            Assert.Equal(new[] { "C" }, (await tags.GetFileTagsAsync(folder, ct)).Select(tag => tag.Name));
            Assert.Contains(await tags.GetFileTagsAsync(child, ct), tag => tag.Name == a.Name);
            Assert.Equal("original", await File.ReadAllTextAsync(path, ct));
            Assert.Equal("child", await File.ReadAllTextAsync(child, ct));
            await vm.NavigateToAsync(root.FullName);
            await vm.NavigateToTagAsync(a);
            Assert.Equal(child, Assert.Single(vm.Entries).FullPath);
            Dispatcher.UIThread.RunJobs();
        }
        finally { root.Delete(true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task DeleteTooltipAndOverflowLabelFollowLocationAndLanguage()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("tag-delete-label-");
        using var localization = new LocalizationService(new StartupSettings());
        Window? window = null;
        try
        {
            using var tags = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root.FullName, "tags.db")));
            var a = await tags.CreateTagAsync("A", ct);
            using var vm = CreateViewModel(new FakeFileService(root.FullName), fileTagService: tags);
            var toolbar = new FinderToolbar { DataContext = vm };
            window = new Window { Content = toolbar };
            window.Show();
            var button = toolbar.FindControl<Button>("DeleteButton")!;
            foreach (var language in new[] { "zh-CN", "zh-Hant", "en", "fr" })
            {
                localization.SetLanguage(language);
                await vm.NavigateToTagAsync(a);
                Dispatcher.UIThread.RunJobs();
                var label = LocalizationText.Get("删除");
                Assert.Equal(label, AutomationProperties.GetName(button));
                Assert.StartsWith(label + " ", Assert.IsType<string>(ToolTip.GetTip(button)));
                Assert.Equal(label, AutomationProperties.GetName(toolbar.FindControl<Button>("DeleteOverflowButton")!));
                await vm.NavigateToAsync(root.FullName);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(LocalizationText.Get("ui.024"), AutomationProperties.GetName(button));
                Assert.StartsWith(LocalizationText.Get("ui.024") + " ", Assert.IsType<string>(ToolTip.GetTip(button)));
            }
        }
        finally
        {
            window?.Close();
            root.Delete(true);
        }
    }
}
