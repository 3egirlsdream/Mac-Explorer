using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData("", 4)]
    [InlineData("com.kingsoft.wpsoffice.mac", 7)]
    [InlineData("com.microsoft.Word", 5)]
    [InlineData("com.apple.iWork.Pages", 5)]
    public async Task NewItemMenusShareAvailableActionsAndRenderedIcons(string installedApp, int count)
    {
        var theme = new FluentTheme();
        Application.Current!.Styles.Insert(0, theme);
        using var vm = CreateViewModel(new FakeFileService("/tmp/FKFinderNewMenu"),
            contextMenuService: new NewMenuContextService(installedApp));
        var toolbar = new FinderToolbar { DataContext = vm };
        var view = new FileListView { DataContext = vm };
        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        layout.Children.Add(view);
        var window = new Window { Width = 900, Height = 500, Content = layout };
        window.Show();
        try
        {
            await vm.LoadNewItemActionsAsync();
            toolbar.ToggleMenu(ToolbarMenuKind.New);
            Dispatcher.UIThread.RunJobs();
            var items = toolbar.FindControl<ItemsControl>("NewItems")!;
            Assert.Same(vm.NewItemActions, items.ItemsSource);
            var buttons = items.GetVisualDescendants().OfType<Button>().Where(button => button.IsVisible).ToArray();
            Assert.Equal(count, buttons.Length);
            Assert.Equal(count > 4 ? 1 : 0, vm.NewItemActions.Count(item => item.IsSeparator));
            toolbar.CloseDropdowns();

            var list = view.FindControl<FastFileList>("FastList")!;
            var point = list.TranslatePoint(new Point(70, 70), window)!.Value;
            window.MouseDown(point, MouseButton.Right, RawInputModifiers.RightMouseButton);
            window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var menu = Assert.Single(window.GetVisualDescendants().OfType<ContextMenu>());
            Assert.True(menu.IsOpen);
            var parent = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "新建");
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "新建文件夹");
            Assert.Same(vm.NewItemActions, Assert.Single(vm.ContextMenuActions, item => item.Label == "新建").SubItems);
            parent.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(parent.IsSubMenuOpen);
            var children = parent.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(count, children.Length);
            for (var i = 0; i < count; i++)
            {
                var action = Assert.IsType<ContextMenuAction>(buttons[i].DataContext);
                Assert.Equal(action.Label, children[i].Header);
                Assert.NotNull(action.Execute);
                Assert.Same(action.IconImage, Assert.IsType<Image>(children[i].Icon).Source);
                Assert.Same(action.IconImage, Assert.Single(buttons[i].GetVisualDescendants().OfType<Image>()).Source);
            }
            menu.Close();
        }
        finally
        {
            toolbar.CloseDropdowns();
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Application.Current!.Styles.Remove(theme);
        }
    }

    private sealed class NewMenuContextService(string installedApp) : IContextMenuService
    {
        public bool IsAppInstalled(string bundleIdentifier) => bundleIdentifier == installedApp;
        public Task<IReadOnlyList<ContextMenuAction>> GetFileContextMenuActionsAsync(FileSystemEntry entry) => Empty();
        public Task<IReadOnlyList<ContextMenuAction>> GetBackgroundContextMenuActionsAsync(string path) => Empty();
        public Task<IReadOnlyList<ContextMenuAction>> GetTrashFileContextMenuActionsAsync(FileSystemEntry entry) => Empty();
        public Task<IReadOnlyList<ContextMenuAction>> GetTrashBackgroundContextMenuActionsAsync() => Empty();
        public Task<IReadOnlyList<ContextMenuAction>> GetOpenWithActionsAsync(string path) => Empty();
        public Task<IReadOnlyList<ContextMenuAction>> GetTopLevelOpenWithActionsAsync(string path) => Empty();
        public Task<IReadOnlyList<RegisteredApp>> GetApplicationsForFileAsync(string path) => Task.FromResult<IReadOnlyList<RegisteredApp>>([]);
        public Task<string?> GetDefaultApplicationIconBase64Async(string path) => Task.FromResult<string?>(null);
        private static Task<IReadOnlyList<ContextMenuAction>> Empty() => Task.FromResult<IReadOnlyList<ContextMenuAction>>([]);
    }
}
