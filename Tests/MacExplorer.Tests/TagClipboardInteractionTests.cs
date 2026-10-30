using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "UnifiedTagInteraction")]
public sealed class TagClipboardInteractionTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyAddsTargetAndCutReplacesOnlySourceWithoutMovingFiles(bool cut)
    {
        var root = Directory.CreateTempSubdirectory("tag-paste-");
        try
        {
            var path = Path.Combine(root.FullName, "file.txt");
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "folder")).FullName;
            await File.WriteAllTextAsync(path, "original");
            using var tags = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root.FullName, "tags.db")));
            var a = await tags.CreateTagAsync("A");
            var b = await tags.CreateTagAsync("B");
            var c = await tags.CreateTagAsync("C");
            await tags.SetTagAsync([path, folder], a, true);
            await tags.SetTagAsync([path, folder], c, true);
            var files = new FakeFileService(root.FullName);
            files.Seed(new FileSystemEntry { FullPath = path, Name = "file.txt" });
            files.Seed(new FileSystemEntry { FullPath = folder, Name = "folder", IsDirectory = true });
            var clipboard = new TagTestClipboard();
            using var vm = CreateTagViewModel(files, tags, clipboard, root.FullName);
            await vm.NavigateToTagAsync(a);
            vm.SetSelection(vm.Entries);
            if (cut) vm.CutSelected(); else vm.CopySelected();
            Assert.Equal(cut ? a.Name : null, clipboard.GetClipboardEntry()!.SourceTag?.Name);
            await vm.NavigateToTagAsync(b);
            await vm.PasteAsync();
            foreach (var selectedPath in new[] { path, folder })
                Assert.Equal(cut ? new[] { "B", "C" } : new[] { "A", "B", "C" },
                    (await tags.GetFileTagsAsync(selectedPath)).Select(t => t.Name).Order());
            Assert.Equal(2, vm.Entries.Count);
            Assert.Equal(0, files.CopyCalls);
            Assert.Equal(0, files.MoveCalls);
            Assert.Equal(!cut, clipboard.HasClipboardFiles);
            Assert.Empty(vm.CutPaths);
            Assert.Equal("original", await File.ReadAllTextAsync(path));
            Dispatcher.UIThread.RunJobs();
        }
        finally { root.Delete(true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task SameTagCutIsNoOpAndOrdinaryDirectoryCutHasNoSourceTag()
    {
        var root = Directory.CreateTempSubdirectory("tag-cut-");
        try
        {
            var path = Path.Combine(root.FullName, "file.txt");
            await File.WriteAllTextAsync(path, "original");
            using var tags = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root.FullName, "tags.db")));
            var a = await tags.CreateTagAsync("A");
            await tags.SetTagAsync([path], a, true);
            var files = new FakeFileService(root.FullName);
            files.Seed(new FileSystemEntry { FullPath = path, Name = "file.txt" });
            var clipboard = new TagTestClipboard();
            using var vm = CreateTagViewModel(files, tags, clipboard, root.FullName);
            await vm.NavigateToTagAsync(a);
            vm.SetSelection(vm.Entries);
            vm.CutSelected();
            var entry = clipboard.GetClipboardEntry();
            await vm.PasteAsync();
            Assert.Same(entry, clipboard.GetClipboardEntry());
            Assert.Single(await tags.FindFilePathsAsync(a));
            await vm.NavigateToAsync(root.FullName);
            vm.SetSelection(vm.Entries);
            vm.CutSelected();
            Assert.Null(clipboard.GetClipboardEntry()!.SourceTag);
            Dispatcher.UIThread.RunJobs();
        }
        finally { root.Delete(true); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinderFilesAreAdoptedAndFailureIsVisible(bool available)
    {
        var root = Directory.CreateTempSubdirectory("tag-finder-");
        try
        {
            var path = Path.Combine(root.FullName, "中文 文件.txt");
            await File.WriteAllTextAsync(path, "original");
            using var tags = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root.FullName, "tags.db")));
            var b = await tags.CreateTagAsync("B");
            var files = new FakeFileService(root.FullName);
            files.Seed(new FileSystemEntry { FullPath = path, Name = "中文 文件.txt" });
            var clipboard = new TagTestClipboard
            {
                PasteKindOverride = ClipboardPasteKind.ExternalFiles,
                ExternalPaths = available ? [path] : []
            };
            using var vm = CreateTagViewModel(files, tags, clipboard, root.FullName);
            await vm.NavigateToTagAsync(b);
            await vm.PasteAsync();
            Assert.Equal(available ? 1 : 0, (await tags.FindFilePathsAsync(b)).Count);
            if (!available) Assert.Equal(LocalizationText.Get("clipboard.files_unavailable"), vm.StatusText);
            Assert.Equal(0, files.CopyCalls);
            Assert.Equal(0, files.MoveCalls);
            Assert.Equal("original", await File.ReadAllTextAsync(path));
            Dispatcher.UIThread.RunJobs();
        }
        finally { root.Delete(true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task RefreshJoinsInFlightTagNavigationWithoutCancellingItsContents()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("tag-refresh-");
        var query = new GatedQuery();
        try
        {
            var path = Path.Combine(root.FullName, "file.txt");
            await File.WriteAllTextAsync(path, "original");
            using var tags = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root.FullName, "tags.db")), query);
            var a = await tags.CreateTagAsync("A");
            await tags.SetTagAsync([path], a, true);
            var files = new FakeFileService(root.FullName);
            files.Seed(new FileSystemEntry { FullPath = path, Name = "file.txt" });
            using var vm = CreateTagViewModel(files, tags, new TagTestClipboard(), root.FullName);
            var navigation = vm.NavigateToTagAsync(a);
            await query.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var refresh = vm.RefreshAsync();
            Assert.Equal(1, query.Reads);
            query.Release.TrySetResult();
            await Task.WhenAll(navigation, refresh);
            Assert.Equal(path, Assert.Single(vm.Entries).FullPath);
            Dispatcher.UIThread.RunJobs();
        }
        finally { query.Release.TrySetResult(); root.Delete(true); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task CompletedTagCutDoesNotConsumeANewerCopy()
    {
        var root = Directory.CreateTempSubdirectory("tag-ownership-");
        try
        {
            var path = Path.Combine(root.FullName, "old.txt");
            var newer = Path.Combine(root.FullName, "new.txt");
            await File.WriteAllTextAsync(path, "old");
            await File.WriteAllTextAsync(newer, "new");
            using var tags = new FileTagService(new DatabaseConnectionFactory(Path.Combine(root.FullName, "tags.db")));
            var a = await tags.CreateTagAsync("A");
            var b = await tags.CreateTagAsync("B");
            await tags.SetTagAsync([path], a, true);
            var files = new FakeFileService(root.FullName);
            files.Seed(new FileSystemEntry { FullPath = path, Name = "old.txt" });
            var clipboard = new TagTestClipboard();
            using var vm = CreateTagViewModel(files, tags, clipboard, root.FullName);
            await vm.NavigateToTagAsync(a);
            vm.SetSelection(vm.Entries);
            vm.CutSelected();
            var old = clipboard.GetClipboardEntry();
            await vm.NavigateToTagAsync(b);
            var copied = false;
            tags.TagsChanged += (_, _) =>
            {
                if (copied) return;
                copied = true;
                vm.FileOps.CopySelected([new FileSystemEntry { FullPath = newer, Name = "new.txt" }]);
            };
            await vm.PasteAsync();
            Assert.NotSame(old, clipboard.GetClipboardEntry());
            Assert.Equal(new[] { newer }, clipboard.GetClipboardEntry()!.SourcePaths);
            Assert.Empty(old!.SourcePaths);
            Assert.Empty(vm.CutPaths);
            Assert.Contains(path, await tags.FindFilePathsAsync(b));
            Dispatcher.UIThread.RunJobs();
        }
        finally { root.Delete(true); }
    }

    private static FileListViewModel CreateTagViewModel(IFileService files, IFileTagService tags,
        IClipboardService clipboard, string root) => new(
        new NavigationViewModel(files) { CurrentPath = root, IsHomePage = false },
        new FileOpsViewModel(clipboard, files, fileTagService: tags),
        new SearchViewModel(), new ArchiveViewModel(fileService: files), new AiViewModel(),
        new PinnedFoldersViewModel(), new SortFilterViewModel(), files, new FakeFileIndex(),
        new FakeFileIndexWriter(), new IndexConfiguration(), fileTagService: tags, clipboardService: clipboard);

    private sealed class GatedQuery : IFinderTagQueryService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads { get; private set; }
        public async Task<IReadOnlyList<string>> FindFilePathsAsync(FileTag tag, CancellationToken cancellationToken = default)
        {
            Reads++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return [];
        }
    }
}
