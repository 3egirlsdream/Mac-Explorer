using System.Diagnostics;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.Services;
using MacExplorer.Platforms.MacCatalyst.Services;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class SftpLoopbackTests
{
    private sealed class ReplaceConflicts : IFileConflictResolver
    {
        public Task<FileConflictDecision> ResolveAsync(FileConflict conflict, CancellationToken cancellationToken)
            => Task.FromResult(new FileConflictDecision(FileConflictAction.Replace));
    }

    [Fact(Explicit = true)]
    public async Task RemoteReplacementHandlesBothTypeChangesAndDirectoryMerges()
    {
        var python = Environment.GetEnvironmentVariable("MACEXPLORER_SFTP_TEST_PYTHON");
        if (python == null) Assert.Skip("Requires the isolated SFTP QA environment.");
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "remote-conflict-upgrade-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var fixture = await StartServer(python!, root, "replacing", 0, token);
            using var connections = new RemoteConnectionService(Path.Combine(root, "servers.json"), new PrivacySecurityFollowupTests.MemoryCredentials());
            connections.ConfirmHostKeyAsync = (_, _) => Task.FromResult(true);
            var server = new RemoteServerInfo { Host = "127.0.0.1", Port = fixture.Port, Username = "fixture", Password = "fixture-secret" };
            var browser = await connections.ConnectAsync(server, token); connections.SaveServer(server);
            browser.CreateDirectory("/destination"); browser.CreateDirectory("/destination/item"); browser.WriteAllText("/destination/item/old.txt", "old directory");
            var source = Path.Combine(root, "item"); await File.WriteAllTextAsync(source, "new file", token);
            var tasks = new BackgroundTaskManager(); using var remote = new SftpFileService(connections);
            var files = new CompositeFileService(new MacFileService(), remote, tasks, connections, conflicts: new ReplaceConflicts());
            var destination = VirtualPath.BuildRemotePath(server.Id, "/destination");
            await files.CopyWithProgressAsync(source, destination, ct: token);
            Assert.Equal("new file", browser.ReadAllText("/destination/item"));
            File.Delete(source); Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "new.txt"), "new directory", token);
            await files.CopyWithProgressAsync(source, destination, ct: token);
            Assert.Equal("new directory", browser.ReadAllText("/destination/item/new.txt"));
            browser.WriteAllText("/destination/item/keep.txt", "keep target only");
            await File.WriteAllTextAsync(Path.Combine(source, "new.txt"), "updated", token);
            await files.CopyWithProgressAsync(source, destination, ct: token);
            Assert.Equal("updated", browser.ReadAllText("/destination/item/new.txt"));
            Assert.Equal("keep target only", browser.ReadAllText("/destination/item/keep.txt"));
            Assert.DoesNotContain(browser.ListDirectory("/destination"), entry => entry.Name.StartsWith(".MacExplorer-replace-", StringComparison.Ordinal));
            await tasks.ShutdownAsync();
        }
        finally
        {
            foreach (var process in Servers) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } process.Dispose(); }
            Servers.Clear();
        }
    }
    [Fact(Explicit = true)]
    public async Task EditingQueuesTheLastSaveHandlesAtomicReplacementAndPreservesRemoteConflicts()
    {
        var python = Environment.GetEnvironmentVariable("MACEXPLORER_SFTP_TEST_PYTHON");
        if (python == null) Assert.Skip("Requires the isolated SFTP QA environment.");
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "remote-edit-upgrade-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var fixture = await StartServer(python!, root, "editing", 0, token);
            using var connections = new RemoteConnectionService(Path.Combine(root, "servers.json"), new PrivacySecurityFollowupTests.MemoryCredentials());
            connections.ConfirmHostKeyAsync = (_, _) => Task.FromResult(true);
            var server = new RemoteServerInfo { Host = "127.0.0.1", Port = fixture.Port, Username = "fixture", Password = "fixture-secret" };
            var browser = await connections.ConnectAsync(server, token); connections.SaveServer(server);
            browser.WriteAllText("/edit.txt", "original");
            var tasks = new BackgroundTaskManager(); using var edits = new RemoteFileEditService(connections, tasks: tasks);
            var local = await edits.DownloadForEditAsync("/edit.txt", server.Id);
            edits.WatchForChanges(local, "/edit.txt", server.Id);
            var savedAgain = false;
            edits.Changed += () =>
            {
                if (!savedAgain && edits.Sessions.Any(s => s.State == RemoteEditState.Uploading))
                { savedAgain = true; File.WriteAllText(local, "last saved version"); }
            };
            await File.WriteAllTextAsync(local, new string('a', 256 * 1024), token);
            await WaitForAsync(() => savedAgain && edits.Sessions.All(s => s.State == RemoteEditState.Synced), token);
            Assert.Equal("last saved version", browser.ReadAllText("/edit.txt"));
            var replacement = local + ".new"; await File.WriteAllTextAsync(replacement, "atomic save", token); File.Move(replacement, local, true);
            await WaitForAsync(() => edits.Sessions.All(s => s.State == RemoteEditState.Synced) && browser.ReadAllText("/edit.txt") == "atomic save", token);
            var baseline = browser.GetAttributes("/edit.txt"); browser.WriteAllText("/edit.txt", "remote save"); File.SetLastWriteTimeUtc(Path.Combine(root, "remote-files", "edit.txt"), baseline.LastWriteTimeUtc);
            await File.WriteAllTextAsync(local, "local unsaved change", token);
            await WaitForAsync(() => edits.Sessions.Any(s => s.State == RemoteEditState.Conflict), token);
            Assert.Equal("remote save", browser.ReadAllText("/edit.txt")); Assert.Equal("local unsaved change", File.ReadAllText(local));
            await edits.ResolveConflictAsync(local, true);
            Assert.Equal("local unsaved change", browser.ReadAllText("/edit.txt"));
            edits.StopWatching(local);
            await File.WriteAllTextAsync(local, "resumed after stop", token);
            Assert.True(edits.HasPendingChanges);
            await edits.RetryAsync(local);
            Assert.Equal("resumed after stop", browser.ReadAllText("/edit.txt"));
            fixture.Process.Kill(true); await fixture.Process.WaitForExitAsync(token);
            await File.WriteAllTextAsync(local, "saved while disconnected", token);
            await WaitForAsync(() => edits.Sessions.Any(s => s.State == RemoteEditState.Failed), token);
            Assert.Equal("saved while disconnected", File.ReadAllText(local));
            await StartServer(python!, root, "editing", fixture.Port, token);
            await edits.ReconnectAsync(local);
            browser = await connections.GetOrConnectAsync(server, token);
            Assert.Equal("saved while disconnected", browser.ReadAllText("/edit.txt"));
            browser.WriteAllText("/edit.txt", "remote again"); await File.WriteAllTextAsync(local, "retain this local copy", token);
            await WaitForAsync(() => edits.Sessions.Any(s => s.State == RemoteEditState.Conflict), token);
            await edits.StopAsync(); edits.CleanupTempFiles(); Assert.True(File.Exists(local));
            Assert.Equal("retain this local copy", File.ReadAllText(local)); await tasks.ShutdownAsync();
        }
        finally
        {
            foreach (var process in Servers) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } process.Dispose(); }
            Servers.Clear();
        }
    }
    private static async Task WaitForAsync(Func<bool> ready, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (!ready()) { if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Remote edit state did not settle."); await Task.Delay(30, token); }
    }
}
