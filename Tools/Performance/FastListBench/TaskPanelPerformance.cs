using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using LiquidGlassAvaloniaUI;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;

internal static class TaskPanelPerformance
{
    public static async Task RunAsync(string output, bool stress = false)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            var window = (MainWindow)desktop.MainWindow!;
            var root = Environment.GetEnvironmentVariable("MACEXPLORER_TEST_ROOT")!;
            await ((MainWindowViewModel)window.DataContext!).FileList.NavigateToAsync(root);
            var manager = MacExplorer.App.Services.GetRequiredService<IBackgroundTaskManager>();
            var panel = window.FindControl<Border>("TaskOverlayPanel")!;
            var mode = typeof(MainWindow).GetField("_taskPanelMode", BindingFlags.Instance | BindingFlags.NonPublic)!;
            mode.SetValue(window, Enum.Parse(mode.FieldType, "Manual"));
            var slide = typeof(MainWindow).GetMethod("SlideTaskPanelAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Task Slide(bool show) => (Task)slide.Invoke(window, [show])!;
            var records = new List<object>();
            var aborted = false;
            var updateIntervalMs = stress ? 8 : 50;
            const double maximumStepMs = 500;
            foreach (var count in stress ? new[] { 2, 100 } : new[] { 2 })
            {
                foreach (var task in manager.Tasks) manager.RemoveTask(task.Id);
                for (var i = 0; i < count; i++)
                    manager.CompleteTask(manager.AddTask($"新建 性能测试文件 {i}.txt", BackgroundTaskKind.Create).Id);
                await Task.Delay(200);
                foreach (var busy in new[] { false, true })
                {
                    await Task.Delay(250);
                    var active = busy ? manager.AddTask("复制性能测试", BackgroundTaskKind.Copy) : null;
                    var progress = 0;
                    using var updater = busy ? new Timer(_ => manager.UpdateProgress(active!.Id, Interlocked.Increment(ref progress) % 100, "测试文件.txt"),
                        null, 0, updateIntervalMs) : null;
                    foreach (var show in new[] { true, false })
                    {
                        var warmup = Stopwatch.StartNew();
                        await Slide(show);
                        if (warmup.Elapsed.TotalMilliseconds > maximumStepMs)
                            throw new InvalidOperationException($"Task panel warmup exceeded {maximumStepMs}ms; benchmark stopped.");
                    }
                    var frames = new List<double>();
                    var layouts = 0;
                    var samples = 0;
                    var openingTimes = new List<double>();
                    var closingTimes = new List<double>();
                    var measuring = true;
                    var previous = Stopwatch.GetTimestamp();
                    window.RequestAnimationFrame(Frame);
                    void Frame(TimeSpan _)
                    {
                        var now = Stopwatch.GetTimestamp();
                        frames.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
                        previous = now;
                        if (measuring) window.RequestAnimationFrame(Frame);
                    }
                    void Layout(object? _, EventArgs e) => layouts++;
                    void Changed(object? _, AvaloniaPropertyChangedEventArgs e) => samples++;
                    panel.LayoutUpdated += Layout;
                    var transform = panel.RenderTransform as AvaloniaObject;
                    if (transform != null) transform.PropertyChanged += Changed;
                    LiquidGlassDiagnostics.Reset();
                    var process = Process.GetCurrentProcess();
                    var cpu = process.TotalProcessorTime;
                    var clock = Stopwatch.StartNew();
                    for (var i = 0; i < (stress ? 5 : 3); i++)
                    {
                        var started = Stopwatch.GetTimestamp();
                        await Slide(true);
                        openingTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        if (openingTimes[^1] > maximumStepMs) { aborted = true; break; }
                        started = Stopwatch.GetTimestamp();
                        await Slide(false);
                        closingTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        if (closingTimes[^1] > maximumStepMs) { aborted = true; break; }
                    }
                    measuring = false;
                    panel.LayoutUpdated -= Layout;
                    if (transform != null) transform.PropertyChanged -= Changed;
                    var ordered = frames.Order().ToArray();
                    var glass = LiquidGlassDiagnostics.Snapshot;
                    records.Add(new { count, busy, stress, updateIntervalMs, aborted, maximumStepMs, elapsedMs = clock.Elapsed.TotalMilliseconds,
                        cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, layouts, uiTransformChanges = samples,
                        openingTimes, closingTimes,
                        frameCallbacks = frames.Count, frameCallbackP95Ms = ordered.Length == 0 ? 0 : ordered[(int)((ordered.Length - 1) * .95)],
                        frameCallbackMaxMs = ordered.LastOrDefault(), glass.CapturesStarted, glass.RendererInvalidations });
                    if (active != null) manager.CompleteTask(active.Id);
                    if (aborted) break;
                }
                if (aborted) break;
            }
            await File.WriteAllTextAsync(Path.Combine(output, "task-panel-results.json"),
                JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            if (aborted)
            {
                panel.IsVisible = false;
                await File.WriteAllTextAsync(Path.Combine(output, "task-panel-error.txt"),
                    $"A task panel step exceeded {maximumStepMs}ms; remaining benchmark rounds were cancelled.");
                desktop.Shutdown(1);
                return;
            }
            if (Environment.GetEnvironmentVariable("FASTLIST_BENCH_KEEP_OPEN") == "1")
            {
                await Slide(true);
                Console.WriteLine("Task panel benchmark complete; test window is ready for review.");
                return;
            }
            desktop.Shutdown();
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "task-panel-error.txt"), ex.ToString());
            desktop.Shutdown(1);
        }
    }
}
