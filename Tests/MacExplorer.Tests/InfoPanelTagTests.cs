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

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InfoPanelRemovesCustomTagsFromFinderAndKeepsOtherTags(bool dark, bool lastTag)
    {
        if (!OperatingSystem.IsMacOS()) return;

        var directory = Path.Combine(Path.GetTempPath(), "fkfinder-info-tags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "标签测试.srt");
        await File.WriteAllTextAsync(path, "subtitle", TestContext.Current.CancellationToken);
        var originalTags = lastTag ? new[] { "待删除" } : new[] { "红色", "待删除", "保留" };
        await SeedFinderTagsAsync(path, originalTags);

        using var vm = CreateViewModel(new FakeFileService(directory));
        vm.SelectedEntries.Add(new FileSystemEntry
        {
            FullPath = path, Name = Path.GetFileName(path), Extension = ".srt"
        });
        vm.IsInfoPanelVisible = true;
        vm.CurrentMetadata = new FileMetadata { FullPath = path, Tags = originalTags.ToList() };
        using var tags = new MacExplorer.Services.Impl.FileTagService(
            new MacExplorer.Services.Impl.DatabaseConnectionFactory(Path.Combine(directory, "tags.db")), store: new MacFileTagStore());
        var panel = new InfoPanelView(tags) { DataContext = vm };
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
            for (var attempt = 0; attempt < 50 && chips.Children.Count == 0; attempt++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
            var remove = FindRemoveButton("待删除");
            var point = remove.TranslatePoint(new Point(remove.Bounds.Width / 2, remove.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.DoesNotContain("待删除", DisplayedCustomTags());

            var expectedTags = lastTag ? Array.Empty<string>() : ["红色", "保留"];
            await ReloadPersistedTagsAsync(expectedTags);
            Assert.Equal(lastTag ? [] : new[] { "保留" }, DisplayedCustomTags());

            if (!lastTag)
            {
                // Removing the remaining custom tag with the keyboard must retain the color tag.
                FindRemoveButton("保留").Focus(NavigationMethod.Tab);
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

            Button FindRemoveButton(string tag) => chips.Children.OfType<Border>()
                .Single(chip => chip.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == tag))
                .GetVisualDescendants().OfType<Button>().Single();

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
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                    Dispatcher.UIThread.RunJobs();
                    metadata = await metadataService.GetMetadataAsync(path);
                    if (metadata.Tags.Order().SequenceEqual(expected.Order())) break;
                } while (DateTime.UtcNow < deadline);

                Assert.Equal(expected.Order(), metadata.Tags.Order());
                vm.CurrentMetadata = metadata;
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            await panel.CancelAndReleaseLivePreviewAsync();
            window.Close();
            Directory.Delete(directory, true);
        }
    }

    private static async Task SeedFinderTagsAsync(string path, string[] tags)
    {
        var plist = new XDocument(new XElement("plist", new XAttribute("version", "1.0"),
            new XElement("array", tags.Select(tag => new XElement("string", tag)))));
        var start = new ProcessStartInfo("/usr/bin/xattr") { UseShellExecute = false };
        foreach (var argument in new[] { "-w", "com.apple.metadata:_kMDItemUserTags", plist.ToString(), path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
    }
}
