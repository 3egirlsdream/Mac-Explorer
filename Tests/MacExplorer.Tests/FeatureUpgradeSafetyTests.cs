using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FeatureUpgradeTests
{
    [Theory]
    [InlineData(System.Net.Sockets.SocketError.ConnectionRefused, RemoteEditFailureReason.ConnectionRefused)]
    [InlineData(System.Net.Sockets.SocketError.TimedOut, RemoteEditFailureReason.TimedOut)]
    [InlineData(System.Net.Sockets.SocketError.ConnectionReset, RemoteEditFailureReason.ConnectionLost)]
    public void RemoteEditFailureUsesTheUnderlyingErrorRatherThanParsingItsMessage(System.Net.Sockets.SocketError error, RemoteEditFailureReason expected)
    {
        var cause = new IOException("unlocalized wrapper", new System.Net.Sockets.SocketException((int)error));
        Assert.Equal(expected, RemoteFileEditService.FailureReason(cause));
        Assert.Equal(RemoteEditFailureReason.PermissionDenied, RemoteFileEditService.FailureReason(new UnauthorizedAccessException()));
        Assert.Equal(RemoteEditFailureReason.Unknown, RemoteFileEditService.FailureReason(new IOException("unexpected error")));
    }

    [Fact]
    public async Task UndoBaselineIsCapturedBeforePublishingRatherThanAfterObserversCanEditTheDestination()
    {
        var source = At("source/report.txt", "new"); var target = At("target/report.txt", "old");
        using var tags = new FileTagService(Database()); var tasks = new BackgroundTaskManager();
        FileOperationHistoryService? history = null;
        var files = new CompositeFileService(new MacFileService(), null!, tasks, history: () => history,
            conflicts: new Choice(FileConflictAction.Replace), tags: tags);
        history = new FileOperationHistoryService(files, tasks: tasks); var edited = false;
        tags.TagsChanged += (_, _) =>
        {
            if (!edited && File.Exists(target) && File.ReadAllText(target) == "new")
            { edited = true; File.WriteAllText(target, "external edit after publication"); }
        };
        await files.CopyWithProgressAsync(source, Path.GetDirectoryName(target)!, ct: Token);
        Assert.True(edited); Assert.False(await history.UndoLastAsync());
        Assert.Equal("external edit after publication", File.ReadAllText(target));
        Assert.Equal("old", File.ReadAllText(history.Records[0].BackupPath!)); await tasks.ShutdownAsync();
    }

    private sealed class Clipboard : IClipboardService
    {
        private ClipboardEntry? _entry;
        public bool HasClipboardFiles => _entry is { IsEmpty: false };
        public bool HasPasteableContent => HasClipboardFiles;
        public ClipboardPasteKind GetPasteKind() => HasClipboardFiles ? ClipboardPasteKind.InAppFiles : ClipboardPasteKind.None;
        public bool TryAdoptExternalFiles() => false;
        public ClipboardImageData? ReadExternalImage() => null;
        public ClipboardEntry? GetClipboardEntry() => _entry;
        public void CopyFiles(string[] paths) => _entry = new() { SourcePaths = paths.ToList(), Operation = ClipboardOperation.Copy };
        public void CutFiles(string[] paths, FileTag? sourceTag = null) => _entry = new() { SourcePaths = paths.ToList(), Operation = ClipboardOperation.Cut, SourceTag = sourceTag };
        public void Clear() => _entry = null;
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public Task PasteFilesAsync(string targetDirectory) => throw new NotSupportedException();
    }
    private sealed class MergeThenSkip : IFileConflictResolver
    {
        private int _calls;
        public Task<FileConflictDecision> ResolveAsync(FileConflict conflict, CancellationToken cancellationToken)
            => Task.FromResult(new FileConflictDecision(++_calls == 1 ? FileConflictAction.Replace : FileConflictAction.Skip));
    }
    [Fact]
    public async Task PartialDirectoryMoveKeepsRemainingCutItemsInTheClipboard()
    {
        var source = Path.GetDirectoryName(At("source/folder/existing.txt", "new"))!;
        At("source/folder/moved.txt", "move this"); At("target/folder/existing.txt", "old");
        var clipboard = new Clipboard(); clipboard.CutFiles([source]); var tasks = new BackgroundTaskManager();
        var files = new CompositeFileService(new MacFileService(), null!, tasks, conflicts: new MergeThenSkip());
        var vm = new FileOpsViewModel(clipboard, files, tasks); vm.CutPaths.Add(source);
        await vm.PasteAsync(Path.Combine(_root, "target"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(source, "existing.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "target/folder/moved.txt")));
        Assert.Contains(source, clipboard.GetClipboardEntry()!.SourcePaths); Assert.Contains(source, vm.CutPaths);
        await tasks.ShutdownAsync();
    }
    [Fact]
    public async Task MovingFromATagStillRemovesOnlyTheSourceTagAfterCompositeMetadataTransfer()
    {
        using var tags = new FileTagService(Database());
        var from = await tags.CreateTagAsync("From"); var keep = await tags.CreateTagAsync("Keep");
        var source = At("source/item.txt"); var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        await tags.SetTagAsync([source], from, true); await tags.SetTagAsync([source], keep, true);
        var clipboard = new Clipboard(); clipboard.CutFiles([source], from); var tasks = new BackgroundTaskManager();
        var files = new CompositeFileService(new MacFileService(), null!, tasks, conflicts: new Choice(FileConflictAction.Replace), tags: tags);
        var vm = new FileOpsViewModel(clipboard, files, tasks, fileTagService: tags);
        await vm.PasteAsync(target);
        Assert.Equal([keep.Name], (await tags.GetFileTagsAsync(Path.Combine(target, "item.txt"))).Select(t => t.Name));
        Assert.False(clipboard.HasClipboardFiles); await tasks.ShutdownAsync();
    }

    [Fact]
    public void VersionElevenDatabaseMigratesWithoutDeletingExistingEntries()
    {
        var database = Database();
        using (var tags = new AiTagService(database)) { }
        using var connection = database.GetConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO files(path,name,parent_path) VALUES('/kept','kept','/');
                DROP TABLE directory_preferences;
                DROP TABLE document_segments; DROP TABLE document_index;
                DELETE FROM schema_version; INSERT INTO schema_version(version) VALUES(11);
                """;
            command.ExecuteNonQuery();
        }
        SqliteSchema.Initialize(connection);
        using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT name FROM files WHERE path='/kept'"; Assert.Equal("kept", verify.ExecuteScalar());
        verify.CommandText = "SELECT max(version) FROM schema_version"; Assert.Equal((long)SqliteSchema.CurrentVersion, verify.ExecuteScalar());
        verify.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='saved_searches'"; Assert.Equal(0L, verify.ExecuteScalar());
        verify.CommandText = "SELECT count(*) FROM document_segments"; Assert.Equal(0L, verify.ExecuteScalar());
        verify.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='face_corrections'"; Assert.Equal(0L, verify.ExecuteScalar());
        verify.CommandText = "SELECT count(*) FROM pragma_table_info('face_observations') WHERE name='manual_state'"; Assert.Equal(0L, verify.ExecuteScalar());
    }

    private sealed class BlockingPdf : IPdfTextExtractionService
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public async Task<IReadOnlyList<RecognizedText>> ExtractAsync(string filePath, IProgress<PdfAnalysisProgress>? progress = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return [new RecognizedText { Text = "Shared extraction", Page = 2 }];
        }
    }

    [Fact]
    public async Task BrowserIndexAndCopilotShareOneCancellableExtractionSlot()
    {
        var path = At("shared.pdf"); var pdf = new BlockingPdf();
        var first = new DocumentContentExtractor(pdf).ExtractAsync(path, Token);
        try
        {
            await pdf.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var waiting = new MacExplorer.Copilot.CopilotContentExtractor(pdf, null!).ExtractPageAsync(path, cancellationToken: cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(1, pdf.Calls);
        }
        finally { pdf.Release.TrySetResult(); }
        Assert.Equal(2, Assert.Single((await first).Segments).Location.Number);
        Assert.Equal("Shared extraction", (await new MacExplorer.Copilot.CopilotContentExtractor(pdf, null!).ExtractPageAsync(path, cancellationToken: Token)).Text);
        Assert.Equal(2, pdf.Calls);
    }

    private sealed class InterruptedCopy(Action<CancellationToken> interrupt) : MacFileService, IFileService
    {
        Task<string> IFileService.CopyWithProgressAsync(string sourcePath, string destinationDirectory,
            IProgress<FileOperationProgress>? progress, CancellationToken ct)
        {
            File.WriteAllText(Path.Combine(destinationDirectory, "partial-output"), "partial");
            interrupt(ct);
            return Task.FromResult("");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledStagingKeepsOriginalAndCleansOnlyOwnedFiles(bool cancel)
    {
        var source = At("source/report.txt", "new");
        var target = At("target/report.txt", "original");
        var unrelated = At("target/user-owned.fkfinder-tmp", "keep");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var local = new InterruptedCopy(token =>
        {
            if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new IOException("Simulated insufficient disk space");
        });
        var tasks = new BackgroundTaskManager();
        var files = new CompositeFileService(local, null!, tasks, conflicts: new Choice(FileConflictAction.Replace));
        await Assert.ThrowsAnyAsync<Exception>(() => files.CopyWithProgressAsync(source, Path.GetDirectoryName(target)!, ct: cancellation.Token));
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Equal("new", File.ReadAllText(source));
        Assert.Equal("keep", File.ReadAllText(unrelated));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(target)!, ".MacExplorer-replace*"));
        await tasks.ShutdownAsync();
    }

    [Fact]
    public async Task DestinationChangedDuringQuestionIsNeverReplaced()
    {
        var source = At("source/file.txt", "new");
        var target = At("target/file.txt", "old");
        var files = Files(new(FileConflictAction.Replace, beforeReturn: () => File.WriteAllText(target, "external change")), out _, out var tasks);
        await Assert.ThrowsAsync<IOException>(() => files.CopyWithProgressAsync(source, Path.GetDirectoryName(target)!, ct: Token));
        Assert.Equal("external change", File.ReadAllText(target));
        await tasks.ShutdownAsync();
    }

    [Fact]
    public async Task FileReplacingDirectoryCanRestoreTheEntireOldDirectory()
    {
        var source = At("source/item", "file contents");
        var original = At("target/item/nested/old.txt", "old directory contents");
        var target = Path.Combine(_root, "target/item");
        var files = Files(new(FileConflictAction.Replace), out var history, out var tasks);
        await files.CopyWithProgressAsync(source, Path.GetDirectoryName(target)!, ct: Token);
        Assert.Equal("file contents", File.ReadAllText(target));
        Assert.True(await history.UndoLastAsync());
        Assert.Equal("old directory contents", File.ReadAllText(original));
        await tasks.ShutdownAsync();
    }

    [Fact]
    public async Task ApplyToRemainingStillAsksBeforeReplacingADifferentType()
    {
        var source = Path.GetDirectoryName(At("source/folder/item", "incoming file"))!;
        var original = At("target/folder/item/old.txt", "old directory");
        var target = Path.Combine(_root, "target/folder");
        var choice = new Choice(FileConflictAction.Replace, true);
        var files = Files(choice, out var history, out var tasks);
        await files.CopyWithProgressAsync(source, Path.GetDirectoryName(target)!, ct: Token);
        Assert.Equal(2, choice.Count);
        Assert.True(await history.UndoLastAsync()); Assert.Equal("old directory", File.ReadAllText(original));
        await tasks.ShutdownAsync();
    }

    [Theory]
    [InlineData(FileConflictAction.Skip)]
    [InlineData(FileConflictAction.KeepBoth)]
    public async Task MoveResultsReflectSkippedOrRenamedDestinations(FileConflictAction action)
    {
        var source = At("source/file.txt", "new");
        var target = At("target/file.txt", "old");
        var files = Files(new(action), out var history, out var tasks);
        var operations = new FileOperationService(files, null, null, null, history, tasks, null);
        var result = await operations.MoveDetailedAsync((await files.GetEntryAsync(source))!,
            (await files.GetEntryAsync(Path.GetDirectoryName(target)!))!);
        Assert.Equal("old", File.ReadAllText(target));
        if (action == FileConflictAction.Skip) { Assert.Empty(result.CompletedPaths); Assert.True(File.Exists(source)); Assert.Empty(history.Records); }
        else { var output = Assert.Single(result.CompletedPaths); Assert.NotEqual(target, output); Assert.Equal("new", File.ReadAllText(output)); Assert.False(File.Exists(source)); }
        await tasks.ShutdownAsync();
    }

    [Fact]
    public async Task RichSearchPaginatesWithoutRepeatingPaths()
    {
        var catalog = new SearchCatalog(Database());
        var entries = Enumerable.Range(0, 125).Select(i =>
        {
            var path = At($"pages/report-{i:000}.txt"); var info = new FileInfo(path);
            return new SearchEntry(path, info.Name, info.DirectoryName!, ".txt", info.Name.ToLowerInvariant(), "", info.DirectoryName!.ToLowerInvariant(), info.Length,
                false, false, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks);
        }).ToArray();
        await catalog.UpsertBatchAsync(entries, "fixture", Token);
        var query = new IndexedFileQuery { Query = "report", Source = "name" };
        var first = await catalog.SearchResourcesAsync(_root, query, new(), Token);
        var second = await catalog.SearchResourcesAsync(_root, query with { Offset = first.NextOffset!.Value }, new(), Token);
        Assert.Equal(100, first.Items.Count); Assert.Equal(25, second.Items.Count); Assert.False(second.HasMore);
        Assert.Equal(125, first.Items.Concat(second.Items).Select(i => i.FullPath).Distinct().Count());
    }

}
