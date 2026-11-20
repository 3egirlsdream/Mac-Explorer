#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class FileOperationWindowSmoke
{
    internal static async Task RunAsync(MainWindow owner)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var output = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "operation-windows")).FullName;
        var manager = App.Services.GetRequiredService<IBackgroundTaskManager>();
        var language = App.Services.GetRequiredService<ILocalizationService>();
        try
        {
            FolderTabRestoreSmoke.ActivateApplication();
            foreach (var mode in new[] { FileOperationPresentation.Copy, FileOperationPresentation.Paste, FileOperationPresentation.Trash })
            foreach (var narrow in new[] { false, true })
            {
                language.SetLanguage(narrow ? "fr" : "zh-CN");
                var delete = mode == FileOperationPresentation.Trash;
                var display = new FileOperationDisplay(mode)
                {
                    Preparing = false, ByItems = delete, TotalItems = 500, DoneItems = 318,
                    TotalBytes = mode == FileOperationPresentation.Copy ? 13_851_269_530 : 3_221_225_472
                };
                var fraction = mode == FileOperationPresentation.Copy ? .65 : .42;
                display.DoneBytes = (long)(display.TotalBytes * fraction) - 64_000_000;
                var request = new FileTaskRequest(delete ? BackgroundTaskKind.Delete : BackgroundTaskKind.Copy, "",
                    [Path.Combine(output, delete ? "旧版素材" : "项目素材")], Path.Combine(output, mode == FileOperationPresentation.Copy ? "移动硬盘" : "工作资料")) { Operation = display };
                var task = new BackgroundTaskInfo { Request = request, Kind = request.Kind, CurrentFile = narrow ? new string('文', 150) + ".mp4" :
                    mode == FileOperationPresentation.Copy ? "旅行视频.mp4" : delete ? "封面草稿.png" : "产品设计.fig" };
                var window = new FileOperationWindow(manager, task)
                { RequestedThemeVariant = narrow ? ThemeVariant.Dark : ThemeVariant.Light };
                if (narrow) window.Width = 380;
                window.Show(owner); window.Activate();
                try
                {
                    await Task.Delay(550);
                    display.DoneItems = 360; display.DoneBytes += 64_000_000;
                    var bar = window.FindControl<ProgressBar>("Bar")!;
                    var previous = bar.Value;
                    var frames = new List<double>();
                    bar.PropertyChanged += (_, e) =>
                    {
                        if (e.Property == ProgressBar.ValueProperty) frames.Add(bar.Value);
                    };
                    window.Refresh();
                    await Task.Delay(250);
                    var target = delete ? 72 : fraction * 100;
                    if (!frames.Any(v => v > previous && v < target) || Math.Abs(bar.Value - target) > .01)
                        throw new InvalidOperationException($"Expected intermediate progress frames: {string.Join(", ", frames)}.");
                    window.UpdateLayout();
                    var stop = window.FindControl<AppButton>("Stop")!;
                    var scale = stop.Bounds.Height / 38;
                    var referenceHeight = delete ? 269 : mode == FileOperationPresentation.Paste ? 275 : 279;
                    if (!stop.Classes.Contains("small") || Math.Abs(stop.Bounds.Height - 24) > .5 ||
                        !narrow && Math.Abs(window.Bounds.Width - 977 * scale) > 1 ||
                        Math.Abs(window.Bounds.Height - referenceHeight * scale) > 1 || stop.Bounds.Width + 1 < 105 * scale ||
                        stop.TranslatePoint(default, window) is not { } pt || pt.X + stop.Bounds.Width > window.Bounds.Width)
                        throw new InvalidOperationException($"{mode}: unexpected window {window.Bounds.Size} or stop button {stop.Bounds}.");
                    VerifyRowLayout(window);
                    await ButtonComponentsSmoke.CaptureAsync(window, Path.Combine(output, $"{mode}-{(narrow ? "dark-narrow" : "light")}.png"));
                }
                finally { task.State = BackgroundTaskState.Completed; window.Close(); }
            }
            await VerifyQueuedCopyAsync(desktop, output, manager);
            foreach (var action in new[] { "resume", "stop-paused", "stop-running" })
                await VerifyRunningCopyAsync(desktop, output, manager, action);
            await VerifyStoppedDeleteAsync(desktop, output, manager);
            await VerifyFastAndFailedCopiesAsync(desktop, output);
            Console.WriteLine("File operation windows PASS " + output);
            desktop.Shutdown(0);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); desktop.Shutdown(1); }
    }
    private static void VerifyRowLayout(FileOperationWindow window)
    {
        var current = window.FindControl<TextBlock>("Current")!;
        var bar = window.FindControl<ProgressBar>("Bar")!;
        var amount = window.FindControl<TextBlock>("Amount")!;
        var currentBottom = current.TranslatePoint(new Point(0, current.Bounds.Height), window)!.Value.Y;
        var progress = bar.TranslatePoint(default, window)!.Value;
        var summary = amount.TranslatePoint(default, window)!.Value;
        if (progress.Y - currentBottom < 6 || summary.Y - (progress.Y + bar.Bounds.Height) < 3)
            throw new InvalidOperationException($"Progress rows overlap or lack spacing: text bottom {currentBottom}, bar {progress.Y}, summary {summary.Y}.");
        if (Math.Abs(summary.X - progress.X) > 1)
            throw new InvalidOperationException($"Progress bar {progress.X} and amount {summary.X} must share their leading edge.");
    }

    private static async Task VerifyQueuedCopyAsync(IClassicDesktopStyleApplicationLifetime desktop,
        string output, IBackgroundTaskManager manager)
    {
        var source = Path.Combine(output, "source.txt");
        await File.WriteAllTextAsync(source, "window smoke");
        var dest = Directory.CreateDirectory(Path.Combine(output, "destination")).FullName;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = manager.RunAsync(new(BackgroundTaskKind.Generic, "test", [], dest), async _ =>
        { await release.Task; return true; });
        var files = (CompositeFileService)App.Services.GetRequiredService<IFileService>();
        var copy = files.CopyBatchAsync([source], dest);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!desktop.Windows.OfType<FileOperationWindow>().Any()) await Task.Delay(25, timeout.Token);
            var window = desktop.Windows.OfType<FileOperationWindow>().Single();
            if (manager.PanelTasks.Any(t => t.Request?.Operation != null) || File.Exists(Path.Combine(dest, "source.txt")))
                throw new InvalidOperationException("Queued copy escaped its slot or appeared in the panel.");
            var task = manager.Tasks.Single(t => t.Request?.Operation != null && t.IsActive);
            window.FindControl<AppButton>("Pause")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            if (task.State != BackgroundTaskState.Paused) throw new InvalidOperationException("Pause did not reach the queued copy.");
            window.FindControl<AppButton>("Pause")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            release.TrySetResult(); await copy.WaitAsync(timeout.Token);
            while (desktop.Windows.OfType<FileOperationWindow>().Any()) await Task.Delay(25, timeout.Token);
            if (await File.ReadAllTextAsync(Path.Combine(dest, "source.txt"), timeout.Token) != "window smoke")
                throw new InvalidOperationException("Resumed copy did not finish.");
        }
        finally { release.TrySetResult(); await blocker; }
    }

    private sealed class CopyProgress(Action<FileOperationProgress> report) : IProgress<FileOperationProgress>
    {
        public void Report(FileOperationProgress progress) => report(progress);
    }

    private static async Task VerifyRunningCopyAsync(IClassicDesktopStyleApplicationLifetime desktop,
        string output, IBackgroundTaskManager manager, string action)
    {
        var destination = Directory.CreateDirectory(Path.Combine(output, action)).FullName;
        var content = new byte[2 * 1024 * 1024];
        new Random(42).NextBytes(content);
        var source = Path.Combine(output, "transfer.bin");
        await File.WriteAllBytesAsync(source, content);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var release = new ManualResetEventSlim();
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new CopyProgress(p =>
        {
            // Cross the transfer reporter's sampling interval even on a fast SSD.
            if (p.Percentage == 0) Thread.Sleep(120);
            else if (p.Percentage < 100 && firstWrite.TrySetResult()) release.Wait(timeout.Token);
        });
        var files = App.Services.GetRequiredService<IFileService>();
        var copy = files.CopyWithProgressAsync(source, destination, progress, timeout.Token);
        try
        {
            await firstWrite.Task.WaitAsync(timeout.Token);
            while (!desktop.Windows.OfType<FileOperationWindow>().Any()) await Task.Delay(10, timeout.Token);
            var window = desktop.Windows.OfType<FileOperationWindow>().Single();
            var task = manager.Tasks.Single(t => t.Request?.Destination == destination);
            void Click(string name) => window.FindControl<AppButton>(name)!.RaiseEvent(
                new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            if (action != "stop-running")
            {
                Click("Pause");
                release.Set();
                while (task.State != BackgroundTaskState.Paused) await Task.Delay(10, timeout.Token);
                var temporary = Directory.GetFiles(destination, "*.fkfinder-tmp").Single();
                var length = new FileInfo(temporary).Length;
                await Task.Delay(250);
                if (copy.IsCompleted || length <= 0 || length >= content.Length || new FileInfo(temporary).Length != length)
                    throw new InvalidOperationException("A paused copy continued writing to disk.");
            }
            if (action == "resume")
            {
                Click("Pause");
                var result = await copy.WaitAsync(timeout.Token);
                if (!(await File.ReadAllBytesAsync(result)).SequenceEqual(content))
                    throw new InvalidOperationException("The resumed copy did not match the source.");
            }
            else
            {
                Click("Stop");
                release.Set();
                try { await copy.WaitAsync(timeout.Token); throw new InvalidOperationException("Stop allowed the copy to finish."); }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
                if (task.State != BackgroundTaskState.Cancelled || Directory.EnumerateFileSystemEntries(destination).Any()
                    || !(await File.ReadAllBytesAsync(source)).SequenceEqual(content))
                    throw new InvalidOperationException("Stopped copy did not clean up or preserve the source.");
            }
            while (window.IsVisible) await Task.Delay(10, timeout.Token);
        }
        finally { release.Set(); }
    }

    private static async Task VerifyStoppedDeleteAsync(IClassicDesktopStyleApplicationLifetime desktop,
        string output, IBackgroundTaskManager manager)
    {
        var root = Directory.CreateDirectory(Path.Combine(output, "delete")).FullName;
        for (var i = 0; i < 5; i++) await File.WriteAllTextAsync(Path.Combine(root, $"{i}.txt"), "test");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var release = new ManualResetEventSlim();
        var firstDeletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChange()
        {
            var task = FileTaskContext.Current?.Task;
            if (task?.Request?.Sources.Contains(root) == true && task.Request.Operation?.DoneItems == 1
                && firstDeletion.TrySetResult()) release.Wait(timeout.Token);
        }
        manager.TasksChanged += OnChange;
        try
        {
            var deletion = App.Services.GetRequiredService<IFileService>().DeleteAsync(root, moveToTrash: false);
            await firstDeletion.Task.WaitAsync(timeout.Token);
            while (!desktop.Windows.OfType<FileOperationWindow>().Any()) await Task.Delay(10, timeout.Token);
            var window = desktop.Windows.OfType<FileOperationWindow>().Single();
            window.FindControl<AppButton>("Stop")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            release.Set();
            try { await deletion.WaitAsync(timeout.Token); throw new InvalidOperationException("Delete ignored stop."); }
            catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
            if (Directory.GetFiles(root).Length != 4) throw new InvalidOperationException("Delete crossed the stop boundary.");
            while (window.IsVisible) await Task.Delay(10, timeout.Token);
        }
        finally { release.Set(); manager.TasksChanged -= OnChange; }
    }

    private static async Task VerifyFastAndFailedCopiesAsync(IClassicDesktopStyleApplicationLifetime desktop, string output)
    {
        var files = (CompositeFileService)App.Services.GetRequiredService<IFileService>();
        var target = Directory.CreateDirectory(Path.Combine(output, "fast")).FullName;
        var opened = 0;
        using var observer = Window.WindowOpenedEvent.AddClassHandler<FileOperationWindow>((_, _) => opened++);
        await files.CopyBatchAsync([Path.Combine(output, "source.txt")], target);
        await Task.Delay(900);
        if (opened != 0) throw new InvalidOperationException("A fast copy flashed a window.");
        try { await files.CopyBatchAsync([Path.Combine(output, "missing")], target); }
        catch (FileNotFoundException) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!desktop.Windows.OfType<FileOperationWindow>().Any()) await Task.Delay(25, timeout.Token);
        var failure = desktop.Windows.OfType<FileOperationWindow>().Single();
        if (opened != 1 || failure.FindControl<Grid>("FailureSummary")?.IsVisible != true ||
            string.IsNullOrEmpty(failure.FindControl<SelectableTextBlock>("Error")?.Text))
            throw new InvalidOperationException("A fast failure did not expose its details.");
        failure.Close();
    }

}
#endif
