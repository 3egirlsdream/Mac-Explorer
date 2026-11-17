#if DEBUG
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class FolderRenderSoak
{
    internal static async Task RunAsync(MainWindow owner)
    {
        var output = Path.Combine(RuntimePaths.TestRoot!, "folder-render-soak");
        Directory.CreateDirectory(output);
        var samples = new List<object>();
        var closed = new List<WeakReference>();
        var watch = Stopwatch.StartNew();
        var cycles = 0;
        string? error = null;
        try
        {
            FolderTabRestoreSmoke.ActivateApplication();
            owner.ShowAndActivate(); owner.Width = 1100; owner.Height = 750;
            var root = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "Documents", "SoakAlbums")).FullName;
            using (var source = AssetLoader.Open(new Uri("avares://MacExplorer/Assets/FolderCoverSamples/alpine-lake.jpg")))
            using (var file = File.Create(Path.Combine(root, "source.jpg"))) source.CopyTo(file);
            for (var i = 0; i < 120; i++)
            {
                var folder = Directory.CreateDirectory(Path.Combine(root, $"Album-{i:D3}")).FullName;
                File.Copy(Path.Combine(root, "source.jpg"), Path.Combine(folder, "photo.jpg"));
            }
            var model = (MainWindowViewModel)owner.DataContext!;
            var settings = App.Services.GetRequiredService<ISettingsService>();
            settings.Set(FileListViewModel.FolderPhotoCoverSettingKey, true);
            var close = typeof(MainWindow).GetMethod("CloseTabCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            // Every cycle closes its workspace; no forced GC during the measured workload.
            while (watch.Elapsed < TimeSpan.FromMinutes(10))
            {
                await owner.OpenNewTabAsync(root);
                var tab = model.SelectedTab!;
                tab.FileList.SetViewMode(ViewMode.Grid);
                var style = (FolderPhotoCoverStyle)(cycles % 3);
                settings.Set(FolderPhotoCoverStyles.SettingKey, FolderPhotoCoverStyles.Value(style));
                await Task.Delay(300);
                var list = owner.GetVisualDescendants().OfType<FastFileList>().First(control => control.IsEffectivelyVisible);
                list.GridIconSize = cycles % 2 == 0 ? 56 : 128;
                for (var step = 0; step < 6; step++)
                {
                    list.ScrollToEntry(list.Rows[step * 19]);
                    await Task.Delay(step % 2 == 0 ? 100 : 500);
                }
                closed.Add(new WeakReference(tab));
                closed.Add(new WeakReference(list));
                await (Task)close.Invoke(owner, [tab])!;
                cycles++;
                samples.Add(new { seconds = watch.Elapsed.TotalSeconds, cycles, style = style.ToString(),
                    rss = Process.GetCurrentProcess().WorkingSet64, footprint = MemoryDiagnostics.ReadPhysicalFootprint(),
                    managed = GC.GetTotalMemory(false), allocated = GC.GetTotalAllocatedBytes(),
                    collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray(),
                    materials = FolderMaterialCache.Shared.GetMemoryState(), retainedMaterialBytes = FolderMaterialCache.Shared.CachedBytes,
                    closedObjectsStillAlive = closed.Count(reference => reference.IsAlive) });
                File.WriteAllText(Path.Combine(output, "samples.json"), JsonSerializer.Serialize(samples));
                if (FolderMaterialCache.Shared.CachedBytes > 4L * 1024 * 1024) throw new InvalidOperationException("Material budget exceeded.");
                await Task.Delay(600);
            }
            // Leave idle maintenance time to trim resources. This is separate from the 10-minute workload.
            await Task.Delay(TimeSpan.FromSeconds(65));
            var beforeCollection = new { footprint = MemoryDiagnostics.ReadPhysicalFootprint(), managed = GC.GetTotalMemory(false),
                materials = FolderMaterialCache.Shared.GetMemoryState(), alive = closed.Count(reference => reference.IsAlive) };
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            File.WriteAllText(Path.Combine(output, "retention.json"), JsonSerializer.Serialize(new { beforeCollection,
                afterCollection = new { footprint = MemoryDiagnostics.ReadPhysicalFootprint(), managed = GC.GetTotalMemory(false),
                    materials = FolderMaterialCache.Shared.GetMemoryState(), alive = closed.Count(reference => reference.IsAlive) } }));
        }
        catch (Exception ex) { error = ex.ToString(); }
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { success = error == null, seconds = watch.Elapsed.TotalSeconds, cycles, error }, new JsonSerializerOptions { WriteIndented = true }));
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(error == null ? 0 : 1);
    }
}
#endif
