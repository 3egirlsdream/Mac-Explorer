using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
using MacExplorer.Views.Dialogs;
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
            Assert.Contains(extract.SubItems!, a => a.Label == LocalizationText.Get("archive.smart_extract"));
            Assert.Contains(extract.SubItems!, a => a.Label == LocalizationText.Get("archive.extract_elsewhere"));
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
    [InlineData("zh-CN", false)]
    [InlineData("zh-Hant", true)]
    [InlineData("en", false)]
    [InlineData("fr", true)]
    public void ArchiveSettingsUseLocalizedControlsAndPersistChanges(string locale, bool dark)
    {
        using var theme = new FastListTestTheme();
        var settings = new ShortcutMemorySettings();
        using var language = new LocalizationService(settings, () => AppLanguage.ChineseSimplified);
        language.SetLanguage(locale);
        using var http = new HttpClient();
        var directory = Directory.CreateTempSubdirectory("fk-archive-settings-");
        var previous = App.Services;
        using var services = new ServiceCollection()
            .AddSingleton(new PhotoLocationConsent(settings, Path.Combine(directory.FullName, "consent")))
            .BuildServiceProvider();
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        var dialog = new SettingsDialog(new AppUpdateTests.DefaultAppServiceStub(), settings,
            new AppUpdateTests.ThemeServiceStub(), new AppUpdateTests.TypographyServiceStub(),
            new AppUpdateTests.OpenWithAppServiceStub(), new AppUpdateService(http), localizationService: language)
        {
            Width = 780, Height = 540, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        try
        {
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            var page = Assert.Single(dialog.FindControl<TabControl>("SettingsTabs")!.Items.OfType<LazyTabItem>(), t => t.PageKey == "general");
            var mode = page.FindContentControl<ComboBox>("ArchiveDoubleClickCombo")!;
            var format = page.FindContentControl<ComboBox>("ArchiveFormatCombo")!;
            var thumbnails = page.FindContentControl<ToggleSwitch>("ArchiveThumbnailsToggle")!;
            var passwords = page.FindContentControl<ToggleSwitch>("ArchivePasswordsToggle")!;
            Assert.Equal(0, mode.SelectedIndex);
            Assert.Equal(0, format.SelectedIndex);
            Assert.True(thumbnails.IsChecked == true);
            Assert.False(passwords.IsChecked == true);
            Assert.Equal(language.Get("archive.browse"), Assert.IsType<ComboBoxItem>(mode.Items[0]).Content);
            mode.SelectedIndex = 1;
            format.SelectedIndex = 2;
            thumbnails.IsChecked = false;
            passwords.IsChecked = true;
            Assert.True(settings.Get(ArchivePreferences.ExtractOnDoubleClick, false));
            Assert.Equal(ArchiveFormat.TarBz2, ArchivePreferences.ReadFormat(settings));
            Assert.False(settings.Get(ArchivePreferences.ShowThumbnails, true));
            Assert.True(settings.Get(ArchivePreferences.SavePasswords, false));
        }
        finally
        {
            dialog.Close();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, previous);
            directory.Delete(true);
        }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task ArchiveListOnlySchedulesVisibleThumbnailsAndCancelsWhenScrolledOrDisabled()
    {
        using var theme = new FastListTestTheme();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rows = Enumerable.Range(0, 2000).Select(i => new FileSystemEntry
        {
            FullPath = ArchivePathHelper.Build("/photos.zip", $"{i:D4}.png"), Name = $"{i:D4}.png",
            ArchiveThumbnailVersion = "fixture", Extension = ".png"
        }).ToArray();
        var list = new FastFileList
        {
            ThumbnailProvider = async (entry, _, token) =>
            {
                var later = string.CompareOrdinal(entry.Name, "0100.png") >= 0;
                if (entry == rows[0]) firstStarted.TrySetResult();
                if (later) laterStarted.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException)
                {
                    if (entry == rows[0]) firstCancelled.TrySetResult();
                    if (later) laterCancelled.TrySetResult();
                }
                return null;
            }
        };
        list.SetRows(rows);
        var window = new Window { Content = list, Width = 620, Height = 260 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.InRange(list.ObservedRowCount, 1, 32);
            Assert.False(laterStarted.Task.IsCompleted);
            list.ScrollToOffset(6000);
            Dispatcher.UIThread.RunJobs();
            await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await laterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            list.ShowArchiveThumbnails = false;
            Dispatcher.UIThread.RunJobs();
            await laterCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { window.Close(); }
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
