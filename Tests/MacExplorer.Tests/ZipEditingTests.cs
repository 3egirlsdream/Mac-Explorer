using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Avalonia.Threading;
using MacExplorer.Controls;
using Avalonia.Headless.XUnit;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Markdown;
using MacExplorer.Views;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Area", "Archive")]
public sealed class ZipEditingTests : FileListViewModelTestBase
{
    [Fact]
    [Trait("Kind", "Unit")]
    public async Task StreamsTextAndBatchesChangesInTheCurrentFolder()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Directory.CreateTempSubdirectory("fkfinder-zip-edit-");
        try
        {
            var path = Path.Combine(root.FullName, "资料.zip");
            var initialText = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("初始\r\n")).ToArray();
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var text = zip.CreateEntry("docs/README").Open()) text.Write(initialText);
                using (var untouched = zip.CreateEntry("docs/untouched.bin").Open()) untouched.Write([1, 2, 3]);
                zip.Comment = "目录测试";
            }
            Assert.NotNull(ZipMutationService.Probe(path));
            var entryPath = ArchivePathHelper.Build(path, "docs/README");
            var document = await MarkdownDocument.OpenArchiveAsync(entryPath, (hash, data, token) =>
                ZipMutationService.ReplaceTextAsync(path, "docs/README", hash, data, 1024, token, null), default);
            Assert.Equal("README", document.Name);
            Assert.Equal("\r\n", document.NewLine);
            await document.SaveAsync("更新\r\n");

            var source = Directory.CreateDirectory(Path.Combine(root.FullName, "导入"));
            await File.WriteAllTextAsync(Path.Combine(source.FullName, "数据.json"), "{}");
            Directory.CreateDirectory(Path.Combine(source.FullName, "空目录"));
            Assert.True(await ZipMutationService.AddAsync(path, "docs/", [source.FullName], null, default, null));
            Assert.Equal("docs/new.txt", await ZipMutationService.CreateAsync(path, "docs/", "new.txt", null, false, default, null));
            Assert.Equal("docs/new 2.txt", await ZipMutationService.CreateAsync(path, "docs/", "new.txt", null, false, default, null));
            var renamed = await ZipMutationService.RenameAsync(path, "docs/导入/", "附件",
                Assert.IsType<ZipRevision>(ZipMutationService.Probe(path)), default, null);
            Assert.Equal("docs/附件/", renamed);
            await ZipMutationService.DeleteAsync(path, ["docs/new.txt", "docs/附件/空目录/"],
                Assert.IsType<ZipRevision>(ZipMutationService.Probe(path)), default, null);

            using var result = ZipFile.OpenRead(path);
            Assert.Equal("目录测试", result.Comment);
            Assert.Equal("更新\r\n", new StreamReader(result.GetEntry("docs/README")!.Open()).ReadToEnd());
            Assert.Equal("{}", new StreamReader(result.GetEntry("docs/附件/数据.json")!.Open()).ReadToEnd());
            Assert.NotNull(result.GetEntry("docs/new 2.txt"));
            Assert.Null(result.GetEntry("docs/new.txt"));
            Assert.Null(result.GetEntry("docs/附件/空目录/"));
            using var unchanged = new MemoryStream();
            using var other = result.GetEntry("docs/untouched.bin")!.Open();
            await other.CopyToAsync(unchanged);
            Assert.Equal(new byte[] { 1, 2, 3 }, unchanged.ToArray());
            Assert.True(File.Exists(Path.Combine(source.FullName, "数据.json")));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task RejectsStaleTextAndDeleteAndKeepsAValidEmptyZip()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Directory.CreateTempSubdirectory("fkfinder-zip-conflict-");
        try
        {
            var path = Path.Combine(root.FullName, "archive.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var text = zip.CreateEntry("text.txt").Open()) text.Write("before"u8);
            var before = Assert.IsType<ZipRevision>(ZipMutationService.Probe(path));
            await ZipMutationService.ReplaceTextAsync(path, "text.txt", SHA256.HashData("before"u8), "after"u8.ToArray(), 1024, default, null);
            var published = File.ReadAllBytes(path);
            await Assert.ThrowsAsync<IOException>(() => ZipMutationService.ReplaceTextAsync(path, "text.txt",
                SHA256.HashData("before"u8), "stale"u8.ToArray(), 1024, default, null));
            await Assert.ThrowsAsync<IOException>(() => ZipMutationService.DeleteAsync(path, ["text.txt"], before, default, null));
            Assert.Equal(published, File.ReadAllBytes(path));
            Assert.DoesNotContain(root.EnumerateFileSystemInfos(), item => item.Name.Contains(".fkfinder-zip-", StringComparison.Ordinal));

            await ZipMutationService.DeleteAsync(path, ["text.txt"], Assert.IsType<ZipRevision>(ZipMutationService.Probe(path)), default, null);
            using (var zip = ZipFile.OpenRead(path)) Assert.Empty(zip.Entries);
            Assert.NotNull(ZipMutationService.Probe(path));
            Assert.Empty(await new ArchiveService().GetArchiveContentsAsync(path));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task CancellingDuringZipWritePreservesTheOriginalBytes()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Directory.CreateTempSubdirectory("fkfinder-zip-cancel-");
        try
        {
            var path = Path.Combine(root.FullName, "original.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var first = zip.CreateEntry("first.txt").Open()) first.Write("before"u8);
                using var large = zip.CreateEntry("large.bin", System.IO.Compression.CompressionLevel.NoCompression).Open();
                large.Write(new byte[8 * 1024 * 1024]);
            }
            var before = SHA256.HashData(File.ReadAllBytes(path));
            using var cancel = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ZipMutationService.ReplaceTextAsync(
                path, "first.txt", SHA256.HashData("before"u8), "new content"u8.ToArray(), 1024, cancel.Token,
                progress => { if (progress > 0.1) cancel.Cancel(); }));
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
            Assert.DoesNotContain(root.EnumerateFileSystemInfos(), item => item.Name.Contains(".fkfinder-zip-", StringComparison.Ordinal));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task ZipContainingAnEncryptedEntryStaysReadOnly()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Directory.CreateTempSubdirectory("fkfinder-zip-encrypted-");
        try
        {
            var path = Path.Combine(root.FullName, "mixed.zip");
            using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipOutputStream(File.Create(path)))
            {
                zip.PutNextEntry(new ICSharpCode.SharpZipLib.Zip.ZipEntry("plain.txt"));
                zip.Write("plain"u8.ToArray());
                zip.CloseEntry();
                zip.Password = "synthetic-test-password";
                zip.PutNextEntry(new ICSharpCode.SharpZipLib.Zip.ZipEntry("encrypted.txt"));
                zip.Write("encrypted"u8.ToArray());
            }
            var before = File.ReadAllBytes(path);
            Assert.Null(ZipMutationService.Probe(path));
            await Assert.ThrowsAsync<IOException>(() =>
                ZipMutationService.CreateAsync(path, "", "new.txt", null, false, default, null));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { root.Delete(recursive: true); }
    }

    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
    public async Task ZipCopyPasteAndDeleteUseTheNormalToolbarAndClipboard()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires the macOS clipboard and ZIP writer.");
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("fkfinder-zip-clipboard-");
        var clipboardName = "MacExplorer.ArchiveClipboardTest." + Guid.NewGuid().ToString("N");
        var clipboard = new MacClipboardService(clipboardName);
        string? staging = null;
        try
        {
            var path = Path.Combine(root.FullName, "archive.zip");
            var target = Directory.CreateDirectory(Path.Combine(root.FullName, "target")).FullName;
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var text = zip.CreateEntry("note.txt").Open()) text.Write("note"u8);
                using (var child = zip.CreateEntry("folder/nested.txt").Open()) child.Write("nested"u8);
                zip.CreateEntry("destination/");
            }

            using var vm = CreateViewModel(new MacFileService(), archiveService: new ArchiveService(), clipboardService: clipboard);
            await vm.OpenEntryAsync(new FileSystemEntry { Name = "archive.zip", FullPath = path });
            var note = Assert.Single(vm.Entries, entry => entry.Name == "note.txt");
            var folder = Assert.Single(vm.Entries, entry => entry.Name == "folder");
            vm.SelectEntry(note, true);
            vm.SelectEntry(folder, true);
            var toolbar = new FinderToolbar { DataContext = vm };
            Assert.True(toolbar.FindControl<Button>("CopyButton")!.IsEnabled);
            Assert.True(toolbar.FindControl<Button>("DeleteButton")!.IsEnabled);
            Assert.False(toolbar.FindControl<Button>("PasteButton")!.IsEnabled);
            await vm.ShowFileContextMenuAsync(note, 0, 0);
            Assert.Contains(vm.ContextMenuActions, action => action.IsQuickAction && action.ShortcutId == ShortcutIds.Copy);
            Assert.Contains(vm.ContextMenuActions, action => action.IsQuickAction && action.ShortcutId == ShortcutIds.Paste);
            Assert.Contains(vm.ContextMenuActions, action => action.IsQuickAction && action.ShortcutId == ShortcutIds.Trash);
            var copy = Assert.Single(vm.ContextMenuActions, action => action.ShortcutId == ShortcutIds.Copy);
            await copy.Execute!();
            var reader = new MacClipboardService(clipboardName);
            Assert.Equal(ClipboardPasteKind.ExternalFiles, reader.GetPasteKind());
            Assert.True(reader.TryAdoptExternalFiles());
            var staged = reader.GetClipboardEntry()!.SourcePaths;
            staging = Path.GetDirectoryName(staged[0]);
            Assert.Equal("note", await File.ReadAllTextAsync(Assert.Single(staged, item => Path.GetFileName(item) == "note.txt")));
            Dispatcher.UIThread.RunJobs();
            Assert.True(toolbar.FindControl<Button>("PasteButton")!.IsEnabled);

            await vm.OpenEntryAsync(Assert.Single(vm.Entries, entry => entry.Name == "destination"));
            await vm.PasteAsync();
            using (var zip = ZipFile.OpenRead(path))
            {
                Assert.NotNull(zip.GetEntry("note.txt"));
                Assert.NotNull(zip.GetEntry("folder/nested.txt"));
                Assert.NotNull(zip.GetEntry("destination/note.txt"));
                Assert.NotNull(zip.GetEntry("destination/folder/nested.txt"));
            }
            await vm.NavigateToAsync(target);
            await vm.PasteAsync();
            Assert.Equal("note", await File.ReadAllTextAsync(Path.Combine(target, "note.txt")));
            Assert.Equal("nested", await File.ReadAllTextAsync(Path.Combine(target, "folder", "nested.txt")));
        }
        finally
        {
            if (staging != null) Directory.Delete(staging, recursive: true);
            root.Delete(recursive: true);
        }
    }

    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
    public async Task FileListContextMenuPasteAndExternalDropUseTheZipDirectory()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("fkfinder-zip-drop-");
        try
        {
            var path = Path.Combine(root.FullName, "archive.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var text = zip.CreateEntry("README").Open()) text.Write("before"u8);
                zip.CreateEntry("docs/");
            }
            var clipboard = new TagTestClipboard();
            using var vm = CreateViewModel(new FakeFileService(root.FullName), archiveService: new ArchiveService(), clipboardService: clipboard);
            await vm.OpenEntryAsync(new FileSystemEntry { Name = "archive.zip", FullPath = path });
            vm.SetViewMode(ViewMode.List);
            var view = new FileListView { DataContext = vm };
            var window = new Window { Width = 900, Height = 600, Content = view };
            window.Show();
            try
            {
                RenderIssue11(window);
                var list = view.FindControl<FastFileList>("FastList")!;
                var textIndex = list.IndexOfPath(ArchivePathHelper.Build(path, "README"));
                var row = list.RowBounds(textIndex);
                var point = list.TranslatePoint(new Point(45, row.Center.Y), window)!.Value;
                window.MouseDown(point, MouseButton.Right);
                window.MouseUp(point, MouseButton.Right);
                Dispatcher.UIThread.RunJobs();
                var menu = Assert.Single(window.GetVisualDescendants().OfType<ContextMenu>());
                Assert.Contains(menu.GetVisualDescendants().OfType<Button>(),
                    button => Avalonia.Automation.AutomationProperties.GetName(button) == LocalizationText.Get("编辑"));
                menu.Close();

                var paste = Path.Combine(root.FullName, "paste.txt");
                await File.WriteAllTextAsync(paste, "pasted");
                clipboard.CopyFiles([paste]);
                await vm.PasteAsync();
                Assert.Contains(vm.Entries, item => item.Name == "paste.txt");
                Assert.True(File.Exists(paste));

                RenderIssue11(window);
                var dropSource = Path.Combine(root.FullName, "drag.txt");
                await File.WriteAllTextAsync(dropSource, "dropped");
                var surface = view.FindControl<Grid>("InteractionSurface")!;
                var folder = Assert.Single(vm.Entries, entry => entry.Name == "docs");
                var target = list.TranslatePoint(list.RowBounds(list.IndexOfPath(folder.FullPath)).Center, surface)!.Value;
                using var data = new DataTransfer();
                data.Add(DataTransferItem.CreateFile((await window.StorageProvider.TryGetFileFromPathAsync(new Uri(dropSource)))!));
                var over = new DragEventArgs(DragDrop.DragOverEvent, data, surface, target, KeyModifiers.None)
                    { DragEffects = DragDropEffects.Copy | DragDropEffects.Move };
                surface.RaiseEvent(over);
                Assert.Equal(DragDropEffects.Copy, over.DragEffects);
                var drop = new DragEventArgs(DragDrop.DropEvent, data, surface, target, KeyModifiers.None)
                    { DragEffects = DragDropEffects.Copy };
                surface.RaiseEvent(drop);
                await WaitForIssue11Async(() =>
                {
                    using var zip = ZipFile.OpenRead(path);
                    return zip.GetEntry("docs/drag.txt") != null;
                });
                Assert.Equal("dropped", await File.ReadAllTextAsync(dropSource));
            }
            finally { window.Close(); }
        }
        finally { root.Delete(recursive: true); }
    }

    [AvaloniaFact(Explicit = true)]
    [Trait("Kind", "UI")]
    public async Task FileListWritesToZipAndPlainTextEditorUsesItsFileName()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var theme = new FastListTestTheme();
        var root = Directory.CreateTempSubdirectory("fkfinder-zip-ui-");
        try
        {
            var path = Path.Combine(root.FullName, "archive.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var text = zip.CreateEntry("README").Open()) text.Write("before"u8);

            using var vm = CreateViewModel(new FakeFileService(root.FullName), archiveService: new ArchiveService());
            await vm.OpenEntryAsync(new FileSystemEntry { Name = "archive.zip", FullPath = path });
            Assert.True(vm.CanModifyArchive);
            var entry = Assert.Single(vm.Entries);
            using var editor = new MarkdownEditorView();
            await editor.OpenAsync(entry.FullPath, Assert.IsType<Func<byte[], byte[], CancellationToken, Task>>(vm.GetArchiveTextWriter(entry)));
            Assert.Equal("README", editor.FindControl<TextBlock>("TitleText")!.Text);
            Assert.False(editor.FindControl<Border>("MarkdownToolbar")!.IsVisible);
            editor.FindControl<AvaloniaEdit.TextEditor>("Editor")!.Text = "after";
            Assert.True(await editor.SaveAsync());
            Assert.Equal("after", Encoding.UTF8.GetString(await ZipMutationService.ReadTextAsync(path, "README", 1024, default)));

            await vm.CreateNewFileAsync(".txt");
            Assert.Contains(vm.Entries, file => file.Name.EndsWith(".txt", StringComparison.Ordinal));
            vm.SelectedEntries.Clear();
            vm.SelectedEntries.Add(Assert.Single(vm.Entries, file => file.Name == "README"));
            await vm.RequestDeleteSelectedAsync();
            Assert.True(vm.DeleteConfirmIncludesArchiveEntries);
            await vm.ConfirmDeleteSelectedAsync();
            Assert.DoesNotContain(vm.Entries, file => file.Name == "README");

            var localPath = Path.Combine(root.FullName, "normal.txt");
            await File.WriteAllTextAsync(localPath, "local");
            using var localEditor = new MarkdownEditorView();
            await localEditor.OpenAsync(localPath);
            localEditor.FindControl<AvaloniaEdit.TextEditor>("Editor")!.Text = "changed";
            Assert.True(await localEditor.SaveAsync());
            Assert.Equal("changed", await File.ReadAllTextAsync(localPath));
        }
        finally { root.Delete(recursive: true); }
    }
}
