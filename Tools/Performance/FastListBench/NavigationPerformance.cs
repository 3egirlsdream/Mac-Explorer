using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Indexing;
using MacExplorer.Performance;
using MacExplorer.Services.Search;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;

internal static class NavigationPerformance
{
    public static async Task RunAsync(string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            var root = Environment.GetEnvironmentVariable("MACEXPLORER_TEST_ROOT")!;
            var data = Path.Combine(root, "navigation-data");
            var config = MacExplorer.App.Services.GetRequiredService<IndexConfiguration>();
            config.ExcludedPaths.Add(Path.Combine(root, ".macexplorer"));
            await Task.Run(() =>
            {
                foreach (var count in new[] { 1000, 10_000, 100_000 })
                {
                    var directory = Path.Combine(data, count.ToString());
                    Directory.CreateDirectory(directory);
                    for (var i = 0; i < count; i++)
                        using (File.Create(Path.Combine(directory, $"file-{i:D6}-性能验证.txt"))) { }
                }
            });
            var window = desktop.MainWindow!;
            window.Width = 1100;
            window.Height = 760;
            var vm = ((MainWindowViewModel)window.DataContext!).FileList;
            var list = window.GetVisualDescendants().OfType<FastFileList>().Single();
            var indexer = MacExplorer.App.Services.GetRequiredService<SearchIndexer>();
            await WaitReadyAsync(indexer, root);
            var records = new List<object>();
            var renders = new List<double>();
            var commits = new List<double>();
            TaskCompletionSource? firstFrame = null;
            long started = 0;
            double firstFrameMs = 0;
            var collecting = false;
            string? expectedPath = null;
            var expectedCount = 0;
            using var metrics = new MeterListener();
            metrics.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == FileListPerformanceMetrics.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            metrics.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            {
                if (!collecting) return;
                if (instrument.Name == "filelist.ui.commit.duration_ms") commits.Add(value);
                if (instrument.Name != "filelist.fast.render.duration_ms") return;
                renders.Add(value);
                if (firstFrame != null && !vm.IsDirectoryLoading && vm.CurrentPath == expectedPath && vm.Entries.Count == expectedCount && list.Rows.Count == vm.Entries.Count)
                {
                    firstFrameMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    firstFrame.TrySetResult();
                    firstFrame = null;
                }
            });
            metrics.Start();
            var heartbeat = new List<double>();
            var previousBeat = Stopwatch.GetTimestamp();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Normal, (_, _) =>
            {
                var now = Stopwatch.GetTimestamp();
                if (collecting) heartbeat.Add(Stopwatch.GetElapsedTime(previousBeat, now).TotalMilliseconds);
                previousBeat = now;
            });
            timer.Start();
            foreach (var mode in new[] { ViewMode.List, ViewMode.Grid, ViewMode.Tree })
            foreach (var busy in new[] { false, true })
            foreach (var count in new[] { 1000, 10_000, 100_000 })
            {
                vm.SetViewMode(mode);
                await vm.NavigateToAsync(root);
                await WaitReadyAsync(indexer, root);
                if (busy) indexer.Refresh(root);
                for (var repeat = 0; repeat < 3; repeat++)
                {
                    if (repeat > 0) await vm.NavigateToAsync(root);
                    var phaseAtStart = indexer.Observe(root).Status.Phase;
                    renders.Clear(); commits.Clear(); heartbeat.Clear();
                    previousBeat = Stopwatch.GetTimestamp();
                    firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    var frameTask = firstFrame.Task;
                    expectedPath = Path.Combine(data, count.ToString());
                    expectedCount = count;
                    started = Stopwatch.GetTimestamp();
                    collecting = true;
                    await vm.NavigateToAsync(expectedPath);
                    var navigationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    await frameTask.WaitAsync(TimeSpan.FromSeconds(30));
                    await Task.Delay(100);
                    var navigationHeartbeatMaxMs = heartbeat.Count == 0 ? 0 : heartbeat.Max();
                    var navigationRenderMaxMs = renders.Count == 0 ? 0 : renders.Max();
                    heartbeat.Clear(); renders.Clear();
                    for (var step = 0; step < 30; step++)
                    {
                        list.Offset = new(0, step * 35);
                        await Task.Delay(16);
                    }
                    collecting = false;
                    if (vm.Entries.Count != count) throw new InvalidOperationException($"Lost entries: {vm.Entries.Count}/{count}");
                    for (var i = 0; i < count; i++)
                        if (vm.Entries[i].Name != $"file-{i:D6}-性能验证.txt")
                            throw new InvalidOperationException($"Unstable order at {i}: {vm.Entries[i].Name}");
                    var record = new
                    {
                        mode = mode.ToString(), busy, count, repeat, phaseAtStart = phaseAtStart.ToString(),
                        phaseAtEnd = indexer.Observe(root).Status.Phase.ToString(), navigationMs, firstFrameMs,
                        commitMaxMs = commits.Count == 0 ? 0 : commits.Max(), navigationHeartbeatMaxMs, navigationRenderMaxMs,
                        scrollHeartbeatMaxMs = heartbeat.Count == 0 ? 0 : heartbeat.Max(),
                        scrollRenderP95Ms = Percentile(renders, .95), scrollRenderMaxMs = renders.Count == 0 ? 0 : renders.Max(),
                        entryCount = vm.Entries.Count, visibleRows = list.LastRenderedRowCount
                    };
                    records.Add(record);
                    Console.WriteLine(JsonSerializer.Serialize(record));
                    await File.WriteAllTextAsync(Path.Combine(output, "navigation-results.json"),
                        JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            timer.Stop();
            desktop.Shutdown();
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "navigation-error.txt"), ex.ToString());
            Console.Error.WriteLine(ex);
            desktop.Shutdown(1);
        }
    }

    private static double Percentile(List<double> samples, double percentile)
    {
        if (samples.Count == 0) return 0;
        var sorted = samples.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    }

    private static async Task WaitReadyAsync(SearchIndexer indexer, string root)
    {
        indexer.EnsureRoot(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (true)
        {
            var observation = indexer.Observe(root);
            if (observation.Status.Phase == SearchIndexPhase.Ready) return;
            if (observation.Status.Phase is SearchIndexPhase.Partial or SearchIndexPhase.Unavailable)
                throw new InvalidOperationException(observation.Status.Error);
            await observation.Changed.WaitAsync(timeout.Token);
        }
    }
}
