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

[Trait("Kind", "UI")]
[Trait("Area", "Preview")]
public sealed class SuperPreviewLayoutViewModelTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactAndGalleryVideosContinueUsingStaticThumbnails(bool gallery)
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_StaticVideoPreview_");
        try
        {
            var entry = new FileSystemEntry { FullPath = Path.Combine(root.FullName, "video.MP4"), Name = "video.MP4", Extension = ".MP4" };
            await File.WriteAllTextAsync(entry.FullPath, "fixture");
            var thumbnails = new PreviewThumbnailStub(entry.FullPath);
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
                configureServices: services => services.AddSingleton<IThumbnailService>(thumbnails));
            var preview = new SuperPreviewView(isCompactPreview: true, isGalleryPreview: gallery);
            var window = new Window { Width = 800, Height = 600, Content = preview };
            try
            {
                window.Show();
                if (gallery) Assert.True(await preview.OpenGalleryFileAsync(entry, TestContext.Current.CancellationToken));
                else await preview.OpenAsync(entry);
                Assert.Equal(1, thumbnails.CallCount);
                Assert.True(preview.FindControl<Image>("PreviewImage")!.IsVisible);
                Assert.NotNull(preview.FindControl<Image>("PreviewImage")!.Source);
                Assert.Empty(preview.FindControl<Grid>("PreviewHost")!.Children.OfType<MacExplorer.Controls.VideoPreviewHost>());
            }
            finally { preview.Close(); window.Close(); }
        }
        finally { root.Delete(true); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SvgPreviewUsesSharedThumbnailService(bool compact)
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_SvgPreview_");
        using var theme = new FastListTestTheme();
        try
        {
            var entry = new FileSystemEntry
            {
                FullPath = Path.Combine(root.FullName, "image.svg"), Name = "image.svg",
                Extension = ".svg", IconKey = "file-image"
            };
            await File.WriteAllTextAsync(entry.FullPath, "<svg xmlns='http://www.w3.org/2000/svg' width='106' height='74'><rect width='106' height='74' fill='red'/></svg>");
            var thumbnails = new MacExplorer.Platforms.MacCatalyst.Services.MacThumbnailService(
                Path.Combine(root.FullName, "cache"), 1024 * 1024, 0.8);
            var files = new FakeFileService(root.FullName);
            files.Seed(entry);
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
                configureServices: services => services.AddSingleton<IThumbnailService>(thumbnails)
                    .AddSingleton<IFileService>(files));
            var preview = new SuperPreviewView(isCompactPreview: compact);
            var window = new Window { Width = 800, Height = 600, Content = preview };
            try
            {
                window.Show();
                await preview.OpenAsync(entry);
                Assert.IsType<global::Avalonia.Media.Imaging.Bitmap>(preview.FindControl<Image>("PreviewImage")!.Source);
                Assert.True(preview.FindControl<Image>("PreviewImage")!.IsVisible);
                // Headless drawing substitutes a dummy Avalonia bitmap; inspect the service's actual PNG.
                var cached = Assert.Single(Directory.GetFiles(Path.Combine(root.FullName, "cache"), "*.png"));
                using var image = SkiaSharp.SKBitmap.Decode(cached);
                Assert.Equal(compact ? 720 : 1800, image.Width);
                Assert.InRange(image.Height, image.Width * 74 / 106 - 1, image.Width * 74 / 106 + 1);
            }
            finally { preview.Close(); window.Close(); }
        }
        finally { root.Delete(true); }
    }

    [AvaloniaTheory(Explicit = true)]
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
