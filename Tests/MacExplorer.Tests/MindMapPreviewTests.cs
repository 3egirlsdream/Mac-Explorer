using System.IO.Compression;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Preview")]
public sealed class MindMapPreviewTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorksheetAndBranchNavigationPreserveSpaceAndEscape(bool darkNarrow)
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_MindMapUi_");
        using var theme = new FastListTestTheme();
        try
        {
            var path = Path.Combine(root.FullName, "Book.XMIND");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("content.json").Open()))
                writer.Write("""
                    [{"title":"Overview","rootTopic":{"title":"Overview root","children":{"attached":[{"title":"Leaf"}]}}},
                     {"title":"Follow-up","rootTopic":{"title":"Second root"}}]
                    """);
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
            var preview = new SuperPreviewView();
            var window = CreateWindow(preview, darkNarrow);
            try
            {
                window.Show();
                await preview.OpenAsync(LocalEntry(path));
                window.UpdateLayout();
                var view = Assert.Single(preview.GetVisualDescendants().OfType<MindMapPreviewView>());
                var picker = view.FindControl<AppComboBox>("SheetPicker")!;
                Assert.True(picker.IsVisible);
                Assert.Equal("Overview", picker.SelectedItem);
                Assert.True(picker.Bounds.Right <= view.Bounds.Width);
                var map = view.FindControl<MindMapCanvas>("Canvas")!;
                Assert.Equal(window.ActualThemeVariant, map.ActualThemeVariant);
                Assert.True(map.Focus());
                Press(Key.Down, PhysicalKey.ArrowDown);
                Assert.Equal("Overview root", AutomationProperties.GetName(map));
                Press(Key.Enter, PhysicalKey.Enter);
                Press(Key.Down, PhysicalKey.ArrowDown);
                Assert.Equal("Overview root", AutomationProperties.GetName(map));

                picker.SelectedIndex = 1;
                Press(Key.Down, PhysicalKey.ArrowDown);
                Assert.Equal("Second root", AutomationProperties.GetName(map));
                picker.SelectedIndex = 0;
                Press(Key.Down, PhysicalKey.ArrowDown);
                Press(Key.Down, PhysicalKey.ArrowDown);
                Assert.Equal("Overview root", AutomationProperties.GetName(map));
                Press(Key.Enter, PhysicalKey.Enter);
                Press(Key.Down, PhysicalKey.ArrowDown);
                Assert.Equal("Leaf", AutomationProperties.GetName(map));

                picker.IsDropDownOpen = true;
                picker.RaiseEvent(new KeyEventArgs { Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent });
                Assert.False(picker.IsDropDownOpen);
                Assert.True(preview.IsVisible);
                Assert.True(map.Focus());
                Press(darkNarrow ? Key.Escape : Key.Space, darkNarrow ? PhysicalKey.Escape : PhysicalKey.Space);
                Assert.False(preview.IsVisible);
                Assert.Empty(preview.GetVisualDescendants().OfType<MindMapPreviewView>());
            }
            finally { window.Close(); }

            void Press(Key key, PhysicalKey physical)
            {
                window.KeyPress(key, RawInputModifiers.None, physical, null);
                window.KeyRelease(key, RawInputModifiers.None, physical, null);
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally { root.Delete(recursive: true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task ArchiveMapAndEdrawImageUseSharedPreviewAndClearOnFastSwitch()
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_MindMapArchive_");
        using var theme = new FastListTestTheme();
        try
        {
            var outer = Path.Combine(root.FullName, "maps.zip");
            using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("nested/map.mm").Open()))
                writer.Write("""<map><node TEXT="Archive map"><node TEXT="Branch"/></node></map>""");

            var emmx = Path.Combine(root.FullName, "map.EMMX");
            var noThumbnail = Path.Combine(root.FullName, "missing.emmx");
            using (var zip = ZipFile.Open(emmx, ZipArchiveMode.Create))
            using (var stream = zip.CreateEntry("thumbnail.png").Open())
                stream.Write(PreviewTestPng);
            using (var zip = ZipFile.Open(noThumbnail, ZipArchiveMode.Create))
            using (var stream = zip.CreateEntry("mergedimage.png").Open())
                stream.Write(PreviewTestPng);

            var source = Path.Combine(root.FullName, "source.mm");
            const string sourceCode = "#include <Foundation/Foundation.h>";
            await File.WriteAllTextAsync(source, sourceCode, TestContext.Current.CancellationToken);
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0, configureServices: services =>
                services.AddSingleton<IArchiveService>(new ArchiveService()));
            var preview = new SuperPreviewView();
            var window = CreateWindow(preview, false);
            try
            {
                window.Show();
                var archived = new FileSystemEntry { FullPath = ArchivePathHelper.Build(outer, "nested/map.mm"), Name = "map.mm" };
                await preview.OpenAsync(archived);
                Assert.Single(preview.GetVisualDescendants().OfType<MindMapPreviewView>());
                await preview.OpenAsync(LocalEntry(emmx));
                Assert.True(preview.HasGalleryImage);
                Assert.Empty(preview.GetVisualDescendants().OfType<MindMapPreviewView>());

                await preview.OpenAsync(LocalEntry(noThumbnail));
                Assert.False(preview.HasGalleryImage);
                Assert.True(preview.FindControl<TextBlock>("PreviewPlaceholder")!.IsVisible);

                var pending = preview.OpenAsync(archived);
                await preview.OpenAsync(LocalEntry(source));
                await pending;
                Assert.True(preview.IsVisible);
                Assert.Empty(preview.GetVisualDescendants().OfType<MindMapPreviewView>());
                Assert.Equal(sourceCode, preview.FindControl<TextBox>("PreviewText")!.Text);
            }
            finally { window.Close(); }
        }
        finally { root.Delete(recursive: true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task InfoPanelPreviewsMapsAtSidebarWidthAndClearsThemOnSelectionChanges()
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_InfoMindMap_");
        using var theme = new FastListTestTheme();
        using var language = new LocalizationService(new StartupSettings(), () => AppLanguage.English);
        using var vm = CreateViewModel(new FakeFileService(root.FullName), archiveService: new ArchiveService());
        var panel = new InfoPanelView { DataContext = vm };
        var window = CreateWindow(panel, true);
        window.Width = 280;
        var token = TestContext.Current.CancellationToken;
        try
        {
            var map = Path.Combine(root.FullName, "book.XMIND");
            using (var zip = ZipFile.Open(map, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("content.json").Open()))
                writer.Write("""
                    [{"title":"A long worksheet name that must fit the sidebar","rootTopic":{"title":"Topic"}},
                     {"title":"Second sheet","rootTopic":{"title":"Second topic"}}]
                    """);
            var archive = Path.Combine(root.FullName, "maps.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(map, "nested/book.xmind");
            var emmx = Path.Combine(root.FullName, "thumb.emmx");
            using (var zip = ZipFile.Open(emmx, ZipArchiveMode.Create))
            using (var stream = zip.CreateEntry("thumbnail.png").Open())
                stream.Write(PreviewTestPng);
            var flowchart = Path.Combine(root.FullName, "flow.pos");
            await File.WriteAllTextAsync(flowchart, """{"meta":{"diagramInfo":{"category":"flow"}}}""", token);
            var source = Path.Combine(root.FullName, "source.mm");
            const string sourceCode = "#include <Foundation/Foundation.h>";
            await File.WriteAllTextAsync(source, sourceCode, token);

            window.Show();
            vm.IsInfoPanelVisible = true;
            await panel.SetLivePreviewStateAsync(true, 1);
            await SelectAsync(LocalEntry(map));
            await WaitForPreviewAsync(() => Map() != null);
            var view = Map()!;
            window.UpdateLayout();
            var canvas = view.FindControl<MindMapCanvas>("Canvas")!;
            var picker = view.FindControl<AppComboBox>("SheetPicker")!;
            var compactHeight = canvas.Bounds.Height;
            Assert.InRange(compactHeight, 90, 220);
            Assert.Equal(ThemeVariant.Dark, canvas.ActualThemeVariant);
            Assert.True(picker.IsVisible && picker.Bounds.Right < canvas.Bounds.Width);
            picker.SelectedIndex = 1;

            panel.RestorePreviewExpanded(true);
            window.UpdateLayout();
            Assert.Same(view, Map());
            Assert.Equal("Second sheet", picker.SelectedItem);
            Assert.True(canvas.Bounds.Height > compactHeight);

            var archived = new FileSystemEntry { FullPath = ArchivePathHelper.Build(archive, "nested/book.xmind"), Name = "book.xmind" };
            await SelectAsync(archived);
            await WaitForPreviewAsync(() => Map() is { } current && current != view);
            await SelectAsync(LocalEntry(map));
            await SelectAsync(LocalEntry(emmx));
            await WaitForPreviewAsync(() => panel.FindControl<Image>("PreviewImage")!.Source != null);
            Assert.Null(Map());

            await SelectAsync(LocalEntry(flowchart));
            const string unavailable = "No mind map preview is available";
            await WaitForPreviewAsync(() => panel.FindControl<TextBlock>("PreviewFeedbackText")!.Text == unavailable);
            Assert.True(panel.FindControl<Image>("PreviewFileIcon")!.IsVisible);

            await SelectAsync(LocalEntry(source));
            await WaitForPreviewAsync(() => panel.FindControl<TextBox>("PreviewText")!.Text == sourceCode);
            Assert.Null(Map());

            await SelectAsync(LocalEntry(map));
            await WaitForPreviewAsync(() => Map() != null);
            await panel.SetLivePreviewStateAsync(false, 2);
            Assert.Null(Map());
        }
        finally
        {
            await panel.SetLivePreviewStateAsync(false, 3);
            window.Close();
            root.Delete(recursive: true);
        }

        MindMapPreviewView? Map() => panel.GetVisualDescendants().OfType<MindMapPreviewView>().SingleOrDefault();

        Task SelectAsync(FileSystemEntry entry)
        {
            vm.SelectedEntries.Clear();
            vm.SelectedEntries.Add(entry);
            return panel.ReloadLivePreviewAsync(1);
        }
    }

    private static FileSystemEntry LocalEntry(string path) => new() { FullPath = path, Name = Path.GetFileName(path) };

    private static Window CreateWindow(Control content, bool darkNarrow)
    {
        var window = new Window
        {
            Width = darkNarrow ? 420 : 800, Height = 520, Content = content,
            RequestedThemeVariant = darkNarrow ? ThemeVariant.Dark : ThemeVariant.Light
        };
        foreach (var asset in new[] { "ThemeTokens", "Styles", "ComponentStyles" })
            window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri($"avares://MacExplorer/Assets/{asset}.axaml")));
        return window;
    }
}
