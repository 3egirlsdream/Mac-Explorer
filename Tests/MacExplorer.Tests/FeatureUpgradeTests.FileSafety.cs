using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FeatureUpgradeTests
{
    [Theory]
    [InlineData(FileConflictAction.KeepBoth)]
    [InlineData(FileConflictAction.Skip)]
    [InlineData(FileConflictAction.Replace)]
    [Trait("Area", "FileConflicts")]
    public async Task SelectionMoveRecordsOnlyTheActualConflictOutcomeAndUndoesIt(FileConflictAction action)
    {
        var source = At("source/item.txt", "incoming");
        var target = At("target/item.txt", "original target");
        var files = Files(new(action), out var history, out var tasks);
        try
        {
            var operations = new FileOpsViewModel(null, files, tasks, fileOperationHistoryService: history);
            await operations.MoveEntriesAsync([(await files.GetEntryAsync(source))!],
                (await files.GetEntryAsync(Path.GetDirectoryName(target)!))!);
            if (action == FileConflictAction.Skip)
            {
                Assert.Empty(history.Records);
                Assert.Empty(operations.LastOperationResult!.CompletedPaths);
            }
            else
            {
                var output = Assert.Single(operations.LastOperationResult!.CompletedPaths);
                var record = Assert.Single(history.Records);
                Assert.Equal(output, record.CurrentPath);
                Assert.Empty(record.Children);
                Assert.Equal(action == FileConflictAction.Replace ? FileOperationKind.Replace : FileOperationKind.Move, record.Kind);
                var undo = await history.UndoAsync(record.Id);
                Assert.True(undo.Success, undo.Message);
                if (action == FileConflictAction.KeepBoth) Assert.False(File.Exists(output));
            }
            Assert.Equal("incoming", File.ReadAllText(source));
            Assert.Equal("original target", File.ReadAllText(target));
        }
        finally { await tasks.ShutdownAsync(); }
    }

    private sealed class FailSourceDeletion(int failures) : MacFileService, IFileService
    {
        internal int Attempts;
        Task IFileService.DeleteAsync(string path, bool moveToTrash)
        {
            if (++Attempts <= failures) throw new IOException("Injected source deletion failure");
            File.Delete(path); return Task.CompletedTask;
        }
    }

    [Fact]
    [Trait("Area", "FileConflicts")]
    public async Task RetryRejectsAReplacementSourceEvenWhenItsBytesAreIdentical()
    {
        var source = At("source/item.txt", "incoming");
        var target = At("target/item.txt", "original target");
        var local = new FailSourceDeletion(1); var tasks = new BackgroundTaskManager();
        var files = new CompositeFileService(local, null!, tasks, conflicts: new Choice(FileConflictAction.Replace));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => files.MoveAsync(source, Path.GetDirectoryName(target)!));
            File.Move(source, source + ".original");
            File.WriteAllText(source, "incoming");
            var task = Assert.Single(tasks.Tasks); tasks.RetryTask(task.Id);
            await Assert.ThrowsAsync<IOException>(() => tasks.WaitForCompletionAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Equal(1, local.Attempts);
            Assert.Equal("incoming", File.ReadAllText(source));
            Assert.Equal("incoming", File.ReadAllText(source + ".original"));
        }
        finally { await tasks.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Area", "FileConflicts")]
    public async Task ReplacementBackupKeepsTheNameAndRestoresHiddenFilesAndDirectories(bool directory)
    {
        var source = At("source/.item", "incoming");
        var original = At(directory ? "target/.item/child.txt" : "target/.item", "original target");
        var target = Path.Combine(_root, "target/.item");
        var files = Files(new(FileConflictAction.Replace), out var history, out var tasks);
        try
        {
            await files.CopyWithProgressAsync(source, Path.GetDirectoryName(target)!, ct: Token);
            var record = Assert.Single(history.Records);
            Assert.Equal(".item", Path.GetFileName(record.BackupPath));
            Assert.Equal(record.BackupSnapshot, FileIdentitySnapshot.Capture(record.BackupPath!, true));
            Assert.True(await history.UndoLastAsync());
            Assert.Equal("original target", File.ReadAllText(original));
            Assert.Equal("incoming", File.ReadAllText(source));
        }
        finally { await tasks.ShutdownAsync(); }
    }

    [Fact]
    [Trait("Area", "FileConflicts")]
    public async Task CancellationImmediatelyAfterPublicationRetainsAnUndoableReplacement()
    {
        var source = At("source/item.txt", "incoming");
        var target = At("target/item.txt", "original target");
        using var tags = new FileTagService(Database()); var tasks = new BackgroundTaskManager();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        FileOperationHistoryService? history = null;
        var files = new CompositeFileService(new MacFileService(), null!, tasks,
            history: () => history, conflicts: new Choice(FileConflictAction.Replace), tags: tags);
        history = new FileOperationHistoryService(files, tasks: tasks);
        tags.TagsChanged += (_, _) =>
        {
            if (File.Exists(target) && File.ReadAllText(target) == "incoming") cancellation.Cancel();
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.MoveWithProgressAsync([source], Path.GetDirectoryName(target)!, ct: cancellation.Token));
            Assert.Single(history.Records);
            Assert.True(await history.UndoLastAsync());
            Assert.Equal("incoming", File.ReadAllText(source));
            Assert.Equal("original target", File.ReadAllText(target));
        }
        finally { await tasks.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Area", "FileConflicts")]
    public async Task RetryingPublishedReplacementKeepsItsIdentityBackupAndUndoHistory(int failures)
    {
        var source = At("source/item.txt", "incoming");
        var target = At("target/item.txt", "original target");
        var local = new FailSourceDeletion(failures);
        var tasks = new BackgroundTaskManager();
        var choice = new Choice(FileConflictAction.Replace);
        FileOperationHistoryService? history = null;
        var files = new CompositeFileService(local, null!, tasks, history: () => history, conflicts: choice);
        history = new FileOperationHistoryService(files, tasks: tasks);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => files.MoveAsync(source, Path.GetDirectoryName(target)!));
            var task = Assert.Single(tasks.Tasks);
            var originalRecord = Assert.Single(history.Records);
            var published = FileIdentitySnapshot.Capture(target, true);
            for (var attempt = 0; attempt < failures; attempt++)
            {
                tasks.RetryTask(task.Id);
                try { await tasks.WaitForCompletionAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(10), Token); }
                catch (IOException) when (attempt + 1 < failures) { }
                Assert.Equal(published, FileIdentitySnapshot.Capture(target, true));
                Assert.Same(originalRecord, Assert.Single(history.Records));
                Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(target)!, ".MacExplorer-replace*"));
            }
            Assert.Equal(BackgroundTaskState.Completed, task.State);
            Assert.Equal(1, choice.Count);
            Assert.Equal(failures + 1, local.Attempts);
            var undo = await history.UndoAsync(originalRecord.Id);
            Assert.True(undo.Success, undo.Message);
            Assert.Equal("incoming", File.ReadAllText(source));
            Assert.Equal("original target", File.ReadAllText(target));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(target)!, ".MacExplorer-replace*"));
        }
        finally { await tasks.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Area", "FileConflicts")]
    public async Task ReplacementRetryPreservesExternalChangesInsteadOfDeletingTheSource(bool changeTarget)
    {
        var source = At("source/item.txt", "incoming");
        var target = At("target/item.txt", "original target");
        var local = new FailSourceDeletion(1);
        var tasks = new BackgroundTaskManager();
        FileOperationHistoryService? history = null;
        var files = new CompositeFileService(local, null!, tasks, history: () => history, conflicts: new Choice(FileConflictAction.Replace));
        history = new FileOperationHistoryService(files, tasks: tasks);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => files.MoveAsync(source, Path.GetDirectoryName(target)!));
            File.WriteAllText(changeTarget ? target : source, "external change");
            var task = Assert.Single(tasks.Tasks);
            tasks.RetryTask(task.Id);
            await Assert.ThrowsAsync<IOException>(() => tasks.WaitForCompletionAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Equal(1, local.Attempts);
            Assert.Equal(changeTarget ? "incoming" : "external change", File.ReadAllText(source));
            Assert.Equal(changeTarget ? "external change" : "incoming", File.ReadAllText(target));
            Assert.Equal("original target", File.ReadAllText(Assert.Single(history.Records).BackupPath!));
        }
        finally { await tasks.ShutdownAsync(); }
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeSnapshotFifo(string path, ushort mode);

    [Fact]
    [Trait("Area", "FileConflicts")]
    public async Task ReplacingDirectoryContainingFifoFailsPromptlyAndKeepsBothInputs()
    {
        var source = At("source/item", "incoming");
        var target = Directory.CreateDirectory(Path.Combine(_root, "target/item")).FullName;
        var pipe = Path.Combine(target, "runtime.pipe");
        Assert.Equal(0, MakeSnapshotFifo(pipe, 0x180));
        var files = Files(new(FileConflictAction.Replace), out var history, out var tasks);
        var operation = files.CopyWithProgressAsync(source, Path.GetDirectoryName(target)!, ct: Token);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => operation.WaitAsync(TimeSpan.FromSeconds(3), Token));
            Assert.Equal("incoming", File.ReadAllText(source));
            Assert.True(Directory.Exists(target));
            Assert.True(File.Exists(pipe));
            Assert.Empty(history.Records);
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(target)!, ".MacExplorer-replace*"));
        }
        finally
        {
            if (!operation.IsCompleted) await Task.Run(() => { using var writer = File.OpenWrite(pipe); writer.WriteByte(42); });
            await tasks.ShutdownAsync();
        }
    }

    [Fact]
    public void ContentSnapshotRejectsSocketsAndUnsafeLinkOpensButPreservesLinkSnapshots()
    {
        var path = Path.Combine(_root, "runtime.sock");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        Assert.Throws<IOException>(() => FileIdentitySnapshot.Capture(path, true));
        var file = At("regular.txt", "contents");
        var link = Path.Combine(_root, "link"); File.CreateSymbolicLink(link, file);
        Assert.Throws<IOException>(() => MacExplorer.Platforms.MacOS.MacSandboxNative.OpenRegularRead(link));
        Assert.NotNull(FileIdentitySnapshot.Capture(link, true));
        var before = FileIdentitySnapshot.Capture(file, true);
        File.WriteAllText(file, "changed contents");
        Assert.NotEqual(before, FileIdentitySnapshot.Capture(file, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContentHashObservesCancellationBeforeAndDuringARead(bool cancelDuringRead)
    {
        using var cancellation = new CancellationTokenSource();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new CancelSnapshotRead(cancellation);
        if (!cancelDuringRead) cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => FileIdentitySnapshot.AppendContent(digest, stream, new byte[1024], cancellation.Token));
        Assert.Equal(cancelDuringRead ? 1 : 0, stream.Reads);
        Assert.Equal(SHA256.HashData([]), digest.GetHashAndReset());
    }

    private sealed class CancelSnapshotRead(CancellationTokenSource cancellation) : MemoryStream(new byte[1024])
    {
        internal int Reads;
        public override int Read(byte[] buffer, int offset, int count)
        { Reads++; var read = base.Read(buffer, offset, count); cancellation.Cancel(); return read; }
    }
}
