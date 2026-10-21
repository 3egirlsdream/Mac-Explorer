using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "InfoPanelTag")]
public sealed class InfoPanelTagInteractionTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataRefreshDuringTagClickPreservesThePressedButton(bool dark)
    {
        var root = Directory.CreateTempSubdirectory("info-tag-click-").FullName;
        using var service = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root, "tags.db")));
        var tag = await service.CreateTagAsync("Project");
        var path = Path.Combine(root, "file.bin");
        await service.SetTagAsync([path], tag, true);
        using var vm = CreateViewModel(new FakeFileService(root), fileTagService: service);
        vm.IsInfoPanelVisible = true;
        vm.SelectedEntries.Add(new FileSystemEntry { FullPath = path, Name = "file.bin" });
        var panel = new InfoPanelView(service) { DataContext = vm };
        var window = new Window { Width = 374, Height = 1000, Content = panel,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var chips = panel.FindControl<WrapPanel>("CustomTagsPanel")!;
            var remove = chips.GetVisualDescendants().OfType<Button>().Single();
            var point = remove.TranslatePoint(new Point(remove.Bounds.Width / 2, remove.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);

            vm.CurrentMetadata = new FileMetadata { FullPath = path, Tags = [tag.Name] };
            Dispatcher.UIThread.RunJobs();
            Assert.Same(remove, chips.GetVisualDescendants().OfType<Button>().Single());

            window.MouseUp(point, MouseButton.Left);
            Assert.Empty(await service.GetFileTagsAsync(path));
            Assert.Empty(chips.Children);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }
}
