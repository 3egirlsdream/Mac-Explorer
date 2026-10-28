using System.Diagnostics;
using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "FileTasks")]
public sealed class TaskProgressRingTests : FileListViewModelTestBase
{
    [AvaloniaFact]
    [Trait("Smoke", "UI")]
    public void RingStopsForHiddenAncestorsMinimizedWindowAndDetach()
    {
        var ring = new AnimatedProgressRing { Value = 37, IsAnimating = true };
        var host = new Border { Child = ring };
        var window = new Window { Content = host, Width = 100, Height = 100 };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.True(ring.IsAnimationActive);
            host.IsVisible = false;
            Assert.False(ring.IsAnimationActive);
            host.IsVisible = true;
            Assert.True(ring.IsAnimationActive);
            window.WindowState = WindowState.Minimized;
            Assert.False(ring.IsAnimationActive);
            window.WindowState = WindowState.Normal;
            Assert.True(ring.IsAnimationActive);
            ring.IsAnimating = false;
            Assert.False(ring.IsAnimationActive);
            ring.IsAnimating = true;
            host.Child = null;
            Assert.False(ring.IsAnimationActive);
            host.Child = ring; window.UpdateLayout();
            Assert.True(ring.IsAnimationActive);
            window.Hide(); Assert.False(ring.IsAnimationActive);
            window.Show(); Assert.True(ring.IsAnimationActive);
            window.Close(); Assert.False(ring.IsAnimationActive);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false, "zh-CN", "未结束任务：2", "总体进度 50%")]
    [InlineData(true, "zh-Hant", "未結束任務：2", "整體進度 50%")]
    [InlineData(false, "en", "Unfinished tasks: 2", "Overall progress 50%")]
    [InlineData(true, "fr", "Tâches en cours : 2", "Progression globale 50%")]
    public async Task ButtonUsesAggregateProgressAndRefreshesLanguageWithoutTaskUpdates(
        bool dark, string language, string count, string progress)
    {
        using var theme = new FastListTestTheme();
        using var localization = new LocalizationService(new ShortcutMemorySettings());
        localization.SetLanguage("en");
        var manager = new BackgroundTaskManager();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0,
            configureServices: services => services.AddSingleton<IBackgroundTaskManager>(manager)
                .AddSingleton<ILocalizationService>(localization));
        var window = fixture.Window;
        window.Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        window.MinWidth = 0; window.Width = 620;
        var button = window.FindControl<Button>("TaskButton")!;
        var ring = window.FindControl<AnimatedProgressRing>("TaskProgressRing")!;
        var panel = window.FindControl<Border>("TaskOverlayPanel")!;
        Assert.False(button.IsVisible);
        var first = manager.AddTask("Copy", BackgroundTaskKind.Copy);
        var second = manager.AddTask("Download", BackgroundTaskKind.RemoteDownload);
        manager.UpdateProgress(first.Id, 20, ""); manager.UpdateProgress(second.Id, 80, "");
        await PumpUntilAsync(() => ring.Value == 50);
        localization.SetLanguage(language);
        await PumpUntilAsync(() => ToolTip.GetTip(button)?.ToString()?.Contains(count) == true);
        Assert.Contains(progress, ToolTip.GetTip(button)!.ToString());
        Assert.Equal(ToolTip.GetTip(button), AutomationProperties.GetName(button));
        window.UpdateLayout();
        Assert.Equal(16, ring.Bounds.Width); Assert.Equal(16, ring.Bounds.Height);
        Assert.Equal(2, ring.StrokeThickness);
        Assert.True(ring.IsAnimating);
        Assert.False(panel.IsVisible);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(panel.IsVisible);

        first.IsIndeterminate = true; manager.UpdateProgress(first.Id, 20, "");
        await PumpUntilAsync(() => ring.IsIndeterminate);
        Assert.DoesNotContain("%", ToolTip.GetTip(button)!.ToString());
        first.IsIndeterminate = false;
        first.State = second.State = BackgroundTaskState.Paused;
        manager.UpdateProgress(first.Id, 20, "");
        await PumpUntilAsync(() => !ring.IsAnimating);
        Assert.Contains("muted", ring.Classes);
        manager.CompleteTask(first.Id); manager.CompleteTask(second.Id);
        await PumpUntilAsync(() => ring.Classes.Contains("completed"));
        Assert.Equal(100, ring.Value); Assert.False(ring.IsIndeterminate);
        Assert.True(panel.IsVisible);
        await PumpUntilAsync(() => !(bool)typeof(Views.MainWindow)
            .GetField("_isTaskPanelAnimating", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await PumpUntilAsync(() => !panel.IsVisible);
        var next = manager.AddTask("Another copy", BackgroundTaskKind.Copy);
        manager.UpdateProgress(next.Id, 37, "");
        await PumpUntilAsync(() => ring.Value == 37 && ring.IsAnimating);
        Assert.False(panel.IsVisible);
        manager.CompleteTask(next.Id);
        await PumpUntilAsync(() => !ring.IsAnimating);
        Assert.False(panel.IsVisible);
        manager.RemoveTask(first.Id); manager.RemoveTask(second.Id); manager.RemoveTask(next.Id);
        await PumpUntilAsync(() => !button.IsVisible);
    }

    private static async Task PumpUntilAsync(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "Task button did not reach its expected state.");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1);
        }
    }
}
