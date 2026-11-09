using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;
using MacExplorer;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using SkiaSharp;

internal static class MemorySoak
{
    private static readonly MethodInfo CloseTab = typeof(MainWindow).GetMethod("CloseTabCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo CreateWindow = typeof(App).GetMethod("CreateWindow", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Queue<(string Kind, WeakReference Owner)> Closed = new();
    private static readonly Dictionary<string, int> Totals = new();
    private static int _cycle, _weakEvicted, _evictedAlive;
    private static long _sequence;
    private static readonly Stopwatch Clock = new();
    private static string _output = "";
    private static string _stage = "prepare";
    private static int _step;
    private static readonly bool PdfOnly = Environment.GetEnvironmentVariable("MACEXPLORER_MEMORY_PREVIEW_KIND") == "pdf";

    internal static async Task RunAsync(string output, TimeSpan duration, string? lifetimeKind = null)
    {
        _output = output;
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var window = (MainWindow)desktop.MainWindow!;
        var model = (MainWindowViewModel)window.DataContext!;
        var root = RuntimePaths.TestRoot ?? throw new InvalidOperationException("An isolated test profile is required.");
        Clock.Start();
        try
        {
            if (!MacExplorer.Platforms.MacOS.MacAutomationLifetime.NativeChildrenFixActive)
                throw new InvalidOperationException("The pinned native accessibility reference fix was not installed.");
            Program.VerifyNativeFontReuse();
            var realRoot = Environment.GetEnvironmentVariable("MACEXPLORER_MEMORY_REAL_ROOT");
            var fixtures = await Task.Run(() => string.IsNullOrEmpty(realRoot) ? CreateFixtures(root) : DiscoverRealFixtures(realRoot));
            var idleRoot = string.IsNullOrEmpty(realRoot) ? root : realRoot;
            await Task.Delay(5000);
            await ResetAsync(window, model, idleRoot);
            Checkpoint("baseline", 0, duration);
            if (MemoryRetentionProbe.Enabled)
            {
                MemoryRetentionProbe.Collect();
                MemoryRetentionProbe.Record("baseline-standard-gc");
                Checkpoint("retention.baseline-standard-gc", 0, duration);
                await Task.Delay(Environment.GetEnvironmentVariable("MACEXPLORER_MEMORY_HEAP_GRAPH_HOLD") == "1" ? 30000 : 10000);
            }
            var block = TimeSpan.FromTicks(duration.Ticks / 6);
            var active = TimeSpan.FromTicks(block.Ticks / 2);
            for (var index = 0; index < 6; index++)
            {
                var scenario = lifetimeKind ?? ((index % 3) switch { 0 => "navigation", 1 => "tabs-windows", _ => "previews" });
                var blockStart = Clock.Elapsed;
                Phase(scenario + ".active", index + 1, duration);
                do
                {
                    if (lifetimeKind != null) await LifetimeBatchAsync(window, model, fixtures, lifetimeKind);
                    else if (index % 3 == 0) await NavigationBatchAsync(window, model, fixtures);
                    else if (index % 3 == 1) await LifetimeBatchAsync(window, model, fixtures);
                    else await PreviewBatchAsync(window, model, fixtures);
                    _cycle++;
                    Checkpoint(scenario + ".cycle", index + 1, duration);
                    await Task.Delay(2000);
                } while (Clock.Elapsed - blockStart < active);
                await ResetAsync(window, model, idleRoot);
                Phase(scenario + ".idle", index + 1, duration);
                var idleUntil = blockStart + block;
                do
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, (idleUntil - Clock.Elapsed).TotalSeconds))));
                    Checkpoint(scenario + ".idle", index + 1, duration);
                } while (Clock.Elapsed < idleUntil);
            }
            Checkpoint("natural.final", 6, duration);
            MemoryDiagnostics.Record("soak.forced_gc", new { stage = "final-only" });
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            if (MemoryRetentionProbe.Enabled) MemoryRetentionProbe.Record("immediate-standard-gc");
            await Task.Delay(2000);
            Checkpoint("forced-gc.final", 6, duration);
            if (Program.NativeWindowsRetained != 0 || !MemoryRetentionProbe.Enabled && Closed.Any(entry => entry.Owner.IsAlive))
                throw new InvalidOperationException("Closed objects survived the final collection.");
            if (MemoryRetentionProbe.Enabled)
                await MemoryRetentionProbe.RunAsync(stage => Checkpoint(stage, 7, duration), () => ResetAsync(window, model, root));
            if (Closed.Any(entry => entry.Owner.IsAlive))
                throw new InvalidOperationException("Closed objects survived the retention investigation.");
            File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new
            {
                completed = true, requestedMinutes = duration.TotalMinutes, elapsedMinutes = Clock.Elapsed.TotalMinutes,
                dataSource = fixtures.IsReal ? "real" : "generated",
                previewKind = PdfOnly ? "pdf" : "mixed",
                cycles = _cycle, operations = Totals, weakTrackingCapacity = 2048, weakEvicted = _weakEvicted,
                evictedAliveWhenDropped = _evictedAlive, retained = Retained(),
                nativeWindowsChecked = Program.NativeWindowsChecked, nativeWindowsRetained = Program.NativeWindowsRetained,
                note = MemoryRetentionProbe.Enabled
                    ? "Controlled retention investigation: baseline/final/compacting collections and native cache interventions."
                    : "Natural-GC workload and idle phases; forced collection only after natural.final."
            }, JsonOptions));
            desktop.Shutdown();
        }
        catch (Exception error)
        {
            // The error report contains only the failure type and a fixed test stage, never exception messages or paths.
            File.WriteAllText(Path.Combine(output, "failure.json"), JsonSerializer.Serialize(new
                { completed = false, exception = error.GetType().Name, stage = _stage, step = _step,
                    cycles = _cycle, elapsedSeconds = Clock.Elapsed.TotalSeconds }, JsonOptions));
            Console.Error.WriteLine("Memory soak failed: " + error.GetType().Name);
            desktop.Shutdown(1);
        }
    }

    private static void Phase(string phase, int block, TimeSpan duration)
    {
        _stage = phase;
        MemoryDiagnostics.Record("soak.phase", new { phase, block, cycle = _cycle });
        Checkpoint(phase, block, duration);
    }

    private static void Checkpoint(string phase, int block, TimeSpan duration)
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        var footprint = MemoryDiagnostics.ReadPhysicalFootprint();
        var record = new { utc = DateTime.UtcNow, pid = Environment.ProcessId, sequence = ++_sequence, phase, block, cycles = _cycle,
            requestedMinutes = duration.TotalMinutes, elapsedSeconds = Clock.Elapsed.TotalSeconds,
            footprintBytes = footprint, rssBytes = process.WorkingSet64, managedBytes = GC.GetTotalMemory(false),
            heapBytes = gc.HeapSizeBytes, fragmentedBytes = gc.FragmentedBytes, committedBytes = gc.TotalCommittedBytes,
            gcIndex = gc.Index, gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
            nativeWindows = Program.ReadNativeWindows(),
            retained = Retained(), weakEvicted = _weakEvicted, evictedAliveWhenDropped = _evictedAlive,
            operations = Totals };
        var json = JsonSerializer.Serialize(record);
        File.AppendAllText(Path.Combine(_output, "soak-samples.jsonl"), json + "\n");
        var temporary = Path.Combine(_output, "progress.tmp");
        File.WriteAllText(temporary, json);
        File.Move(temporary, Path.Combine(_output, "progress.json"), true);
        Console.WriteLine(json);
        if (footprint > 3L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("The test footprint reached its capture limit.");
    }

    private static object[] Retained() => Closed.GroupBy(entry => entry.Kind)
        .Select(group => (object)new { kind = group.Key, observed = group.Count(), alive = group.Count(entry => entry.Owner.IsAlive) }).ToArray();

    private static void Track(string kind, WeakReference owner)
    {
        if (Closed.Count >= 2048)
        {
            if (Closed.Dequeue().Owner.IsAlive) _evictedAlive++;
            _weakEvicted++;
        }
        Closed.Enqueue((kind, owner));
        Totals[kind] = Totals.GetValueOrDefault(kind) + 1;
    }

    private static async Task ResetAsync(MainWindow window, MainWindowViewModel model, string root)
    {
        window.FindControl<SuperPreviewView>("SuperPreviewControl")?.Close();
        model.FileList.IsInfoPanelVisible = false;
        model.FileList.SetThumbnailZoomLevel(0);
        model.FileList.SetViewMode(ViewMode.List);
        await model.FileList.NavigateToAsync(root);
        await Task.Delay(1000);
    }

    private static async Task NavigationBatchAsync(MainWindow window, MainWindowViewModel model, Fixtures fixtures)
    {
        var files = model.FileList;
        for (var index = 0; index < fixtures.Directories.Length; index++)
        {
            files.SetViewMode(index % 2 == 0 ? ViewMode.List : ViewMode.Tree);
            await files.NavigateToAsync(fixtures.Directories[index]).WaitAsync(TimeSpan.FromSeconds(60));
            if (!fixtures.IsReal && files.Entries.Count < 2000) throw new InvalidOperationException("The directory fixture was not fully loaded.");
            Totals["directory-entries-observed"] = Math.Max(Totals.GetValueOrDefault("directory-entries-observed"), files.Entries.Count);
            var list = ActiveFileList(window).GetVisualDescendants().OfType<FastFileList>().First(control => control.IsVisible);
            await Task.Delay(100);
            var maximum = Math.Max(0, list.Extent.Height - list.Viewport.Height);
            var stride = Math.Max(300, list.Viewport.Height * 0.8);
            var requestedSteps = fixtures.IsReal ? (int)Math.Ceiling(maximum / stride) + 1 : 8;
            var steps = Math.Min(100, requestedSteps);
            for (var scroll = 0; scroll < steps; scroll++)
            {
                list.ScrollToOffset(fixtures.IsReal ? scroll == steps - 1 ? maximum : scroll * stride : scroll * 600);
                await Task.Delay(100);
            }
            Totals["scroll-steps"] = Totals.GetValueOrDefault("scroll-steps") + steps;
            if (fixtures.IsReal && requestedSteps <= 100)
                Totals["full-directory-scrolls"] = Totals.GetValueOrDefault("full-directory-scrolls") + 1;
            if (files.ViewMode == ViewMode.Tree)
            {
                foreach (var directory in files.Entries.Where(entry => entry.IsDirectory).Take(3).ToArray())
                {
                    await files.ExpandTreeDirectoryAsync(directory);
                    files.CollapseTreeDirectory(directory);
                }
            }
            Program.ReadAccessibility(window);
        }
        Totals["directory-visits"] = Totals.GetValueOrDefault("directory-visits") + fixtures.Directories.Length;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task LifetimeBatchAsync(MainWindow window, MainWindowViewModel model, Fixtures fixtures, string? kind = null)
    {
        if (kind is null or "tab") for (var index = 0; index < 4; index++)
        {
            await window.OpenNewTabAsync(fixtures.Directories[index]);
            Program.ReadAccessibility(window);
            await Task.Delay(250);
            Track("closed-tab", await CloseSelectedTabAsync(window, model));
        }
        if (kind is null or "window") Track("closed-window", await OpenAndCloseWindowAsync(window, fixtures.Directories[0]));
        if (kind is null or "settings") Track("closed-settings", await OpenAndCloseSettingsAsync(window, model.FileList));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> CloseSelectedTabAsync(MainWindow window, MainWindowViewModel model)
    {
        var tab = model.SelectedTab!;
        var weak = new WeakReference(tab.FileList);
        await (Task)CloseTab.Invoke(window, [tab])!;
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> OpenAndCloseWindowAsync(MainWindow source, string path)
    {
        var window = (MainWindow)CreateWindow.Invoke(null, [path, false, source, null])!;
        window.ShowActivated = false;
        window.Show();
        await Task.Delay(500);
        Program.ReadAccessibility(window);
        var weak = new WeakReference(window);
        var native = Program.RetainNativeWindow(window);
        window.Close();
        await WaitUntilAsync(() => !window.IsVisible, 15);
        Program.ReleaseNativeWindow(native);
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> OpenAndCloseSettingsAsync(MainWindow window, FileListViewModel files)
    {
        var settings = new SettingsDialog { DataContext = files, ShowActivated = false };
        settings.Show(window);
        await Task.Delay(300);
        Program.ReadAccessibility(settings);
        var weak = new WeakReference(settings);
        var native = Program.RetainNativeWindow(settings);
        settings.Close();
        await Task.Delay(200);
        Program.ReleaseNativeWindow(native);
        return weak;
    }

    private static async Task PreviewBatchAsync(MainWindow window, MainWindowViewModel model, Fixtures fixtures)
    {
        var files = model.FileList;
        files.SetViewMode(ViewMode.Grid);
        await files.NavigateToAsync(fixtures.Previews);
        files.SetThumbnailZoomLevel(3);
        var view = ActiveFileList(window);
        var entries = files.Entries.Where(entry => !entry.IsDirectory
            && (!fixtures.IsReal || IsPreviewFile(entry.FullPath))).Take(8).ToArray();
        if (entries.Length == 0) throw new InvalidOperationException("No supported preview files were available.");
        files.IsInfoPanelVisible = true;
        foreach (var entry in entries)
        {
            files.SelectEntry(entry);
            await Task.Delay(200);
        }
        files.SetThumbnailZoomLevel(4);
        foreach (var entry in entries.Take(4))
        {
            _stage = "previews.gallery-select";
            _step++;
            files.SelectEntry(entry);
            await WaitUntilAsync(() => (view.GalleryFilmstrip?.SelectedItem as FileSystemEntry)?.FullPath == entry.FullPath, 20);
            if (!await view.GalleryPreviewReady.WaitAsync(TimeSpan.FromSeconds(20)))
                throw new InvalidOperationException("The gallery preview did not complete.");
            await Task.Delay(300);
        }
        files.SetThumbnailZoomLevel(1);
        files.IsInfoPanelVisible = false;
        var preview = window.FindControl<SuperPreviewView>("SuperPreviewControl")!;
        foreach (var entry in entries.Take(2))
        {
            _stage = "previews.overlay-open";
            await preview.OpenAsync(entry).WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(300);
            preview.Close();
        }
        Totals["preview-selections"] = Totals.GetValueOrDefault("preview-selections") + entries.Length + 6;
        Program.ReadAccessibility(window);
    }

    private static FileListView ActiveFileList(MainWindow window)
        => window.GetVisualDescendants().OfType<ExplorerWorkspaceView>().First(workspace => workspace.IsVisible).FileListView;

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutSeconds)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            if (clock.Elapsed.TotalSeconds > timeoutSeconds) throw new TimeoutException();
            await Task.Delay(50);
        }
    }

    private sealed record Fixtures(string[] Directories, string Previews, bool IsReal = false);

    private static bool IsPreviewFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return PdfOnly ? extension == ".pdf"
            : extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".heic" or ".avif" or ".pdf";
    }

    private static Fixtures DiscoverRealFixtures(string root)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new ArgumentException("The real test directory is unavailable.");
        var pending = new Queue<(string Path, int Depth)>();
        var candidates = new List<(string Path, int Entries, int Previews)>();
        pending.Enqueue((root, 0));
        var scan = Stopwatch.StartNew();
        var scanned = 0;
        while (pending.TryDequeue(out var current) && scanned < 200 && scan.Elapsed.TotalSeconds < 30)
        {
            scanned++;
            try
            {
                var entries = new DirectoryInfo(current.Path).EnumerateFileSystemInfos().Take(20000).ToArray();
                var visible = entries.Where(entry => !entry.Name.StartsWith('.')).ToArray();
                var previews = visible.Count(entry => entry is FileInfo && IsPreviewFile(entry.Name));
                candidates.Add((current.Path, visible.Length, previews));
                if (current.Depth >= 3) continue;
                foreach (var directory in visible.OfType<DirectoryInfo>())
                {
                    if (pending.Count + scanned >= 200) break;
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0
                        || directory.Extension.ToLowerInvariant() is ".app" or ".photoslibrary" or ".bundle") continue;
                    pending.Enqueue((directory.FullName, current.Depth + 1));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        var selected = candidates.Where(item => item.Path != root).OrderByDescending(item => item.Entries)
            .Take(3).Select(item => item.Path).ToArray();
        var directories = new[] { root }.Concat(selected).ToArray();
        if (directories.Length < 4) directories = directories.Concat(Enumerable.Repeat(root, 4 - directories.Length)).ToArray();
        var preview = candidates.OrderByDescending(item => item.Previews).FirstOrDefault();
        if (preview.Previews == 0) throw new InvalidOperationException("No supported real preview files were found.");
        MemoryDiagnostics.Record("soak.real_data", new
        {
            scannedDirectories = scanned, scanElapsedMs = scan.ElapsedMilliseconds,
            selectedEntryCounts = directories.Select(path => candidates.FirstOrDefault(item => item.Path == path).Entries).ToArray(),
            supportedPreviewCount = preview.Previews
        });
        return new(directories, preview.Path, true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Fixtures CreateFixtures(string root)
    {
        var data = Path.Combine(root, "soak-fixtures");
        var directories = Enumerable.Range(0, 4).Select(index => Path.Combine(data, "directory-" + index)).ToArray();
        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory);
            for (var index = 0; index < 2000; index++) File.WriteAllText(Path.Combine(directory, $"file-{index:D4}.txt"), "Generated memory test fixture.");
            for (var branch = 0; branch < 3; branch++)
            {
                var child = Path.Combine(directory, "branch-" + branch); Directory.CreateDirectory(child);
                for (var index = 0; index < 40; index++) File.WriteAllText(Path.Combine(child, $"child-{index:D3}.txt"), "Generated fixture.");
            }
        }
        var previews = Path.Combine(data, "previews"); Directory.CreateDirectory(previews);
        using (var document = SKDocument.CreatePdf(Path.Combine(previews, "00-document.pdf")))
        {
            for (var page = 0; page < 12; page++)
            {
                var canvas = document.BeginPage(800, 1000);
                canvas.Clear(new SKColor((byte)(page * 17), 150, 200));
                document.EndPage();
            }
            document.Close();
        }
        using var bitmap = new SKBitmap(3000, 2000);
        using var paint = new SKPaint();
        using var imageCanvas = new SKCanvas(bitmap);
        for (var variant = 0; variant < 4; variant++)
        {
            imageCanvas.Clear(new SKColor((byte)(variant * 60), 90, 170));
            paint.Color = SKColors.Gold;
            for (var shape = 0; shape < 20; shape++) imageCanvas.DrawCircle(shape * 150, shape * 80, 100 + variant * 10, paint);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 90);
            var bytes = encoded.ToArray();
            for (var index = variant; index < 96; index += 4) File.WriteAllBytes(Path.Combine(previews, $"image-{index:D3}.png"), bytes);
        }
        return new(directories, previews);
    }
}
