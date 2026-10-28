using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiquidGlassAvaloniaUI;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Pointer = Avalonia.Input.Pointer;

// Native renderer, isolated data, no debugger. Frame timings are callback latency,
// not physical display presentation latency. Keep cold and warmed samples separate.
internal static class InteractionPerformance
{
    public static async Task RunAsync(string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var records = new List<object>();
        try
        {
            var window = (MainWindow)desktop.MainWindow!;
            var model = (MainWindowViewModel)window.DataContext!;
            var root = Environment.GetEnvironmentVariable("MACEXPLORER_TEST_ROOT")!;
            var directory = Directory.CreateDirectory(Path.Combine(root, "interaction-data")).FullName;
            for (var i = 0; i < 100; i++) await File.WriteAllTextAsync(Path.Combine(directory, $"sample-{i:D3}.txt"), "test");
            await model.FileList.NavigateToAsync(directory);
            var tags = MacExplorer.App.Services.GetRequiredService<IFileTagService>();
            var tag = await tags.CreateTagAsync("Interaction fixture");
            await tags.SetTagAsync([Path.Combine(directory, "sample-000.txt")], tag, true);
            var tabs = window.FindControl<ListBox>("TabList")!;
            for (var i = 0; i < 3; i++) await window.OpenTabToRightAsync(model.SelectedTab!);
            var fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var surface = window.FindControl<ChromiumTabStripSurface>("TabSurface")!;
            var silhouette = (TabSilhouetteCache)typeof(ChromiumTabStripSurface).GetField("_silhouette", fields)!.GetValue(surface)!;
            using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);

            async Task Frame()
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => ready.TrySetResult()));
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            async Task Measure(string name, int iteration, Func<Task> action)
            {
                var layouts = 0;
                void Layout(object? sender, EventArgs e) => layouts++;
                window.LayoutUpdated += Layout;
                LiquidGlassDiagnostics.Reset();
                var bitmaps = silhouette.RenderCount;
                var allocated = GC.GetTotalAllocatedBytes(false);
                var start = Stopwatch.GetTimestamp();
                await action();
                var actionMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                await Frame();
                var callbackMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                window.LayoutUpdated -= Layout;
                var glass = LiquidGlassDiagnostics.Snapshot;
                var record = new { name, iteration, theme = window.ActualThemeVariant.ToString(), actionMs, callbackMs,
                    layouts, tabBitmaps = silhouette.RenderCount - bitmaps, allocated = GC.GetTotalAllocatedBytes(false) - allocated,
                    glass.CapturesStarted, glass.CapturesPublished, glass.FullBitmapCopyBytes };
                records.Add(record);
                Console.WriteLine(JsonSerializer.Serialize(record));
            }

            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = theme;
                await Task.Delay(300);
                for (var i = 0; i < 5; i++)
                {
                    var anchor = model.Tabs[i % model.Tabs.Count];
                    await Measure("tab-switch", i, () => { model.SelectedTab = anchor; return Task.CompletedTask; });
                    await Measure("tab-context", i, () =>
                    {
                        window.ShowTabContextMenu((Control)tabs.ContainerFromItem(anchor)!, anchor);
                        return Task.CompletedTask;
                    });
                    ((ContextMenu?)typeof(MainWindow).GetField("_tabContextMenu", fields)!.GetValue(window))?.Close();
                    await Frame();
                    var toolbar = window.GetVisualDescendants().OfType<TagToolbar>().First(t => t.IsEffectivelyVisible);
                    await Measure("tag-context", i, () =>
                    {
                        typeof(TagToolbar).GetMethod("OpenActions", fields)!.Invoke(toolbar, [tag]);
                        return Task.CompletedTask;
                    });
                    toolbar.CloseDropdown();
                    await Frame();
                    var fileView = window.GetVisualDescendants().OfType<FileListView>().First(v => v.IsEffectivelyVisible);
                    var list = fileView.GetVisualDescendants().OfType<FastFileList>().Single();
                    var entry = model.FileList.Entries[i];
                    await Measure("file-select", i, () => { model.FileList.SelectEntry(entry); return Task.CompletedTask; });
                    await Measure("file-context", i, () => (Task)typeof(FileListView).GetMethod("ShowMenuAsync", fields)!.Invoke(fileView, [list, entry])!);
                    fileView.DismissContextMenu();
                    await Frame();
                    await Measure("file-context-complete", i, async () =>
                    {
                        // Both builds publish initial actions and then complete actions.
                        // Await the second publication so deferred work cannot escape the sample.
                        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var publications = 0;
                        var fileList = model.FileList;
                        void Published(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                        {
                            if (e.PropertyName == nameof(FileListViewModel.ContextMenuActions) && ++publications == 2)
                                completed.TrySetResult();
                        }
                        fileList.PropertyChanged += Published;
                        try
                        {
                            await (Task)typeof(FileListView).GetMethod("ShowMenuAsync", fields)!.Invoke(fileView, [list, entry])!;
                            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        }
                        finally { fileList.PropertyChanged -= Published; }
                    });
                    fileView.DismissContextMenu();
                    await Frame();
                    var header = (Control)tabs.ContainerFromItem(anchor)!;
                    await Measure("pointer-dispatch", i, () =>
                    {
                        var point = header.TranslatePoint(new Point(40, 18), window)!.Value;
                        header.RaiseEvent(new PointerPressedEventArgs(header, pointer, window, point, 0,
                            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
                        header.RaiseEvent(new PointerReleasedEventArgs(header, pointer, window, point, 1,
                            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                        return Task.CompletedTask;
                    });
                }
            }
            for (var i = 0; i < 3; i++)
                await Measure("new-tab", i, () => window.OpenTabToRightAsync(model.SelectedTab!));
            var dragged = (Control)tabs.ContainerFromItem(model.Tabs[0])!;
            var origin = dragged.TranslatePoint(new Point(40, 18), window)!.Value;
            dragged.RaiseEvent(new PointerPressedEventArgs(dragged, pointer, window, origin, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            for (var step = 1; step <= 30; step++)
            {
                var point = origin + new Vector(step * 20, 0);
                await Measure("tab-drag", step, () =>
                {
                    tabs.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, tabs, pointer, window, point, 0,
                        new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
                    return Task.CompletedTask;
                });
            }
            await Measure("tab-drop", 0, () =>
            {
                tabs.RaiseEvent(new PointerReleasedEventArgs(tabs, pointer, window, origin + new Vector(600, 0), 1,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                return Task.CompletedTask;
            });
            await File.WriteAllTextAsync(Path.Combine(output, "interaction-results.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            desktop.Shutdown();
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "interaction-error.txt"), ex.ToString());
            Console.Error.WriteLine(ex);
            desktop.Shutdown(1);
        }
    }
}
