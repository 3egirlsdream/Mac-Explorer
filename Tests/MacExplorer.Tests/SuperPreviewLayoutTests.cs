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
using LiquidGlassAvaloniaUI;
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
                if (gallery)
                {
                    var canvas = preview.FindControl<GalleryImageCanvas>("GalleryImageCanvas")!;
                    Assert.True(canvas.IsVisible);
                    Assert.NotNull(canvas.Source);
                }
                else
                {
                    Assert.True(preview.FindControl<Image>("PreviewImage")!.IsVisible);
                    Assert.NotNull(preview.FindControl<Image>("PreviewImage")!.Source);
                }
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
                if (compact)
                {
                    Assert.IsType<global::Avalonia.Media.Imaging.Bitmap>(preview.FindControl<Image>("PreviewImage")!.Source);
                    Assert.True(preview.FindControl<Image>("PreviewImage")!.IsVisible);
                }
                else
                {
                    var canvas = preview.FindControl<GalleryImageCanvas>("GalleryImageCanvas")!;
                    Assert.IsType<global::Avalonia.Media.Imaging.Bitmap>(canvas.Source);
                    Assert.True(canvas.IsVisible);
                }
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

    [AvaloniaFact(Explicit = true)]
    public async Task StandaloneImagePreviewOnlyRequestsTheDisplayedImage()
    {
        var root = Directory.CreateTempSubdirectory("FKFinder_StandaloneImagePreview_");
        try
        {
            var path = Path.Combine(root.FullName, "image.png");
            await File.WriteAllBytesAsync(path, PreviewTestPng);
            var entry = new FileSystemEntry { FullPath = path, Name = "image.png", Extension = ".png" };
            var thumbnails = new PreviewThumbnailStub(path);
            var files = new FakeFileService(root.FullName);
            files.Seed(entry);
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
                configureServices: services => services.AddSingleton<IThumbnailService>(thumbnails)
                    .AddSingleton<IFileService>(files));
            var preview = new SuperPreviewView();
            var window = new Window { Width = 800, Height = 600, Content = preview };
            try
            {
                window.Show();
                await preview.OpenAsync(entry);
                RenderIssue11(window);
                Assert.True(preview.FindControl<GalleryImageCanvas>("GalleryImageCanvas")!.IsVisible);
                Assert.True(preview.GalleryShowCheckerboard);
                Assert.False(preview.FindControl<Border>("PreviewListCard")!.IsVisible);
                Assert.Equal(1, thumbnails.CallCount);
                Assert.Equal(1800, thumbnails.PixelSize);
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
            var clipboard = new ImageClipboardService();
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
                configureServices: services => services.AddSingleton<IFileService>(files)
                    .AddSingleton<IClipboardService>(clipboard));
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
            var actionBar = preview.FindControl<PreviewActionBar>("PreviewToolbar")!;
            var glassToolbar = actionBar.BarSurface;
            var previewHost = preview.FindControl<Grid>("PreviewHost")!;
            Assert.True(actionBar.IsVisible);
            Assert.True(PopupGlass.GetIsEnabled(glassToolbar));
            var toolbarMaterial = glassToolbar.GetVisualDescendants().OfType<LiquidGlassSurface>().Single();
            Assert.InRange(toolbarMaterial.SurfaceColor.A, 153, 178);
            Assert.Equal(0.30, toolbarMaterial.BackdropOpacity);
            Assert.Equal(preview.FindControl<Border>("PreviewCard")!.Bounds.Size, previewHost.Bounds.Size);
            var toolbarOffset = actionBar.TranslatePoint(default, previewHost)!.Value;
            Assert.InRange(toolbarOffset.Y + actionBar.Bounds.Height, previewHost.Bounds.Height - 15, previewHost.Bounds.Height - 13);
            Assert.Null(preview.FindControl<Control>("PreviewMetadata"));
            Assert.True(double.IsPositiveInfinity(preview.FindControl<Image>("PreviewImage")!.MaxHeight));
            Assert.True(actionBar.MoreButton.IsVisible);
            Assert.True(actionBar.CopyPathMenuItem.IsVisible);
            actionBar.CopyPathMenuItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(entry.FullPath, clipboard.CopiedText);

            await preview.OpenAsync(new FileSystemEntry { FullPath = root.FullName, Name = root.Name, IsDirectory = true });
            fixture.Window.UpdateLayout();
            Assert.True(preview.FindControl<Border>("PreviewListCard")!.IsVisible);
            Assert.True(preview.FindControl<Border>("ContentDivider")!.IsVisible);
            var grid = preview.FindControl<Grid>("PreviewContentGrid")!;
            var tree = preview.FindControl<FastFileList>("PreviewTree")!;
            var divider = preview.FindControl<Border>("ContentDivider")!;
            var card = preview.FindControl<Border>("PreviewCard")!;
            Assert.Equal(2, tree.Rows.Count);
            Assert.Equal(240, grid.ColumnDefinitions[0].Width.Value);
            Assert.Equal(1, divider.Bounds.Width);
            Assert.Equal(divider.Bounds.Left, preview.FindControl<Border>("PreviewListCard")!.Bounds.Right);
            Assert.Equal(divider.Bounds.Right, card.Bounds.Left);
            var handle = preview.FindControl<Border>("PreviewTreeResizeHandle")!;
            var start = handle.TranslatePoint(new Point(3, 35), fixture.Window)!.Value;
            var end = start + new Vector(120, 0);
            fixture.Window.MouseMove(start);
            fixture.Window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            fixture.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            fixture.Window.MouseUp(end, MouseButton.Left);
            fixture.Window.UpdateLayout();
            Assert.Equal(360, grid.ColumnDefinitions[0].Width.Value);

            preview.Width = 700;
            RenderIssue11(fixture.Window);
            Assert.InRange(grid.ColumnDefinitions[0].ActualWidth, 335, 343);
            Assert.True(card.Bounds.Width >= 320);
            Assert.True(actionBar.MoreButton.IsVisible);
            Assert.All(actionBar.ActionButtons.Children.OfType<Button>().Where(button => button.IsVisible), button =>
            {
                Assert.NotEmpty(AutomationProperties.GetName(button)!);
            });
            preview.Width = double.NaN;
            RenderIssue11(fixture.Window);
            Assert.Equal(360, grid.ColumnDefinitions[0].Width.Value);
            preview.RaiseEvent(new KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Key.Space });
            Assert.False(preview.IsVisible);
            Assert.True(fileList.IsKeyboardFocusWithin);
            Assert.Empty(tree.Rows);
            Assert.Equal("", preview.FindControl<TextBox>("PreviewText")!.Text);
            await preview.OpenAsync(new FileSystemEntry { FullPath = root.FullName, Name = root.Name, IsDirectory = true });
            RenderIssue11(fixture.Window);
            Assert.Equal(360, grid.ColumnDefinitions[0].Width.Value);
            preview.Close();
        }
        finally { root.Delete(true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task DirectoryTreeLoadsOnlyTheSelectedPreviewAndRetriesOnDemand()
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("FKFinder_PreviewTree_");
        try
        {
            var branchPath = Directory.CreateDirectory(Path.Combine(root.FullName, "2026")).FullName;
            var emptyPath = Directory.CreateDirectory(Path.Combine(branchPath, "empty")).FullName;
            var folder = new FileSystemEntry { FullPath = root.FullName, Name = root.Name, IsDirectory = true };
            var branch = new FileSystemEntry { FullPath = branchPath, Name = "2026", IsDirectory = true };
            var files = new FakeFileService(root.FullName);
            files.Seed(branch);
            files.Seed(new FileSystemEntry { FullPath = emptyPath, Name = "empty", IsDirectory = true });
            var imagePath = Path.Combine(branchPath, "image.png");
            await File.WriteAllBytesAsync(imagePath, PreviewTestPng);
            files.Seed(new FileSystemEntry { FullPath = imagePath, Name = "image.png", Extension = ".png" });
            for (var i = 1; i <= 40; i++)
            {
                var name = $"unused-{i:D2}.png";
                files.Seed(new FileSystemEntry { FullPath = Path.Combine(root.FullName, name), Name = name, Extension = ".png" });
            }
            var fail = true;
            files.BeforeEnumerate = (path, _) => path == branchPath && fail
                ? Task.FromException(new UnauthorizedAccessException("fixture denied")) : Task.CompletedTask;
            var thumbnails = new PreviewThumbnailStub(imagePath);
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0, configureServices: services =>
                services.AddSingleton<IFileService>(files).AddSingleton<IThumbnailService>(thumbnails));
            var preview = fixture.Window.FindControl<SuperPreviewView>("SuperPreviewControl")!;
            await preview.OpenAsync(folder);
            RenderIssue11(fixture.Window);
            var tree = preview.FindControl<FastFileList>("PreviewTree")!;
            Assert.Equal(branchPath, tree.SelectedPath);
            Assert.Equal(42, tree.Rows.Count);
            Assert.Equal(0, thumbnails.CallCount);
            tree.ScrollToEntry(tree.Rows[^1]);
            RenderIssue11(fixture.Window);
            Assert.Equal(0, thumbnails.CallCount);
            tree.ScrollToEntry(branch);

            Press(Key.Right);
            await WaitForPreviewAsync(() => tree.TreeRowAt(1)?.HasError == true);
            Assert.Contains("fixture denied", preview.FindControl<TextBlock>("PreviewPlaceholder")!.Text!);
            fail = false;

            var disclosure = new Point(37, tree.RowBounds(1).Center.Y);
            Assert.Equal(1, tree.TreeDisclosureIndexAt(disclosure));
            var clickPoint = tree.TranslatePoint(disclosure, fixture.Window)!.Value;
            ClickDisclosure();
            Assert.True(tree.TreeRowAt(1)?.IsExpanded == true);
            ClickDisclosure();
            Assert.False(tree.TreeRowAt(1)?.IsExpanded == true);

            Press(Key.Right);
            await WaitForPreviewAsync(() => tree.Rows.Count == 44);
            Assert.Equal(2, tree.TreeRowAt(2)?.Depth);
            Assert.Equal(0, thumbnails.CallCount);

            Press(Key.Right);
            await WaitForPreviewAsync(() => tree.SelectedPath == emptyPath);
            Press(Key.Right);
            await WaitForPreviewAsync(() => tree.TreeRowAt(2) is { IsExpanded: true, IsLoading: false });
            Assert.True(preview.FindControl<StackPanel>("FolderSummary")!.IsVisible);
            Assert.Contains("0", preview.FindControl<TextBlock>("FolderSummaryDetails")!.Text!);
            Press(Key.Down);
            await WaitForPreviewAsync(() => preview.FindControl<GalleryImageCanvas>("GalleryImageCanvas")!.IsVisible);
            Assert.Equal(imagePath, tree.SelectedPath);
            Assert.Equal(1, thumbnails.CallCount);

            var bar = preview.FindControl<PreviewActionBar>("PreviewToolbar")!;
            Assert.True(bar.ZoomButton.IsVisible);
            bar.ZoomButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(preview.FindControl<ScrollViewer>("ZoomedImageScroll")!.IsVisible);
            Assert.Equal(1, thumbnails.CallCount);

            Press(Key.Left);
            Assert.Equal(branchPath, tree.SelectedPath);
            Press(Key.Left);
            Assert.Equal(42, tree.Rows.Count);
            Assert.Equal(1, thumbnails.CallCount);
            preview.Close();

            void Press(Key key) => tree.RaiseEvent(new KeyEventArgs { Key = key, RoutedEvent = InputElement.KeyDownEvent });
            void ClickDisclosure()
            {
                fixture.Window.MouseDown(clickPoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);
                fixture.Window.MouseUp(clickPoint, MouseButton.Left);
            }
        }
        finally { root.Delete(true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task PreviewSideButtonsFollowVisibleTreeFilesOnHover()
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("FKFinder_PreviewStep_");
        try
        {
            var files = new FakeFileService(root.FullName);
            var first = new FileSystemEntry { FullPath = Path.Combine(root.FullName, "a.txt"), Name = "a.txt" };
            var last = new FileSystemEntry { FullPath = Path.Combine(root.FullName, "b.txt"), Name = "b.txt" };
            var folder = new FileSystemEntry { FullPath = root.FullName, Name = root.Name, IsDirectory = true };
            File.WriteAllText(first.FullPath, "first");
            File.WriteAllText(last.FullPath, "last");
            files.Seed(first);
            files.Seed(last);
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
                configureServices: services => services.AddSingleton<IFileService>(files));
            var preview = fixture.Window.FindControl<SuperPreviewView>("SuperPreviewControl")!;
            var previous = preview.FindControl<Button>("PreviewPreviousButton")!;
            var next = preview.FindControl<Button>("PreviewNextButton")!;
            var tree = preview.FindControl<FastFileList>("PreviewTree")!;

            await preview.OpenAsync(folder);
            RenderIssue11(fixture.Window);
            Assert.Equal(first.FullPath, tree.SelectedPath);
            Assert.False(previous.IsEffectivelyVisible);
            var glass = Assert.IsType<Border>(next.Content);
            Assert.Equal(0, glass.Opacity);
            Assert.True(PopupGlass.GetIsFloating(glass));

            HoverAndClick(next);
            await WaitForPreviewAsync(() => tree.SelectedPath == last.FullPath);
            Assert.False(next.IsEffectivelyVisible);
            HoverAndClick(previous);
            await WaitForPreviewAsync(() => tree.SelectedPath == first.FullPath);
            preview.Close();

            await preview.OpenAsync(first, [last, folder, first]);
            RenderIssue11(fixture.Window);
            Assert.False(tree.IsEffectivelyVisible);
            Assert.False(next.IsEffectivelyVisible);
            HoverAndClick(previous);
            await WaitForPreviewAsync(() => preview.FindControl<TextBlock>("TitleText")!.Text == last.Name);
            Assert.False(previous.IsEffectivelyVisible);
            Assert.True(next.IsEffectivelyVisible);
            preview.Close();

            void HoverAndClick(Button button)
            {
                RenderIssue11(fixture.Window);
                var position = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), fixture.Window)!.Value;
                fixture.Window.MouseMove(position);
                Assert.Equal(1, Assert.IsType<Border>(button.Content).Opacity);
                Assert.Equal(Avalonia.Media.Colors.Transparent, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(button.Background).Color);
                fixture.Window.MouseDown(position, MouseButton.Left, RawInputModifiers.LeftMouseButton);
                fixture.Window.MouseUp(position, MouseButton.Left);
                fixture.Window.MouseMove(new Point(2, 2));
            }
        }
        finally { root.Delete(true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task NestedArchiveIsExtractedOnlyOnExpansionAndCleanedOnCollapse()
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("FKFinder_NestedPreview_");
        try
        {
            var text = Path.Combine(root.FullName, "note.txt");
            var nested = Path.Combine(root.FullName, "nested.zip");
            var outer = Path.Combine(root.FullName, "outer.zip");
            await File.WriteAllTextAsync(text, "nested text");
            using (var archive = ZipFile.Open(nested, ZipArchiveMode.Create)) archive.CreateEntryFromFile(text, "note.txt");
            using (var archive = ZipFile.Open(outer, ZipArchiveMode.Create)) archive.CreateEntryFromFile(nested, "nested.zip");
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0, configureServices: services =>
                services.AddSingleton<IArchiveService>(new ArchiveService()));
            var preview = fixture.Window.FindControl<SuperPreviewView>("SuperPreviewControl")!;
            await preview.OpenAsync(new FileSystemEntry { FullPath = outer, Name = "outer.zip" });
            var tree = preview.FindControl<FastFileList>("PreviewTree")!;
            Assert.Equal(2, tree.Rows.Count);
            Assert.False(tree.TreeRowAt(1)!.Value.IsExpanded);

            Press(Key.Right);
            await WaitForPreviewAsync(() => tree.Rows.Count == 3);
            var temporary = ArchivePathHelper.Parse(tree.Rows[2].FullPath).archivePath;
            Assert.True(File.Exists(temporary));
            Press(Key.Right);
            await WaitForPreviewAsync(() => preview.FindControl<TextBox>("PreviewText")!.Text == "nested text");
            Press(Key.Left);
            Press(Key.Left);
            Assert.Equal(2, tree.Rows.Count);
            Assert.False(File.Exists(temporary));
            preview.Close();

            void Press(Key key) => tree.RaiseEvent(new KeyEventArgs { Key = key, RoutedEvent = InputElement.KeyDownEvent });
        }
        finally { root.Delete(true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task EncryptedArchiveUsesThePasswordForTheSelectedMember()
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("FKFinder_EncryptedPreview_");
        try
        {
            const string password = "preview-fixture";
            var path = Path.Combine(root.FullName, "encrypted.zip");
            using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipOutputStream(File.Create(path)) { Password = password })
            {
                zip.PutNextEntry(new ICSharpCode.SharpZipLib.Zip.ZipEntry("docs/note.txt") { AESKeySize = 256 });
                zip.Write("encrypted note"u8.ToArray());
            }
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0, configureServices: services =>
                services.AddSingleton<IArchiveService>(new ArchiveService()));
            var preview = fixture.Window.FindControl<SuperPreviewView>("SuperPreviewControl")!;
            var prompts = 0;
            preview.PasswordPrompt = Prompt;
            var folder = new FileSystemEntry
                { FullPath = ArchivePathHelper.Build(path, "docs/"), Name = "docs", IsDirectory = true };
            await preview.OpenAsync(folder);
            var tree = preview.FindControl<FastFileList>("PreviewTree")!;
            Assert.Equal(2, tree.Rows.Count);
            Assert.Equal(folder.FullPath, tree.SelectedPath);
            preview.Close();
            preview.PasswordPrompt = Prompt;
            prompts = 0;
            var entry = new FileSystemEntry { FullPath = ArchivePathHelper.Build(path, "docs/note.txt"), Name = "note.txt" };
            await preview.OpenAsync(entry);
            Assert.Equal(1, prompts);
            Assert.Equal(entry.FullPath, tree.SelectedPath);
            Assert.Equal("encrypted note", preview.FindControl<TextBox>("PreviewText")!.Text);
            preview.Close();

            Task<string?> Prompt()
            {
                prompts++;
                return Task.FromResult<string?>(password);
            }
        }
        finally { root.Delete(true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task SwitchingTreeSelectionCancelsThePreviousPreview()
    {
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("FKFinder_PreviewSelection_");
        try
        {
            var folder = new FileSystemEntry { FullPath = root.FullName, Name = root.Name, IsDirectory = true };
            var image = new FileSystemEntry { FullPath = Path.Combine(root.FullName, "a.png"), Name = "a.png" };
            var text = new FileSystemEntry { FullPath = Path.Combine(root.FullName, "b.txt"), Name = "b.txt" };
            await File.WriteAllBytesAsync(image.FullPath, PreviewTestPng);
            await File.WriteAllTextAsync(text.FullPath, "latest selection");
            var files = new FakeFileService(root.FullName);
            files.Seed(image);
            files.Seed(text);
            var thumbnails = new PreviewThumbnailStub(null);
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0, configureServices: services =>
                services.AddSingleton<IFileService>(files).AddSingleton<IThumbnailService>(thumbnails));
            var preview = fixture.Window.FindControl<SuperPreviewView>("SuperPreviewControl")!;
            var opening = preview.OpenAsync(folder);
            await WaitForPreviewAsync(() => thumbnails.CallCount == 1);
            preview.FindControl<FastFileList>("PreviewTree")!.RaiseEvent(new KeyEventArgs
                { Key = Key.Down, RoutedEvent = InputElement.KeyDownEvent });
            await WaitForPreviewAsync(() => thumbnails.WasCancelled && preview.FindControl<TextBox>("PreviewText")!.Text == "latest selection");
            await opening;
            Assert.False(preview.FindControl<Image>("PreviewImage")!.IsVisible);
            preview.Close();
        }
        finally { root.Delete(true); }
    }

}
