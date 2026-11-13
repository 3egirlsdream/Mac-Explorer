#if DEBUG
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class BulkPasteSmoke
{
    private sealed record TreeDigest(int Files, int Directories, int Links, long Bytes, string Hash);
    private sealed record Scenario(string Name, int SelectedItems, double ClipboardMs, double PasteMs,
        double MaxUiGapMs, int UiTicks, TreeDigest? Source, TreeDigest? Destination,
        string State, string? Error);

    public static async Task RunAsync(MainWindow owner)
    {
        var root = RuntimePaths.TestRoot ?? throw new InvalidOperationException("An isolated profile is required.");
        var output = Directory.CreateDirectory(Path.Combine(root, "bulk-paste")).FullName;
        var scenarios = new List<Scenario>();
        string? error = null;
        using var scope = App.Services.CreateScope();
        var services = scope.ServiceProvider;
        var boardName = "MacExplorer.BulkPasteSmoke." + Guid.NewGuid().ToString("N");
        var clipboard = new MacClipboardService(boardName);
        var externalClipboard = new MacClipboardService(boardName);
        var operations = ActivatorUtilities.CreateInstance<FileOpsViewModel>(services, clipboard);
        using var vm = ActivatorUtilities.CreateInstance<FileListViewModel>(services, clipboard, operations);
        var manager = services.GetRequiredService<IBackgroundTaskManager>();
        ((MainWindowViewModel)owner.DataContext!).AddTab(vm, select: true);
        try
        {
            var source = Path.Combine(output, "local-source");
            await Task.Run(() =>
            {
                Directory.CreateDirectory(source);
                for (var i = 0; i < 6000; i++)
                    File.WriteAllText(Path.Combine(source, $"file-{i:D5}.txt"), $"bulk contents {i}\n");
                File.WriteAllText(Path.Combine(source, new string('a', 240) + ".txt"), "long ascii filename");
                File.WriteAllText(Path.Combine(source, new string('文', 80) + ".txt"), "long unicode filename");
            });
            await RunScenarioAsync("local-many-selected", source, selectChildren: true);
            var external = Environment.GetEnvironmentVariable("MACEXPLORER_BULK_PASTE_SOURCE");
            if (!string.IsNullOrEmpty(external))
            {
                if (!Path.IsPathFullyQualified(external) || !external.StartsWith("/Volumes/", StringComparison.Ordinal)
                    || !Directory.Exists(external)) throw new IOException("An existing external source directory is required.");
                await RunScenarioAsync("external-folder", external, selectChildren: false);
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            await SaveReportAsync();
            Console.WriteLine($"Bulk paste smoke: {Path.Combine(output, "report.json")}");
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(error == null && scenarios.All(s => s.Error == null) ? 0 : 1);
        }

        Task SaveReportAsync() => File.WriteAllTextAsync(Path.Combine(output, "report.json"),
            JsonSerializer.Serialize(new { success = error == null && scenarios.All(s => s.Error == null), error, scenarios },
                new JsonSerializerOptions { WriteIndented = true }));

        async Task RunScenarioAsync(string name, string source, bool selectChildren)
        {
            var target = Directory.CreateDirectory(Path.Combine(output, name)).FullName;
            await vm.NavigateToAsync(target);
            var expected = await Task.Run(() => Digest(source));
            var paths = selectChildren ? await Task.Run(() => Directory.GetFileSystemEntries(source)) : [source];
            var before = manager.Tasks.Select(t => t.Id).ToHashSet();
            var clock = Stopwatch.StartNew();
            var last = clock.Elapsed.TotalMilliseconds;
            var maxGap = 0d;
            var ticks = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                var now = clock.Elapsed.TotalMilliseconds;
                maxGap = Math.Max(maxGap, now - last); last = now; ticks++;
            };
            timer.Start();
            double clipboardMs;
            try
            {
                if (selectChildren) clipboard.CopyFiles(paths);
                else
                {
                    externalClipboard.CopyFiles(paths);
                    if (clipboard.GetPasteKind() != ClipboardPasteKind.ExternalFiles)
                        throw new IOException("External clipboard URLs were not detected.");
                }
                clipboardMs = clock.Elapsed.TotalMilliseconds;
                Console.WriteLine($"Bulk paste starting: {name}, selected={paths.Length}, files={expected.Files}, clipboardMs={clipboardMs:F1}");
                await vm.PasteAsync().WaitAsync(TimeSpan.FromMinutes(5));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                maxGap = Math.Max(maxGap, clock.Elapsed.TotalMilliseconds - last);
            }
            finally { timer.Stop(); }
            var elapsed = clock.Elapsed.TotalMilliseconds;
            var tasks = manager.Tasks.Where(t => !before.Contains(t.Id)).ToArray();
            var task = tasks.LastOrDefault();
            TreeDigest? actual = null;
            var failure = task?.ErrorDetail;
            if (task?.State == BackgroundTaskState.Completed)
            {
                actual = await Task.Run(() => Digest(selectChildren ? target : Path.Combine(target, Path.GetFileName(source))));
                if (actual != expected) failure = "Source and destination content digests differ.";
            }
            else failure ??= "Paste did not complete: " + vm.StatusText;
            if (ticks == 0 || maxGap > 1000) failure ??= $"UI heartbeat stalled: max gap {maxGap:F1} ms, ticks {ticks}.";
            scenarios.Add(new(name, paths.Length, clipboardMs, elapsed, maxGap, ticks, expected, actual,
                task?.State.ToString() ?? "Missing", failure));
            await SaveReportAsync();
            owner.UpdateLayout();
            var visual = ElementComposition.GetElementVisual(owner)!;
            await visual.Compositor.RequestCommitAsync();
            using var bitmap = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
            bitmap.Save(Path.Combine(output, name + ".png"));
            Console.WriteLine($"Bulk paste finished: {name}, state={task?.State}, ms={elapsed:F1}, maxUiGapMs={maxGap:F1}, error={failure}");
        }
    }

    private static TreeDigest Digest(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = 0; var directories = 0; var links = 0; long bytes = 0;
        var buffer = new byte[128 * 1024];
        void Add(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text + "\0"));
        void Visit(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var isDirectory = File.GetAttributes(path).HasFlag(FileAttributes.Directory);
                FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
                Add(Path.GetRelativePath(root, path));
                if (info.LinkTarget is { } link) { Add("link"); Add(link); links++; }
                else if (isDirectory) { Add("directory"); directories++; Visit(path); }
                else
                {
                    Add("file"); files++;
                    using var input = File.OpenRead(path); bytes += input.Length; int read;
                    while ((read = input.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, read));
                }
            }
        }
        Visit(root);
        return new(files, directories, links, bytes, Convert.ToHexString(hash.GetHashAndReset()));
    }
}
#endif
