using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "FileTasks")]
public sealed class SessionFileTasksUiTests
{
    private static void AssertCentered(Border row)
    {
        var title = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("task-title"));
        var status = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("task-percentage"));
        var action = row.GetVisualDescendants().OfType<Button>().Last();
        foreach (var control in new Control[] { title, status, action })
        {
            var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), row)!.Value;
            Assert.InRange(Math.Abs(center.Y - row.Bounds.Height / 2), 0, 1);
        }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(320)]
    [InlineData(640)]
    public void RowsWithoutProgressKeepNamesStatusAndActionsOnOneCenteredLine(int width)
    {
        using var theme = new FastListTestTheme();
        using var language = new LocalizationService(new ShortcutMemorySettings()); language.SetLanguage("en");
        var manager = new BackgroundTaskManager();
        foreach (var state in new[] { BackgroundTaskState.Waiting, BackgroundTaskState.Completed,
                     BackgroundTaskState.Cancelled, BackgroundTaskState.Failed })
        {
            var task = manager.AddTask("Move documents with a very long English name", BackgroundTaskKind.Move);
            task.State = state;
        }
        var presenter = new FileTaskPanelPresenter(manager, null);
        var window = new Window { Width = width, Height = 320, Content = presenter.View };
        window.Styles.Add((Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        window.Styles.Add((Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); presenter.Refresh(); Dispatcher.UIThread.RunJobs();
            var rows = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("file-task-row")).ToArray();
            Assert.Equal(4, rows.Length);
            foreach (var row in rows)
            {
                AssertCentered(row);
                Assert.InRange(row.Bounds.Height, 53, 55);
                Assert.DoesNotContain(row.GetVisualDescendants().OfType<ProgressBar>(), p => p.IsVisible);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false, 320)]
    [InlineData(true, 320)]
    [InlineData(false, 640)]
    [InlineData(true, 640)]
    public void TransferRowsKeepMetricsProgressAndActionsAlignedInBothLayouts(bool dark, int width)
    {
        using var theme = new FastListTestTheme();
        using var language = new LocalizationService(new ShortcutMemorySettings());
        language.SetLanguage("en");
        var manager = new BackgroundTaskManager();
        Add(BackgroundTaskKind.Copy, BackgroundTaskState.Running, "Design assets with a very long directory name 文件", 68, 4L << 30);
        Add(BackgroundTaskKind.RemoteDownload, BackgroundTaskState.Running, "Product demo.mp4", 32, 2L << 30);
        Add(BackgroundTaskKind.Compress, BackgroundTaskState.Paused, "Project backup.zip", 45, 1200L << 20);
        Add(BackgroundTaskKind.Move, BackgroundTaskState.Waiting, "Documents", 0, 0);
        var disconnected = Add(BackgroundTaskKind.RemoteUpload, BackgroundTaskState.NeedsAttention, "Promotional video.mp4", 18, 3L << 30);
        disconnected.ErrorDetail = "SshConnectionException: connection lost";
        manager.Tasks[2].Progress = 40; // A progress callback can lag behind the last written block at pause.
        var presenter = new FileTaskPanelPresenter(manager, null);
        var window = new Window { Width = width, Height = 500, Content = new ScrollViewer { Content = presenter.View },
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Styles.Add((Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/Styles.axaml")));
        window.Styles.Add((Styles)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(
            new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml")));
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            presenter.Refresh(); Dispatcher.UIThread.RunJobs();
            var rows = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("file-task-row")).ToArray();
            Assert.Equal(5, rows.Length);
            var allText = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("Waiting to start", allText);
            Assert.Contains("Disconnected", allText);
            Assert.Contains("45%", allText);
            Assert.Contains(allText, t => t?.StartsWith("Copy Design assets") == true);
            Assert.Contains(allText, t => t?.Contains("GB / 4 GB") == true);
            Assert.Contains(allText, t => t?.Contains("MB/s") == true);
            Assert.Contains(allText, t => t?.StartsWith("Paused · ") == true);
            Assert.DoesNotContain("Session history", allText);
            Assert.Empty(window.GetVisualDescendants().OfType<Expander>());
            Assert.Equal(4, window.GetVisualDescendants().OfType<ProgressBar>().Count(b => b.IsVisible));
            if (width >= 540)
            {
                var metrics = rows.Where((_, index) => index != 3).Select(row =>
                    row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("task-meta"))).ToArray();
                Assert.All(metrics, text => Assert.Equal(metrics[0].TranslatePoint(default, window)!.Value.X,
                    text.TranslatePoint(default, window)!.Value.X));
            }
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                Assert.InRange(row.Bounds.Height, 53, 90);
                var buttons = row.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToArray();
                Assert.All(buttons, button =>
                {
                    var icon = Assert.IsType<Viewbox>(button.Content);
                    var path = Assert.IsType<Avalonia.Controls.Shapes.Path>(icon.Child);
                    Assert.Null(path.Fill);
                    Assert.Equal(Avalonia.Media.PenLineCap.Round, path.StrokeLineCap);
                });
                Assert.Contains(buttons, b => AutomationProperties.GetName(b) == "Cancel task");
                if (index == 3) Assert.Single(buttons);
                else Assert.Equal(2, buttons.Length);
                foreach (var control in row.GetVisualDescendants().OfType<Control>()
                             .Where(c => c is Button or TextBlock or ProgressBar && c.IsEffectivelyVisible))
                {
                    var origin = control.TranslatePoint(default, window)!.Value;
                    Assert.True(origin.X >= -1 && origin.X + control.Bounds.Width <= width + 1,
                        $"Row {index}: {control.GetType().Name} at {origin.X}, width {control.Bounds.Width}, window {width}");
                }
                if (index == 3)
                {
                    AssertCentered(row);
                    continue;
                }
                var title = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("task-title"));
                var metrics = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("task-meta"));
                Assert.Equal(width >= 540 ? 0 : 1, Grid.GetRow(metrics));
                var bar = Assert.Single(row.GetVisualDescendants().OfType<ProgressBar>());
                Assert.Equal(title.TranslatePoint(default, window)!.Value.X, bar.TranslatePoint(default, window)!.Value.X);
                Assert.Equal(index >= 2, bar.Classes.Contains("muted"));
                if (index >= 2) Assert.Equal(rows[0].GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.Classes.Contains("task-meta")).Foreground, bar.Foreground);
            }
        }
        finally { window.Close(); }

        BackgroundTaskInfo Add(BackgroundTaskKind kind, BackgroundTaskState state, string name, double percent, long total)
        {
            var task = manager.AddTask(name, kind);
            task.Request = new(kind, name, ["/test/" + name]);
            task.IsQueuedFileTask = true; task.State = state; task.Progress = percent;
            task.TotalBytes = total; task.BytesTransferred = (long)(total * percent / 100);
            task.BytesPerSecond = state == BackgroundTaskState.Running ? 24L << 20 : 0;
            task.CanRetry = true;
            return task;
        }
    }
}
