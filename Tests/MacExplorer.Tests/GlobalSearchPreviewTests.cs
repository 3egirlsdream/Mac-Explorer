using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AppIcons = MacExplorer.Assets.Icons;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalSearchFixedPreviewTracksSelectionKeepsInputFocusAndReleasesOnClose(bool dark)
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_SearchPreview_");
        using var theme = new FastListTestTheme();
        try
        {
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
                configureServices: services => services.AddSingleton<IFileService>(new FakeFileService(root.FullName)));
            foreach (var asset in new[] { "ThemeTokens", "Styles", "ComponentStyles" })
                fixture.Window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri($"avares://MacExplorer/Assets/{asset}.axaml")));
            fixture.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            fixture.Window.Width = 1000;
            var file = new FileSystemEntry
            {
                FullPath = Path.Combine(root.FullName, "a.txt"), Name = "a.txt", Extension = ".txt"
            };
            await File.WriteAllTextAsync(file.FullPath, "selected preview content");
            var (input, results, row) = ShowGlobalSearchResult(fixture.Window,
                new(OmniboxSuggestionKind.Result, file.Name, file.FullPath, file.FullPath, AppIcons.File, "#777777", file));
            var originalPath = fixture.Model.FileList.CurrentPath;
            var host = fixture.Window.FindControl<ContentControl>("GlobalSearchPreviewHost")!;
            var preview = Assert.IsType<SuperPreviewView>(host.Content);
            var text = preview.FindControl<TextBox>("PreviewText")!;
            await WaitForPreviewAsync(() => text.Text == "selected preview content");
            Assert.True(input.IsKeyboardFocusWithin);
            Assert.Equal(originalPath, fixture.Model.FileList.CurrentPath);
            Assert.Empty(fixture.Model.FileList.SelectedEntries);
            Assert.False(preview.FindControl<Border>("PreviewListCard")!.IsVisible);
            Assert.False(preview.FindControl<Grid>("PreviewHeader")!.IsVisible);
            Assert.Null(preview.FindControl<Grid>("PreviewMetadata"));
            var panel = fixture.Window.FindControl<Border>("GlobalSearchPreviewPanel")!;
            Assert.Equal(220, panel.Bounds.Width);
            Assert.Equal(860, fixture.Window.FindControl<Border>("GlobalSearchPanel")!.Width);
            Assert.True(panel.TranslatePoint(default, fixture.Window)!.Value.X
                > results.TranslatePoint(default, fixture.Window)!.Value.X + results.Bounds.Width);
            Assert.Null(fixture.Window.FindControl<ComboBox>("GlobalSearchPreviewSizeCombo"));

            var second = new FileSystemEntry
            {
                FullPath = Path.Combine(root.FullName, "b.txt"), Name = "b.txt", Extension = ".txt"
            };
            await File.WriteAllTextAsync(second.FullPath, "second content");
            Assert.IsAssignableFrom<ICollection<OmniboxSuggestion>>(results.ItemsSource).Add(
                new(OmniboxSuggestionKind.Result, second.Name, second.FullPath, second.FullPath, AppIcons.File, "#777777", second));
            fixture.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            await WaitForPreviewAsync(() => text.Text == "second content");
            Assert.Same(preview, host.Content);
            Assert.True(input.IsKeyboardFocusWithin);
            Assert.Equal(originalPath, fixture.Model.FileList.CurrentPath);

            fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(preview.IsVisible);
            Assert.True(string.IsNullOrEmpty(text.Text));
        }
        finally { root.Delete(true); }
    }

    [AvaloniaFact]
    public async Task GlobalSearchLocateSelectsFileAndEnterStillLaunchesIt()
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_SearchLocate_");
        using var theme = new FastListTestTheme();
        var launcher = new BundleTestLauncher();
        try
        {
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0, launcherService: launcher);
            var file = new FileSystemEntry
            {
                FullPath = Path.Combine(root.FullName, "target.txt"), Name = "target.txt", Extension = ".txt"
            };
            await File.WriteAllTextAsync(file.FullPath, "test");
            fixture.Files.Seed(file);
            var suggestion = new OmniboxSuggestion(OmniboxSuggestionKind.Result,
                file.Name, file.FullPath, file.FullPath, AppIcons.File, "#777777", file);
            var (_, _, row) = ShowGlobalSearchResult(fixture.Window, suggestion);
            var locate = Assert.Single(row.GetVisualDescendants().OfType<Button>());
            locate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForPreviewAsync(() => fixture.Model.FileList.SelectedEntries.Count == 1);
            Assert.Equal(root.FullName, fixture.Model.FileList.CurrentPath);
            Assert.Equal(file.FullPath, fixture.Model.FileList.SelectedEntries[0].FullPath);
            Assert.Empty(launcher.Opened);

            ShowGlobalSearchResult(fixture.Window, suggestion);
            fixture.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.Equal(file.FullPath, Assert.Single(launcher.Opened));
            Assert.Equal(root.FullName, fixture.Model.FileList.CurrentPath);
            Assert.False(fixture.Window.FindControl<Border>("GlobalSearchOverlay")!.IsVisible);
        }
        finally { root.Delete(true); }
    }

    [AvaloniaFact]
    public async Task GlobalSearchFixedFolderUsesSharedContentListAndCommandsClearPreview()
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_SearchFolder_");
        using var theme = new FastListTestTheme();
        try
        {
            var files = new FakeFileService(root.FullName);
            files.Seed(new FileSystemEntry { FullPath = Path.Combine(root.FullName, "child.txt"), Name = "child.txt" });
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
                configureServices: services => services.AddSingleton<IFileService>(files));
            var (_, _, row) = ShowGlobalSearchResult(fixture.Window,
                new(OmniboxSuggestionKind.Path, root.Name, root.FullName, root.FullName, AppIcons.File, "#777777"));
            var preview = Assert.IsType<SuperPreviewView>(fixture.Window.FindControl<ContentControl>("GlobalSearchPreviewHost")!.Content);
            await WaitForPreviewAsync(() => preview.FindControl<ListBox>("ItemsList")!.ItemCount == 1);
            Assert.Equal("child.txt", Assert.IsType<FileSystemEntry>(Assert.Single(preview.FindControl<ListBox>("ItemsList")!.Items)).Name);
            Assert.True(preview.FindControl<Border>("PreviewListCard")!.IsVisible);
            Assert.False(preview.FindControl<Border>("PreviewCard")!.IsVisible);

            fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            var (_, _, commandRow) = ShowGlobalSearchResult(fixture.Window,
                new(OmniboxSuggestionKind.Command, "command", "", "command", AppIcons.File, "#777777"));
            Assert.False(Assert.Single(commandRow.GetVisualDescendants().OfType<Button>()).IsVisible);
            Assert.False(preview.IsVisible);
            Assert.Empty(preview.FindControl<ListBox>("ItemsList")!.Items);
        }
        finally { root.Delete(true); }
    }

    private static (TextBox Input, ListBox Results, Grid Row) ShowGlobalSearchResult(
        MainWindow window, OmniboxSuggestion suggestion)
    {
        window.KeyPress(Key.K, RawInputModifiers.Meta, PhysicalKey.K, null);
        Dispatcher.UIThread.RunJobs();
        var input = window.FindControl<TextBox>("GlobalSearchBox")!;
        var results = window.FindControl<ListBox>("GlobalSearchResults")!;
        var suggestions = Assert.IsAssignableFrom<ICollection<OmniboxSuggestion>>(results.ItemsSource);
        suggestions.Add(suggestion);
        results.IsVisible = true;
        results.SelectedIndex = 0;
        window.FindControl<TextBlock>("GlobalSearchEmptyHint")!.IsVisible = false;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var row = Assert.Single(results.GetVisualDescendants().OfType<Grid>(),
            control => control.Classes.Contains("global-search-result"));
        return (input, results, row);
    }
}
