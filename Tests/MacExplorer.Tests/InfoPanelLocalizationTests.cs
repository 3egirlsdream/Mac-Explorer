using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public async Task InfoPanelLocalizesUnsupportedPreviewAndKeepsLoadedContentWhenLanguageChanges()
    {
        using var language = new LocalizationService(new StartupSettings(), () => AppLanguage.English);
        var root = Path.Combine(Path.GetTempPath(), "fkfinder-info-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "我的文件.unknown");
        await File.WriteAllTextAsync(path, "sample", TestContext.Current.CancellationToken);
        using var vm = CreateViewModel(new FakeFileService(root));
        vm.IsInfoPanelVisible = true;
        vm.SelectedEntries.Add(new FileSystemEntry { FullPath = path, Name = Path.GetFileName(path), Extension = ".unknown", Size = FileHashCalculator.AutomaticByteLimit + 1 });
        var panel = new InfoPanelView { DataContext = vm };
        var window = new Window { Width = 380, Height = 800, Content = panel };
        try
        {
            window.Show();
            await panel.SetLivePreviewStateAsync(true, 1);
            for (var i = 0; i < 200 && panel.FindControl<TextBlock>("PreviewFeedbackText")!.Text != "Preview is not supported for this format"; i++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal("Info", panel.FindControl<TextBlock>("PanelTitle")!.Text);
            Assert.Equal("Preview is not supported for this format", panel.FindControl<TextBlock>("PreviewFeedbackText")!.Text);
            Assert.Equal("Calculate on demand", panel.FindControl<TextBlock>("InfoHash")!.Text);
            var icon = panel.FindControl<Image>("PreviewFileIcon")!.Source;
            var generation = panel.PreviewRequestGeneration;
            language.SetLanguage(AppLanguage.ChineseSimplified);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("信息", panel.FindControl<TextBlock>("PanelTitle")!.Text);
            Assert.Equal("暂不支持此格式的预览", panel.FindControl<TextBlock>("PreviewFeedbackText")!.Text);
            Assert.Equal("按需计算", panel.FindControl<TextBlock>("InfoHash")!.Text);
            Assert.Same(icon, panel.FindControl<Image>("PreviewFileIcon")!.Source);
            Assert.Equal(generation, panel.PreviewRequestGeneration);
            Assert.Equal("我的文件.unknown", panel.FindControl<TextBlock>("SelectedFileName")!.Text);
            panel.RestorePreviewExpanded(true);
            Assert.Equal("文件预览", panel.FindControl<TextBlock>("PanelTitle")!.Text);
            language.SetLanguage(AppLanguage.English);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("File preview", panel.FindControl<TextBlock>("PanelTitle")!.Text);
            Assert.Equal("Collapse preview", ToolTip.GetTip(panel.FindControl<Button>("ExpandPreviewBtn")!));
            Assert.Equal("Preview is not supported for this format", panel.FindControl<TextBlock>("PreviewFeedbackText")!.Text);
        }
        finally
        {
            await panel.SetLivePreviewStateAsync(false, 2);
            window.Close();
            Directory.Delete(root, true);
        }
    }
}
