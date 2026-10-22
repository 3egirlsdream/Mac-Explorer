using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiquidGlassAvaloniaUI;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "FileTasks")]
public sealed class TaskPanelMotionTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData("zh-CN", "任务")]
    [InlineData("zh-Hant", "任務")]
    [InlineData("en", "Tasks")]
    [InlineData("fr", "Tâches")]
    public void TaskWindowUsesTheGeneralTaskTitle(string language, string expected)
    {
        using var localization = new LocalizationService(new ShortcutMemorySettings());
        localization.SetLanguage(language);
        var window = new TaskPanel();
        try { Assert.Equal(expected, window.Title); }
        finally { window.Close(); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false, 620)]
    [InlineData(true, 620)]
    [InlineData(false, 1100)]
    [InlineData(true, 1100)]
    public async Task PanelDefersHiddenAndAnimatingUpdatesThenShowsTheLatestProgress(bool dark, int width)
    {
        using var theme = new FastListTestTheme();
        using var localization = new LocalizationService(new ShortcutMemorySettings());
        localization.SetLanguage("en");
        var manager = new BackgroundTaskManager();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
            configureServices: services => services.AddSingleton<IBackgroundTaskManager>(manager));
        var window = fixture.Window;
        window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        window.Width = width;
        var mode = Field("_taskPanelMode");
        mode.SetValue(window, Enum.Parse(mode.FieldType, "Manual"));
        for (var i = 0; i < 12; i++) manager.CompleteTask(manager.AddTask("Create " + i, BackgroundTaskKind.Create).Id);
        var active = manager.AddTask("Copy", BackgroundTaskKind.Copy);
        for (var i = 0; i < 100; i++) manager.UpdateProgress(active.Id, i % 50, "file.txt");
        await PumpUntilAsync(() => (int)Field("_taskRefreshPending").GetValue(window)! == 0);
        Assert.Null(Field("_fileTaskPresenter").GetValue(window));

        var panel = window.FindControl<Border>("TaskOverlayPanel")!;
        var opening = Slide(window, true);
        var rows = panel.GetVisualDescendants().OfType<Border>().Where(row => row.Classes.Contains("file-task-row")).ToArray();
        Assert.Equal(13, rows.Length);
        var progress = Assert.Single(panel.GetVisualDescendants().OfType<ProgressBar>().Where(bar => bar.IsVisible));
        var previous = progress.Value;
        manager.UpdateProgress(active.Id, 73, "new-file.txt");
        typeof(MainWindow).GetMethod("RefreshTaskUi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        if (!opening.IsCompleted) Assert.Equal(previous, progress.Value);
        await PumpUntilAsync(() => opening.IsCompleted);
        await opening;
        Assert.Equal(73, progress.Value);
        Assert.Equal("Tasks", window.FindControl<TextBlock>("TaskPanelTitle")!.Text);
        Assert.True(LiquidGlassBackdrop.GetIsExcludedFromCapture(panel));
        Assert.Null(panel.RenderTransform);
        if (ElementComposition.GetElementVisual(panel) is { } visual)
            Assert.Equal(default, visual.Translation);

        var closing = Slide(window, false);
        Assert.False(panel.IsHitTestVisible);
        await PumpUntilAsync(() => closing.IsCompleted);
        await closing;
        Assert.False(panel.IsVisible);
        var reopening = Slide(window, true);
        await PumpUntilAsync(() => reopening.IsCompleted);
        await reopening;
        Assert.True(panel.IsVisible);
        Assert.True(panel.IsHitTestVisible);
        Assert.Equal(rows, panel.GetVisualDescendants().OfType<Border>().Where(row => row.Classes.Contains("file-task-row")));
        Assert.InRange(panel.Width, 280, Math.Min(560, width - 32));
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static Task Slide(MainWindow window, bool show) => (Task)typeof(MainWindow)
        .GetMethod("SlideTaskPanelAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [show])!;
    private static async Task PumpUntilAsync(Func<bool> completed)
    {
        var clock = Stopwatch.StartNew();
        while (!completed())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "Task panel operation did not finish.");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1);
        }
    }
}
