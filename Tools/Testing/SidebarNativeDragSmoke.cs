#if DEBUG
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class SidebarNativeDragSmoke
{
    internal static async Task RunAsync(MainWindow owner)
    {
        var output = Path.Combine(RuntimePaths.TestRoot!, "sidebar-native-drag");
        Directory.CreateDirectory(output);
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var checks = new List<string>();
        var phases = new List<FileDragSessionEvent>();
        string? error = null;
        try
        {
            if (!CGPreflightPostEventAccess()) throw new InvalidOperationException("Native mouse event access is unavailable.");
            var delivery = App.Services.GetRequiredService<FileDeliveryService>();
            delivery.Enabled = true;
            FolderTabRestoreSmoke.ActivateApplication();
            owner.Width = 1280; owner.Height = 900;
            owner.ShowAndActivate();
            var fixture = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "Documents", "NativeDrag")).FullName;
            var folders = new[] { "Project A", "Project B", "Project C", "Cancel" }.Select(name =>
                Directory.CreateDirectory(Path.Combine(fixture, name)).FullName).ToArray();
            foreach (var path in folders) await File.WriteAllTextAsync(Path.Combine(path, "original.txt"), "preserve original");
            var store = App.Services.GetRequiredService<IPinnedFolderService>();
            var id = await store.CreateGroupAsync("Native drag regression");
            var vm = ((MainWindowViewModel)owner.DataContext!).FileList;
            vm.SetViewMode(ViewMode.List);
            await vm.NavigateToAsync(fixture);
            await WaitUntil(() => vm.CustomGroups.Count == 1 && vm.Entries.Count == 4);
            var view = owner.GetVisualDescendants().OfType<FileListView>().Single(item =>
                item.IsEffectivelyVisible && ReferenceEquals(item.DataContext, vm));
            var list = view.FindControl<FastFileList>("FastList")!;
            var sidebar = owner.GetVisualDescendants().OfType<FinderSidebarView>().Single(item => item.IsEffectivelyVisible);
            view.DragSessionChanged += phases.Add;
            FileDeliveryDropWindow? originalDropWindow = null;
            for (var iteration = 0; iteration < folders.Length; iteration++)
            {
                owner.RequestedThemeVariant = iteration % 2 == 0 ? ThemeVariant.Light : ThemeVariant.Dark;
                owner.UpdateLayout();
                await Task.Delay(150);
                var group = sidebar.GetVisualDescendants().OfType<StackPanel>().Single(panel =>
                    panel.Classes.Contains("pinned-folder-group") && panel.DataContext is PinnedFolderGroup item && item.Id == id);
                var header = group.Children.OfType<Grid>().Single();
                var row = list.RowBounds(list.IndexOfPath(folders[iteration]));
                var from = list.PointToScreen(new Point(65, row.Center.Y));
                var to = header.PointToScreen(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2));
                await File.WriteAllTextAsync(Path.Combine(output, "progress.json"), JsonSerializer.Serialize(new { iteration, from, to }));
                var cancel = iteration == folders.Length - 1;
                await Task.Run(() => Drag(from, to, cancel));
                await WaitUntil(() => phases.Count(item => item.Phase == FileDragPhase.Ended) == iteration + 1);
                if (!cancel) await WaitUntil(() => vm.CustomGroups.Single().Folders.Count == iteration + 1);
                var dropWindow = lifetime.Windows.OfType<FileDeliveryDropWindow>().Single();
                originalDropWindow ??= dropWindow;
                Check(ReferenceEquals(dropWindow, originalDropWindow), "Drag " + iteration + ": reuses the delivery drop window");
                await WaitUntil(() => !dropWindow.IsVisible);
                Check(phases.Count(item => item.Phase == FileDragPhase.Started) == iteration + 1,
                    "Drag " + iteration + ": real AppKit dragging session started and ended");
                Check(Directory.Exists(folders[iteration]) && await File.ReadAllTextAsync(Path.Combine(folders[iteration], "original.txt")) == "preserve original",
                    "Drag " + iteration + ": original folder and content preserved");
                if (iteration == 0) vm.ToggleCustomGroupCollapsed(id);
            }
            Check(vm.CustomGroups.Single().Folders.Count == 3 && !await store.IsPinnedAsync(folders[3], id), "Escape cancels without adding a shortcut");
            Check(delivery.StagedPaths.Count == 0, "Dropping on the sidebar does not add delivery staging items");
            Check(vm.CustomGroups.Single().IsCollapsed, "Pinning on a collapsed header preserves its collapsed state");
            await Capture("collapsed");
            var finalHeader = sidebar.GetVisualDescendants().OfType<Grid>().Single(grid =>
                grid.Classes.Contains("custom-group-header") && grid.DataContext is PinnedFolderGroup group && group.Id == id);
            var click = finalHeader.PointToScreen(new Point(finalHeader.Bounds.Width / 2, finalHeader.Bounds.Height / 2));
            await Task.Run(() =>
            {
                PostMouse(5, click.X, click.Y);
                PostMouse(1, click.X, click.Y);
                Thread.Sleep(50);
                PostMouse(2, click.X, click.Y);
            });
            await WaitUntil(() => !vm.CustomGroups.Single().IsCollapsed);
            Check(!vm.CustomGroups.Single().IsCollapsed, "Clicking the custom header expands its pinned folders");
            await Capture("completed");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(
                new { success = error == null, checks, phases, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Sidebar native drag smoke: {output}; success={error == null}");
            lifetime.Shutdown(error == null ? 0 : 1);
        }
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        async Task Capture(string name)
        {
            owner.UpdateLayout();
            var visual = ElementComposition.GetElementVisual(owner)!;
            await visual.Compositor.RequestCommitAsync();
            using var bitmap = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
    }

    private static void Drag(PixelPoint from, PixelPoint to, bool cancel)
    {
        PostMouse(5, from.X, from.Y);
        PostMouse(1, from.X, from.Y);
        Thread.Sleep(100);
        try
        {
            for (var step = 1; step <= 60; step++)
            {
                PostMouse(6, from.X + (to.X - from.X) * step / 60d, from.Y + (to.Y - from.Y) * step / 60d);
                Thread.Sleep(16);
            }
            Thread.Sleep(150);
            if (cancel)
            {
                foreach (var down in new[] { true, false })
                {
                    var key = CGEventCreateKeyboardEvent(IntPtr.Zero, 53, down);
                    try { CGEventPostToPid(Environment.ProcessId, key); } finally { CFRelease(key); }
                    Thread.Sleep(200);
                }
                Thread.Sleep(100);
            }
        }
        finally { PostMouse(2, to.X, to.Y); }
    }
    private static void PostMouse(int type, double x, double y)
    {
        var ev = CGEventCreateMouseEvent(IntPtr.Zero, type, new NativePoint(x, y), 0);
        try { CGEventPost(0, ev); } finally { CFRelease(ev); }
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Native sidebar drag did not complete.");
            await Task.Delay(20);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(double X, double Y);
    private const string Graphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport(Graphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightPostEventAccess();
    [DllImport(Graphics)] private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, int type, NativePoint position, int button);
    [DllImport(Graphics)] private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort key, [MarshalAs(UnmanagedType.I1)] bool down);
    [DllImport(Graphics)] private static extern void CGEventPost(int tap, IntPtr ev);
    [DllImport(Graphics)] private static extern void CGEventPostToPid(int pid, IntPtr ev);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr value);
}
#endif
