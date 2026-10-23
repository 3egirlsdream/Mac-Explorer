using System.Diagnostics;
using System.Xml.Linq;
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
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "InfoPanelTag")]
public sealed class InfoPanelTagViewModelTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InfoPanelRemovesCustomTagsFromFinderAndKeepsOtherTags(bool dark, bool lastTag)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS.");

        var directory = Path.Combine(Path.GetTempPath(), "fkfinder-info-tags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "标签测试.srt");
        await File.WriteAllTextAsync(path, "subtitle", cancellationToken);
        var originalTags = lastTag ? new[] { "待删除" } : new[] { "红色", "待删除", "保留" };
        await SeedFinderTagsAsync(path, originalTags, cancellationToken);

        using var vm = CreateViewModel(new FakeFileService(directory));
        vm.SelectedEntries.Add(new FileSystemEntry
        {
            FullPath = path, Name = Path.GetFileName(path), Extension = ".srt"
        });
        vm.IsInfoPanelVisible = true;
        vm.CurrentMetadata = new FileMetadata { FullPath = path, Tags = originalTags.ToList() };
        using var tags = new MacExplorer.Services.Impl.FileTagService(
            new MacExplorer.Services.Impl.DatabaseConnectionFactory(Path.Combine(directory, "tags.db")), store: new MacFileTagStore());
        Assert.Equal(originalTags.Order(), (await tags.GetFileTagsAsync(path, cancellationToken)).Select(tag => tag.Name).Order());
        var panel = new InfoPanelView(tags);
        await panel.SetLivePreviewStateAsync(false, 0);
        panel.DataContext = vm;
        var window = new Window
        {
            Width = 374, Height = 940, Content = panel,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var chips = panel.FindControl<WrapPanel>("CustomTagsPanel")!;
            var remove = await WaitForRemoveButtonAsync("待删除");
            var point = remove.TranslatePoint(new Point(remove.Bounds.Width / 2, remove.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            var expectedTags = lastTag ? Array.Empty<string>() : ["红色", "保留"];
            await ReloadPersistedTagsAsync(expectedTags);
            Assert.Equal(lastTag ? [] : new[] { "保留" }, DisplayedCustomTags());

            if (!lastTag)
            {
                // Removing the remaining custom tag with the keyboard must retain the color tag.
                (await WaitForRemoveButtonAsync("保留")).Focus(NavigationMethod.Tab);
                window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
                window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
                await ReloadPersistedTagsAsync(["红色"]);
                Assert.Empty(chips.Children);

                var red = panel.FindControl<StackPanel>("SystemTagsPanel")!.Children
                    .OfType<Button>().Single(button => Equals(button.Tag, "红色"));
                var redPoint = red.TranslatePoint(new Point(12, 12), window)!.Value;
                window.MouseMove(redPoint);
                window.MouseDown(redPoint, MouseButton.Left);
                window.MouseUp(redPoint, MouseButton.Left);
                await ReloadPersistedTagsAsync([]);
                Assert.Empty(chips.Children);
            }

            async Task<Button> WaitForRemoveButtonAsync(string tag)
            {
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(30))
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var chip = chips.Children.OfType<Border>().SingleOrDefault(candidate =>
                        candidate.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == tag));
                    var button = chip?.GetVisualDescendants().OfType<Button>().SingleOrDefault();
                    if (button is { Bounds.Width: > 0, Bounds.Height: > 0 }) return button;
                    await Task.Delay(25, cancellationToken);
                }
                throw new TimeoutException($"Tag remove button '{tag}' did not become ready. Displayed tags: {string.Join(", ", DisplayedCustomTags())}");
            }

            string[] DisplayedCustomTags() => chips.Children.OfType<Border>()
                .Select(chip => ((StackPanel)chip.Child!).Children.OfType<TextBlock>().First().Text!)
                .ToArray();

            async Task ReloadPersistedTagsAsync(string[] expected)
            {
                var metadataService = new MacMetadataService();
                var deadline = DateTime.UtcNow.AddSeconds(8);
                FileMetadata metadata;
                do
                {
                    await Task.Delay(50, cancellationToken);
                    Dispatcher.UIThread.RunJobs();
                    metadata = await metadataService.GetMetadataAsync(path);
                    if (metadata.Tags.Order().SequenceEqual(expected.Order())) break;
                } while (DateTime.UtcNow < deadline);

                Assert.Equal(expected.Order(), metadata.Tags.Order());
                vm.CurrentMetadata = metadata;
                Dispatcher.UIThread.RunJobs();
                var expectedCustom = expected.Where(tag => tag != "红色").ToArray();
                for (var attempt = 0; attempt < 200 && !DisplayedCustomTags().SequenceEqual(expectedCustom); attempt++)
                {
                    await Task.Delay(25, cancellationToken);
                    Dispatcher.UIThread.RunJobs();
                }
            }
        }
        finally
        {
            await panel.CancelAndReleaseLivePreviewAsync();
            window.Close();
            Directory.Delete(directory, true);
        }
    }

}

public abstract partial class FileListViewModelTestBase
{
    protected static async Task SeedFinderTagsAsync(string path, string[] tags, CancellationToken cancellationToken)
    {
        var plist = new XDocument(new XElement("plist", new XAttribute("version", "1.0"),
            new XElement("array", tags.Select(tag => new XElement("string", tag)))));
        var start = new ProcessStartInfo("/usr/bin/xattr") { UseShellExecute = false };
        foreach (var argument in new[] { "-w", "com.apple.metadata:_kMDItemUserTags", plist.ToString(), path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync(cancellationToken);
        Assert.Equal(0, process.ExitCode);
    }

}
