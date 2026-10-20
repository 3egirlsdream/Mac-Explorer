using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Archive")]
public sealed class ArchiveInteractionTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false, "sample.txt.xz", "sample.txt", "")]
    [InlineData(true, "sample.tar.lz", "中文说明.txt", "docs/")]
    public async Task FileListNavigationAndContentPreviewUseNewFormats(bool dark, string name, string file, string prefix)
    {
        using var theme = new FastListTestTheme();
        var service = new ArchiveService();
        var path = ArchiveFormatTests.Fixture(name);
        var files = new FakeFileService(Path.GetDirectoryName(path)!);
        using var vm = CreateViewModel(files, archiveService: service);
        await vm.OpenEntryAsync(new FileSystemEntry { FullPath = path, Name = name });
        Assert.True(vm.IsArchiveView);
        if (prefix.Length > 0) await vm.OpenEntryAsync(Assert.Single(vm.Entries, e => e.Name == "docs"));
        var selected = Assert.Single(vm.Entries, e => e.Name == file);
        Assert.Equal(ArchivePathHelper.Build(path, prefix + file), selected.FullPath);

        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0, configureServices: services =>
            services.AddSingleton<IArchiveService>(service).AddSingleton<IFileService>(files));
        fixture.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        fixture.Window.Width = 700;
        var preview = fixture.Window.FindControl<SuperPreviewView>("SuperPreviewControl")!;
        await preview.OpenAsync(selected);
        Assert.Equal(ArchiveFormatTests.SampleText, preview.FindControl<TextBox>("PreviewText")!.Text);
        Assert.True(preview.FindControl<Border>("PreviewListCard")!.IsVisible);
        preview.Close();
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData("zh-CN", false)]
    [InlineData("en", true)]
    public async Task ExtractionMenuRefreshesLanguageAndBothDestinationsWork(string locale, bool namedFolder)
    {
        using var theme = new FastListTestTheme();
        using var language = new LocalizationService(new ShortcutMemorySettings());
        var root = Directory.CreateTempSubdirectory("fk-archive-ui-");
        try
        {
            var path = Path.Combine(root.FullName, "项目.tar.zstd");
            File.Copy(ArchiveFormatTests.Fixture("sample.tar.zst"), path);
            var service = new ArchiveService();
            var entry = new FileSystemEntry { FullPath = path, Name = Path.GetFileName(path) };
            using var vm = CreateViewModel(new FakeFileService(root.FullName), archiveService: service);
            language.SetLanguage(locale == "en" ? "zh-CN" : "en");
            await vm.LoadCompleteFileContextMenuAsync(entry);
            language.SetLanguage(locale);
            var actions = await vm.LoadCompleteFileContextMenuAsync(entry);
            var extract = Assert.Single(actions, a => a.Label == LocalizationText.Get("解压"));
            Assert.Contains(extract.SubItems!, a => a.Label == LocalizationText.Get("解压到当前文件夹"));
            Assert.Contains(extract.SubItems!, a => a.Label == LocalizationText.Get("解压到 {0}", "项目"));
            var manager = new BackgroundTaskManager();
            var archives = new ArchiveViewModel(service, manager);
            Task<string?> Password() => throw new InvalidOperationException("No password prompt expected");
            if (namedFolder)
                await archives.ExtractToNamedFolderAsync(entry, root.FullName, _ => { }, () => Task.CompletedTask, Password);
            else
                await archives.ExtractHereAsync(entry, root.FullName, _ => { }, () => Task.CompletedTask, Password);
            var task = Assert.Single(manager.Tasks);
            await manager.WaitForCompletionAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(BackgroundTaskState.Completed, task.State);
            var destination = namedFolder ? Path.Combine(root.FullName, "项目") : root.FullName;
            Assert.Equal(ArchiveFormatTests.SampleText, await File.ReadAllTextAsync(Path.Combine(destination, "docs", "中文说明.txt")));
        }
        finally { root.Delete(true); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false, 320)]
    [InlineData(true, 640)]
    public void UnknownSizeProgressShowsActivityAndPausesWithoutAPercentage(bool dark, int width)
    {
        using var theme = new FastListTestTheme();
        using var language = new LocalizationService(new ShortcutMemorySettings());
        language.SetLanguage("en");
        var manager = new BackgroundTaskManager();
        var task = manager.AddTask("large file.xz", BackgroundTaskKind.Extract);
        task.IsIndeterminate = true;
        var presenter = new FileTaskPanelPresenter(manager, null);
        var window = new Window { Content = presenter.View, Width = width, Height = 160,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        foreach (var asset in new[] { "Styles", "ComponentStyles" })
            window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri($"avares://MacExplorer/Assets/{asset}.axaml")));
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); presenter.Refresh();
            var bar = Assert.Single(window.GetVisualDescendants().OfType<ProgressBar>());
            var percentage = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), b => b.Classes.Contains("task-percentage"));
            Assert.True(bar.IsIndeterminate);
            Assert.DoesNotContain("%", percentage.Text!);
            Assert.NotEqual(LocalizationText.Get("已完成"), percentage.Text);
            Assert.Equal(width < 540 ? 90 : double.PositiveInfinity, percentage.MaxWidth);
            task.State = BackgroundTaskState.Paused;
            presenter.Refresh();
            bar = Assert.Single(window.GetVisualDescendants().OfType<ProgressBar>());
            Assert.False(bar.IsIndeterminate);
            percentage = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), b => b.Classes.Contains("task-percentage"));
            Assert.Equal(LocalizationText.Get("已暂停"), percentage.Text);
        }
        finally { window.Close(); }
    }
}
