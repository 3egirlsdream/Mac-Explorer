#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class TagClipboardSmoke
{
    public static async Task RunAsync(MainWindow owner)
    {
        var root = RuntimePaths.TestRoot ?? throw new InvalidOperationException("An isolated profile is required.");
        var output = Directory.CreateDirectory(Path.Combine(root, "tag-clipboard")).FullName;
        var checks = new List<string>();
        string? error = null;
        try
        {
            using var scope = App.Services.CreateScope();
            var services = scope.ServiceProvider;
            var boardName = "MacExplorer.TagClipboardSmoke." + Guid.NewGuid().ToString("N");
            var clipboard = new MacClipboardService(boardName);
            var operations = ActivatorUtilities.CreateInstance<FileOpsViewModel>(services, clipboard);
            using var vm = ActivatorUtilities.CreateInstance<FileListViewModel>(services, clipboard, operations);
            ((MainWindowViewModel)owner.DataContext!).AddTab(vm, select: true);
            var tags = services.GetRequiredService<IFileTagService>();
            var store = new MacFileTagStore();
            var fixture = Directory.CreateDirectory(Path.Combine(root, "Documents", "TagClipboardFixture")).FullName;
            var parent = Directory.CreateDirectory(Path.Combine(fixture, "B folder")).FullName;
            var destination = Directory.CreateDirectory(Path.Combine(parent, "child")).FullName;
            var a = await tags.CreateTagAsync("Smoke A");
            var b = await tags.CreateTagAsync("Smoke B");
            var c = await tags.CreateTagAsync("Smoke C");
            await tags.SetTagAsync([parent], b, true);
            foreach (var (cut, tagTarget) in new[] { (false, true), (true, true), (false, false), (true, false) })
            {
                var name = $"{(cut ? "cut" : "copy")}-{(tagTarget ? "tag" : "folder")}.txt";
                var source = Path.Combine(fixture, name);
                await File.WriteAllTextAsync(source, "original contents");
                await tags.SetTagAsync([source], a, true);
                await tags.SetTagAsync([source], c, true);
                await vm.NavigateToTagAsync(a);
                await WaitForAsync(() => vm.Entries.Any(entry => entry.FullPath == source));
                vm.SetSelection([vm.Entries.Single(entry => entry.FullPath == source)]);
                if (cut) vm.CutSelected(); else vm.CopySelected();
                if (tagTarget) await vm.NavigateToTagAsync(b); else await vm.NavigateToAsync(destination);
                await vm.PasteAsync();
                var result = tagTarget ? source : Path.Combine(destination, name);
                var expected = tagTarget ? (cut ? new[] { b.Name, c.Name } : new[] { a.Name, b.Name, c.Name })
                    : (cut ? new[] { c.Name } : new[] { a.Name, c.Name });
                Require((await store.ReadAsync(result)).Select(tag => tag.Name).Order().SequenceEqual(expected), name + " tags");
                Require(await File.ReadAllTextAsync(result) == "original contents", name + " contents");
                if (cut && !tagTarget) Require(!File.Exists(source), name + " source moved");
                if (!cut && !tagTarget)
                {
                    await tags.SetTagAsync([result], a, false);
                    await vm.NavigateToTagAsync(a);
                    await vm.RefreshAsync();
                    Require(!vm.Entries.Any(entry => entry.FullPath == result), "removed copy stays out of A");
                }
            }
            var finderFiles = new[] { Path.Combine(fixture, "Finder 中文 #.txt"), Path.Combine(fixture, "Finder second.txt") };
            foreach (var path in finderFiles) await File.WriteAllTextAsync(path, "Finder reference contents");
            await WriteFinderReferencesAsync(boardName, finderFiles);
            Require(clipboard.GetPasteKind() == ClipboardPasteKind.ExternalFiles, "Finder reference classification");
            await vm.NavigateToTagAsync(b);
            await vm.PasteAsync();
            foreach (var path in finderFiles)
            {
                Require((await store.ReadAsync(path)).Any(tag => tag.Name == b.Name), "Finder reference tag added");
                Require(await File.ReadAllTextAsync(path) == "Finder reference contents", "Finder source unchanged");
            }
            Require(clipboard.GetClipboardEntry() is { Operation: ClipboardOperation.Copy, SourceTag: null }, "Finder adoption clears cut provenance");
            var adoptedPaths = clipboard.GetClipboardEntry()!.SourcePaths.ToArray();
            Require(adoptedPaths.Length == finderFiles.Length, "Finder multi-file adoption");
            await WaitForAsync(() => adoptedPaths.All(path => vm.Entries.Any(entry => entry.FullPath == path)));
            owner.UpdateLayout();
            var visual = ElementComposition.GetElementVisual(owner)!;
            await visual.Compositor.RequestCommitAsync();
            using var bitmap = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
            bitmap.Save(Path.Combine(output, "tag-b.png"));
            vm.SetSelection(vm.Entries.Where(entry => adoptedPaths.Contains(entry.FullPath)).ToArray());
            var menu = await vm.LoadCompleteFileContextMenuAsync(vm.SelectedEntries[0]);
            await menu.Single(action => action.ShortcutId == ShortcutIds.Trash).Execute!();
            Require(!vm.IsDeleteConfirmDialogVisible, "tag removal skips file trash confirmation");
            Require(!vm.Entries.Any(entry => adoptedPaths.Contains(entry.FullPath)), "context menu removes selected files from tag list");
            foreach (var path in finderFiles)
            {
                Require(!(await store.ReadAsync(path)).Any(tag => tag.Name == b.Name), "context menu removes native tag");
                Require(await File.ReadAllTextAsync(path) == "Finder reference contents", "context menu preserves original file");
            }
            var copyPath = Path.Combine(fixture, "copy-tag.txt");
            vm.SetSelection([vm.Entries.Single(entry => entry.FullPath == copyPath)]);
            var toolbar = owner.GetVisualDescendants().OfType<FinderToolbar>().Single(view => ReferenceEquals(view.DataContext, vm));
            toolbar.FindControl<Button>("DeleteButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => !vm.Entries.Any(entry => entry.FullPath == copyPath));
            Require((await store.ReadAsync(copyPath)).Select(tag => tag.Name).Order().SequenceEqual(new[] { a.Name, c.Name }), "toolbar preserves other tags");
            Require(await File.ReadAllTextAsync(copyPath) == "original contents", "toolbar preserves original file");
            var cutPath = Path.Combine(fixture, "cut-tag.txt");
            var fileList = owner.GetVisualDescendants().OfType<FileListView>().Single(view => ReferenceEquals(view.DataContext, vm));
            await WaitForAsync(() => !vm.IsLoading && !vm.IsDirectoryLoading && !fileList.FindControl<FastFileList>("FastList")!.IsLoading);
            await visual.Compositor.RequestCommitAsync();
            vm.SetSelection([vm.Entries.Single(entry => entry.FullPath == cutPath)]);
            Require(fileList.TryHandleFileShortcut(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete }), "Delete shortcut handled");
            await WaitForAsync(() => !vm.Entries.Any(entry => entry.FullPath == cutPath));
            Require((await store.ReadAsync(cutPath)).Select(tag => tag.Name).SequenceEqual(new[] { c.Name }), "Delete shortcut preserves other tags");
            Require(await File.ReadAllTextAsync(cutPath) == "original contents", "Delete shortcut preserves original file");
            await vm.NavigateToAsync(fixture);
            vm.SetSelection([vm.Entries.Single(entry => entry.FullPath == copyPath)]);
            await vm.RequestDeleteSelectedAsync();
            Require(vm.IsDeleteConfirmDialogVisible, "ordinary folder retains file trash confirmation");
            vm.CancelDeleteConfirmDialog();
            await vm.NavigateToTagAsync(b);
            await vm.RefreshAsync();
            Require(vm.Entries.Count == 1 && vm.Entries[0].FullPath == parent, "removed files stay absent after returning to tag");
            owner.UpdateLayout();
            await visual.Compositor.RequestCommitAsync();
            using var afterRemoval = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
            afterRemoval.Save(Path.Combine(output, "tag-b-after-remove.png"));
        }
        catch (Exception ex) { error = ex.ToString(); }
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { success = error == null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Tag clipboard smoke: {output}; success={error == null}");
        ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Shutdown(error == null ? 0 : 1);

        void Require(bool passed, string check)
        {
            if (!passed) throw new InvalidOperationException(check);
            checks.Add(check);
        }
    }

    private static async Task WaitForAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }

    private static async Task WriteFinderReferencesAsync(string boardName, string[] paths)
    {
        const string script = """
            import AppKit
            let board = NSPasteboard(name: NSPasteboard.Name(CommandLine.arguments[1]))
            board.clearContents()
            let items = CommandLine.arguments.dropFirst(2).map { path -> NSPasteboardItem in
                let file = NSURL(fileURLWithPath: path)
                let reference = file.perform(NSSelectorFromString("fileReferenceURL"))!.takeUnretainedValue() as! NSURL
                let item = NSPasteboardItem()
                item.setString(reference.absoluteString!, forType: .fileURL)
                return item
            }
            if !board.writeObjects(items) { exit(1) }
            """;
        var start = new ProcessStartInfo("/usr/bin/swift") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-e", script, boardName }.Concat(paths)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await output;
        var failure = await error;
        if (process.ExitCode != 0) throw new IOException(failure);
    }
}
#endif
