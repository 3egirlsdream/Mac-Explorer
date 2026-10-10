using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
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
    public async Task StandalonePreviewUsesFullWidthAndDirectoryPreviewRetainsNavigation(bool dark)
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_PreviewLayout_");
        using var theme = new FastListTestTheme();
        try
        {
            var entry = new FileSystemEntry
            {
                FullPath = Path.Combine(root.FullName, "long-document-name.txt"),
                Name = "long-document-name.txt", Extension = ".txt"
            };
            await File.WriteAllTextAsync(entry.FullPath, "document content");
            var files = new FakeFileService(root.FullName);
            files.Seed(entry);
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
                configureServices: services => services.AddSingleton<IFileService>(files));
            foreach (var asset in new[] { "ThemeTokens", "Styles", "ComponentStyles" })
                fixture.Window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri($"avares://MacExplorer/Assets/{asset}.axaml")));
            fixture.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            fixture.Window.Width = 1100;
            var preview = fixture.Window.FindControl<SuperPreviewView>("SuperPreviewControl")!;
            var fileList = fixture.Workspaces[fixture.Model.SelectedTab!].FileListView.FindControl<MacExplorer.Controls.FastFileList>("FastList")!;
            fileList.Focus();
            await preview.OpenAsync(entry);
            fixture.Window.UpdateLayout();
            Assert.Equal("document content", preview.FindControl<TextBox>("PreviewText")!.Text);
            Assert.False(preview.FindControl<Border>("PreviewListCard")!.IsVisible);
            Assert.False(preview.FindControl<Border>("ContentDivider")!.IsVisible);
            Assert.Equal(entry.Name, preview.FindControl<TextBlock>("TitleText")!.Text);
            Assert.False(preview.FindControl<TextBlock>("BreadcrumbText")!.IsVisible);
            Assert.Equal(0, preview.FindControl<Grid>("PreviewContentGrid")!.ColumnDefinitions[0].Width.Value);
            foreach (var name in new[] { "PreviewFooter", "PreviewMetadata", "PreviewListHeader", "QuickLookButton" })
                Assert.Null(preview.FindControl<Control>(name));
            Assert.True(double.IsPositiveInfinity(preview.FindControl<Image>("PreviewImage")!.MaxHeight));

            await preview.OpenAsync(new FileSystemEntry { FullPath = root.FullName, Name = root.Name, IsDirectory = true });
            fixture.Window.UpdateLayout();
            Assert.True(preview.FindControl<Border>("PreviewListCard")!.IsVisible);
            Assert.True(preview.FindControl<Border>("ContentDivider")!.IsVisible);
            Assert.Equal(240, preview.FindControl<Grid>("PreviewContentGrid")!.ColumnDefinitions[0].Width.Value);
            preview.Width = 700;
            fixture.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            fixture.Window.UpdateLayout();
            Assert.Equal(200, preview.FindControl<Grid>("PreviewContentGrid")!.ColumnDefinitions[0].Width.Value);
            preview.RaiseEvent(new KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Key.Space });
            Assert.False(preview.IsVisible);
            Assert.True(fileList.IsKeyboardFocusWithin);
            Assert.Empty(preview.FindControl<ListBox>("ItemsList")!.Items);
            Assert.Equal("", preview.FindControl<TextBox>("PreviewText")!.Text);
        }
        finally { root.Delete(true); }
    }
}
