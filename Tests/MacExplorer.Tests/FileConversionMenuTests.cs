using Avalonia.Controls;
using Avalonia;
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
using MacExplorer.Services.Impl;
using MacExplorer.Views.Dialogs;
using MacExplorer.Views;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public void RightPressAndReleaseOpensConversionSubmenuInFastList()
    {
        using var plugins = new PluginTestEnvironment();
        var files = new FakeFileService("/tmp/FKFinderConversionMenu");
        using var vm = CreateViewModel(files, sortFilter: new SortFilterViewModel { ViewMode = ViewMode.List }, pluginManager: plugins.Manager);
        vm.Entries.Add(new FileSystemEntry { FullPath = "/tmp/FKFinderConversionMenu/document.md", Name = "document.md", Extension = ".md", IconKey = "file-markdown" });
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 360, Content = view };
        window.Styles.Add(new FluentTheme());
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<FastFileList>("FastList")!;
            var origin = list.TranslatePoint(default, window)!.Value;
            var point = new Point(origin.X + 70, origin.Y + 15);
            window.MouseDown(point, MouseButton.Right, RawInputModifiers.RightMouseButton);
            window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var menu = Assert.Single(window.GetVisualDescendants().OfType<ContextMenu>());
            Assert.True(menu.IsOpen);
            var conversion = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "文件转换");
            conversion.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(conversion.IsSubMenuOpen);
            Assert.Equal(new[] { "转为 Word（.docx）", "转为 PDF" }, conversion.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()));
            Assert.Collection(conversion.Items.OfType<MenuItem>(),
                item => Assert.NotNull(item.Icon),
                item => Assert.NotNull(item.Icon));
            menu.Close();
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public async Task ConversionMenuMatchesBothBuildPhasesAndExcludesUnsupportedSelections()
    {
        var root = Path.Combine(Path.GetTempPath(), "fkfinder-menu-convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var first = new FileSystemEntry { FullPath = Path.Combine(root, "first.md"), Name = "first.md" };
        var second = new FileSystemEntry { FullPath = Path.Combine(root, "second.md"), Name = "second.md" };
        using var plugins = new PluginTestEnvironment();
        var files = new FakeFileService(root); files.Seed(first); files.Seed(second);
        using var vm = CreateViewModel(files, pluginManager: plugins.Manager);
        try
        {
            await vm.RefreshAsync();
            vm.SetSelection([first]);
            await vm.ShowFileContextMenuAsync(first, 0, 0);
            var fast = Assert.Single(vm.ContextMenuActions, item => item.Label == "文件转换");
            var complete = Assert.Single(await vm.LoadCompleteFileContextMenuAsync(first), item => item.Label == "文件转换");
            Assert.Equal(new[] { "转为 Word（.docx）", "转为 PDF" }, fast.SubItems!.Select(item => item.Label));
            Assert.Equal(fast.SubItems.Select(item => item.Label), complete.SubItems!.Select(item => item.Label));
            vm.SetSelection([first, second]);
            var batch = Assert.Single(await vm.LoadCompleteFileContextMenuAsync(first), item => item.Label == "文件转换");
            Assert.Equal(new[] { "转为 Word（.docx）", "转为 PDF" }, batch.SubItems!.Select(item => item.Label));
            vm.SetSelection([first, new FileSystemEntry { FullPath = Path.Combine(root, "photo.png"), Name = "photo.png" }]);
            Assert.DoesNotContain(await vm.LoadCompleteFileContextMenuAsync(first), item => item.Label == "文件转换");
            vm.ClearSelection();
            foreach (var entry in new[]
            {
                new FileSystemEntry { FullPath = root, Name = "folder.md", IsDirectory = true },
                new FileSystemEntry { FullPath = "sftp://server/file.md", Name = "file.md" },
                new FileSystemEntry { FullPath = Path.Combine(root, "file.md"), Name = "file.md", IsVirtual = true },
                new FileSystemEntry { FullPath = Path.Combine(vm.TrashPath, "file.md"), Name = "file.md" },
                new FileSystemEntry { FullPath = Path.Combine(root, "file.png"), Name = "file.png" }
            })
                Assert.DoesNotContain(await vm.LoadCompleteFileContextMenuAsync(entry), item => item.Label == "文件转换");
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task PluginTaskPanelEntryAppearsOnlyWhenPluginRequestsIt()
    {
        using var plugins = new PluginTestEnvironment();
        await plugins.Manager.InstallAsync(plugins.CreateFixture());
        var root = Path.Combine(Path.GetTempPath(), "fkfinder-plugin-tasks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var window = new Window();
        try
        {
            var first = new FileSystemEntry { FullPath = Path.Combine(root, "first.txt"), Name = "first.txt" };
            var second = new FileSystemEntry { FullPath = Path.Combine(root, "second.txt"), Name = "second.txt" };
            await File.WriteAllTextAsync(first.FullPath, "first");
            await File.WriteAllTextAsync(second.FullPath, "second");
            var files = new FakeFileService(root); files.Seed(first); files.Seed(second);
            var tasks = new BackgroundTaskManager();
            using var vm = CreateViewModel(files, pluginManager: plugins.Manager, backgroundTaskManager: tasks);
            vm.SetOwnerWindow(window);
            await vm.RefreshAsync();

            vm.SetSelection([first]);
            await RunPluginCommandAsync(vm, first, "run");
            Assert.Empty(tasks.Tasks);

            vm.SetSelection([first, second]);
            await RunPluginCommandAsync(vm, first, "batch");
            var task = Assert.Single(tasks.Tasks);
            Assert.Equal("测试批量", task.Label);
            Assert.Equal(BackgroundTaskState.Completed, task.State);
            Assert.Equal([Path.Combine(root, "first.txt.copy"), Path.Combine(root, "second.txt.copy")],
                Directory.GetFiles(root, "*.copy").Order(StringComparer.Ordinal));
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    private static async Task RunPluginCommandAsync(FileListViewModel viewModel, FileSystemEntry entry, string commandLabel)
    {
        var plugin = Assert.Single(await viewModel.LoadCompleteFileContextMenuAsync(entry), item => item.Label == "测试插件");
        var execution = Assert.Single(plugin.SubItems!, item => item.Label == commandLabel).Execute!();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!execution.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(execution.IsCompleted, "插件命令未在限时内完成。");
        await execution;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ImageConversionDialogKeepsAspectRatio()
    {
        var dialog = new ImageConversionDialog(new ConversionImageSize(80, 40), FileConversionFormat.Png);
        var width = dialog.FindControl<NumericUpDown>("WidthInput")!;
        var height = dialog.FindControl<NumericUpDown>("HeightInput")!;
        width.Value = 320;
        Assert.Equal(160m, height.Value);
        height.Value = 240;
        Assert.Equal(480m, width.Value);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageSizeSpinnersStayBorderlessDuringRepeatedClicks(bool dark)
    {
        var fluent = new FluentTheme();
        var components = (Avalonia.Styling.Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml"));
        Application.Current!.Styles.Insert(0, fluent);
        Application.Current.Styles.Add(components);
        var dialog = new ImageConversionDialog(new ConversionImageSize(512, 256), FileConversionFormat.Png)
        {
            RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light
        };
        dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            foreach (var name in new[] { "WidthInput", "HeightInput" })
            {
                var input = dialog.FindControl<NumericUpDown>(name)!;
                var editor = input.GetVisualDescendants().OfType<TextBox>().Single();
                editor.Focus();
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(((Grid)input.Parent!).Children.OfType<Border>());
                Assert.Equal(new Thickness(0), input.BorderThickness);
                var editorBorder = editor.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_BorderElement");
                var initial = input.Value;
                foreach (var button in input.GetVisualDescendants().OfType<RepeatButton>())
                {
                    var presenter = button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                        .Single(p => p.Name == "PART_ContentPresenter");
                    Assert.Equal(Avalonia.Media.Colors.Transparent, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(presenter.BorderBrush).Color);
                    Assert.Equal(new Thickness(0), input.BorderThickness);
                    var bounds = button.Bounds;
                    var point = button.TranslatePoint(new Point(bounds.Width / 2, bounds.Height / 2), dialog)!.Value;
                    for (var click = 0; click < 3; click++)
                    {
                        dialog.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
                        for (var frame = 0; frame < 5; frame++)
                        {
                            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                            Dispatcher.UIThread.RunJobs();
                            Assert.True(input.IsKeyboardFocusWithin);
                            Assert.Equal(new Thickness(0), input.BorderThickness);
                            Assert.False(editorBorder.IsVisible);
                            Assert.Equal(bounds, button.Bounds);
                        }
                        dialog.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
                        Dispatcher.UIThread.RunJobs();
                        Assert.True(input.IsKeyboardFocusWithin);
                        Assert.Equal(new Thickness(0), input.BorderThickness);
                    }
                }
                Assert.Equal(initial, input.Value);
            }
        }
        finally
        {
            dialog.Close(); Dispatcher.UIThread.RunJobs();
            Application.Current.Styles.Remove(components);
            Application.Current.Styles.Remove(fluent);
        }
    }
}
