using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "InfoPanelTag")]
public sealed class InfoPanelTagSearchTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchesCustomTagsAndAddsByClickOrReturnWithoutCreatingPartialNames(bool dark)
    {
        var root = Directory.CreateTempSubdirectory("info-tag-search-").FullName;
        using var service = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root, "tags.db")));
        using var language = new LocalizationService(new StartupSettings(), () => AppLanguage.ChineseSimplified);
        var parent = await service.CreateTagAsync("Project");
        var child = await service.CreateChildTagAsync("Project Alpha", parent.Name);
        var archive = await service.CreateTagAsync("X Project Archive");
        var existing = await service.CreateTagAsync("Project Existing");
        var path = Path.Combine(root, "file.bin");
        await service.SetTagAsync([path], existing, true);
        using var vm = CreateViewModel(new FakeFileService(root), fileTagService: service);
        vm.IsInfoPanelVisible = true;
        vm.SelectedEntries.Add(new FileSystemEntry { FullPath = path, Name = "file.bin" });
        var panel = new InfoPanelView(service) { DataContext = vm };
        var window = new Window { Width = 320, Height = 1000, Content = panel,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var input = panel.FindControl<TextBox>("TagInput")!;
            var list = panel.FindControl<ListBox>("TagSuggestions")!;
            var popup = panel.FindControl<Popup>("TagSuggestionsPopup")!;
            input.BringIntoView(); input.Focus(); Dispatcher.UIThread.RunJobs();
            Assert.Contains("搜索", input.PlaceholderText);
            Assert.False(popup.IsOpen);
            input.Text = "色"; Dispatcher.UIThread.RunJobs();
            Assert.False(popup.IsOpen);
            input.Text = "pRoJ";
            await UntilAsync(() => popup.IsOpen && list.ItemCount == 3);
            Assert.Equal(new[] { parent.Name, child.Name, archive.Name }, list.Items.OfType<FileTag>().Select(t => t.Name));
            Assert.All(list.Items.OfType<FileTag>(), tag => Assert.True(tag.IsCustom));
            Assert.DoesNotContain(list.Items.OfType<FileTag>(), tag => tag.Name == existing.Name);
            list.UpdateLayout();
            var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
            var popupRoot = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(row));
            var point = row.TranslatePoint(new Point(row.Bounds.Width - 4, row.Bounds.Height / 2), popupRoot)!.Value;
            popupRoot.MouseDown(point, MouseButton.Left); popupRoot.MouseUp(point, MouseButton.Left);
            await UntilAsync(() => !popup.IsOpen && input.Text == "");
            Assert.Contains(child.Name, (await service.GetFileTagsAsync(path)).Select(t => t.Name));
            Assert.DoesNotContain(await service.GetSidebarTagsAsync(), tag => tag.Name == "pRoJ");

            input.Text = "project";
            await UntilAsync(() => popup.IsOpen && list.ItemCount == 2);
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
            Assert.Equal(archive.Name, Assert.IsType<FileTag>(list.SelectedItem).Name);
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await UntilAsync(() => !popup.IsOpen && input.Text == "");
            Assert.Contains(archive.Name, (await service.GetFileTagsAsync(path)).Select(t => t.Name));

            input.Text = "Brand new"; Dispatcher.UIThread.RunJobs();
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await UntilAsync(() => input.Text == "");
            Assert.Contains("Brand new", (await service.GetFileTagsAsync(path)).Select(t => t.Name));
            input.Text = "pro"; await UntilAsync(() => popup.IsOpen);
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.False(popup.IsOpen);
            input.Text = "proj"; await UntilAsync(() => popup.IsOpen);
            vm.SelectedEntries.Clear();
            Assert.False(popup.IsOpen); Assert.Equal("", input.Text);

            language.SetLanguage(AppLanguage.English); Dispatcher.UIThread.RunJobs();
            Assert.Contains("Search", input.PlaceholderText);
            Assert.Contains("Return", input.PlaceholderText);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition());
    }
}
