using System.Diagnostics;
using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.Services;
using MacExplorer.Platforms.MacCatalyst.Services;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "Sftp")]
public sealed class SftpLoopbackTests
{
    [Fact(Explicit = true)]
    [Trait("Area", "FileTasks")]
    public async Task QueuedTransfersPauseResumeAndCaptureTheServerInsteadOfTheSelectedTab()
    {
        var python = Environment.GetEnvironmentVariable("MACEXPLORER_SFTP_TEST_PYTHON");
        if (python == null) Assert.Skip("Set MACEXPLORER_SFTP_TEST_PYTHON to an isolated Paramiko QA virtualenv.");
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(RuntimePaths.TestRoot ?? Path.GetTempPath(), "sftp-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await StartServer(python!, root, "transfer-host", 0, token);
            using var connections = new RemoteConnectionService(Path.Combine(root, "servers.json"),
                new PrivacySecurityFollowupTests.MemoryCredentials());
            connections.ConfirmHostKeyAsync = (_, _) => Task.FromResult(true);
            var server = new RemoteServerInfo { Host = "127.0.0.1", Port = fixture.Port, Username = "fixture", Password = "fixture-secret" };
            var browserClient = await connections.ConnectAsync(server, token); connections.SaveServer(server);
            var payload = new byte[1024 * 1024]; new Random(19).NextBytes(payload);
            using (var stream = browserClient.Create("/payload.bin")) stream.Write(payload);
            browserClient.CreateDirectory("/uploads");
            var manager = new BackgroundTaskManager();
            using var remote = new SftpFileService(connections);
            remote.SetCurrentServer("a-different-tab");
            var files = new CompositeFileService(new MacFileService(), remote, manager, connections);
            var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var requested = false;
            manager.TasksChanged += () =>
            {
                var task = manager.Tasks.FirstOrDefault(t => t.IsQueuedFileTask);
                if (!requested && task?.BytesTransferred > 0)
                { requested = true; manager.PauseTask(task.Id); }
                if (task?.State == BackgroundTaskState.Paused) paused.TrySetResult();
            };
            var outputDirectory = Directory.CreateDirectory(Path.Combine(root, "downloads")).FullName;
            var download = files.CopyWithProgressAsync(VirtualPath.BuildRemotePath(server.Id, "/payload.bin"), outputDirectory, ct: token);
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(12), token); Assert.False(download.IsCompleted);
            manager.ResumeTask(manager.Tasks[0].Id);
            var output = await download.WaitAsync(TimeSpan.FromSeconds(20), token);
            Assert.Equal(payload, await File.ReadAllBytesAsync(output, token));
            var remoteOutput = await files.CopyWithProgressAsync(output, VirtualPath.BuildRemotePath(server.Id, "/uploads"), ct: token);
            Assert.Equal(VirtualPath.BuildRemotePath(server.Id, "/uploads/payload.bin"), remoteOutput);
            using var uploaded = browserClient.OpenRead("/uploads/payload.bin"); using var memory = new MemoryStream();
            uploaded.CopyTo(memory); Assert.Equal(payload, memory.ToArray());
            var history = new FileOperationHistoryService(new MacFileService());
            using var editor = new RemoteFileEditService(connections, tasks: manager, history: history);
            var localEdit = await editor.DownloadForEditAsync("/uploads/payload.bin", server.Id);
            await File.WriteAllTextAsync(localEdit, "edited contents", token);
            await editor.UploadAsync(localEdit, "/uploads/payload.bin", server.Id);
            Assert.Equal("edited contents", browserClient.ReadAllText("/uploads/payload.bin"));
            Assert.False(history.CanUndo);
            Assert.Equal(4, manager.Tasks.Count); Assert.All(manager.Tasks, t => Assert.Equal(BackgroundTaskState.Completed, t.State));
            await manager.ShutdownAsync();
        }
        finally
        {
            foreach (var process in Servers)
            { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } process.Dispose(); }
            Servers.Clear(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Explicit = true)]
    [Trait("Area", "FileTasks")]
    public async Task DisconnectedDownloadResumesOwnedPartialOutputWithoutCreatingAnotherCopy()
    {
        var python = Environment.GetEnvironmentVariable("MACEXPLORER_SFTP_TEST_PYTHON");
        if (python == null) Assert.Skip("Set MACEXPLORER_SFTP_TEST_PYTHON to an isolated Paramiko QA virtualenv.");
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(RuntimePaths.TestRoot ?? Path.GetTempPath(), "sftp-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await StartServer(python!, root, "resume-host", 0, token);
            using var connections = new RemoteConnectionService(Path.Combine(root, "servers.json"), new PrivacySecurityFollowupTests.MemoryCredentials());
            connections.ConfirmHostKeyAsync = (_, _) => Task.FromResult(true);
            var server = new RemoteServerInfo { Host = "127.0.0.1", Port = fixture.Port, Username = "fixture", Password = "fixture-secret" };
            var browser = await connections.ConnectAsync(server, token); connections.SaveServer(server);
            var payload = new byte[2 * 1024 * 1024]; new Random(20).NextBytes(payload);
            using (var stream = browser.Create("/resume.bin")) stream.Write(payload);
            var manager = new BackgroundTaskManager(); using var remote = new SftpFileService(connections);
            var files = new CompositeFileService(new MacFileService(), remote, manager, connections);
            var disconnected = false;
            manager.TasksChanged += () =>
            {
                if (!disconnected && manager.Tasks.FirstOrDefault()?.BytesTransferred >= 128 * 1024)
                { disconnected = true; fixture.Process.Kill(true); }
            };
            var directory = Directory.CreateDirectory(Path.Combine(root, "downloads")).FullName;
            var first = files.CopyWithProgressAsync(VirtualPath.BuildRemotePath(server.Id, "/resume.bin"), directory, ct: token);
            await Assert.ThrowsAnyAsync<Exception>(() => first.WaitAsync(TimeSpan.FromSeconds(15), token));
            var task = Assert.Single(manager.Tasks); Assert.Equal(BackgroundTaskState.NeedsAttention, task.State);
            Assert.True(Directory.GetFiles(directory).Single().EndsWith(".fkfinder-tmp", StringComparison.Ordinal));
            connections.Disconnect(server.Id); await fixture.Process.WaitForExitAsync(token);
            await StartServer(python!, root, "resume-host", fixture.Port, token);
            manager.RetryTask(task.Id); await manager.WaitForCompletionAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(20), token);
            Assert.Equal(BackgroundTaskState.Completed, task.State);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(directory, "resume.bin"), token));
            Assert.Single(Directory.GetFiles(directory)); Assert.Single(manager.Tasks); await manager.ShutdownAsync();
        }
        finally
        {
            foreach (var process in Servers)
            { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } process.Dispose(); }
            Servers.Clear(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact(Explicit = true)]
    public async Task RealHandshakeRejectsBeforeAuthenticationTrustsOnceAndRejectsChangedHost()
    {
        var python = Environment.GetEnvironmentVariable("MACEXPLORER_SFTP_TEST_PYTHON");
        if (python == null) Assert.Skip("Set MACEXPLORER_SFTP_TEST_PYTHON to an isolated Paramiko QA virtualenv.");
        var root = Path.Combine(RuntimePaths.TestRoot ?? Path.GetTempPath(), "sftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var token = TestContext.Current.CancellationToken;
        try
        {
            var originalServer = await StartServer(python!, root, "original", 0, token);
            var fixture = originalServer.Process;
            var port = originalServer.Port;
            var config = Path.Combine(root, "servers.json");
            using var service = new RemoteConnectionService(config, new PrivacySecurityFollowupTests.MemoryCredentials());
            var server = new RemoteServerInfo { Host = "127.0.0.1", Port = port, Username = "fixture", Password = "fixture-secret" };
            service.ConfirmHostKeyAsync = (_, _) => Task.FromResult(false);
            await Assert.ThrowsAsync<SftpHostKeyException>(() => service.ConnectAsync(server, token));
            Assert.False(service.IsConnected(server.Id)); Assert.Empty(service.GetSavedServers()); Assert.Equal(0, Authentications(root));
            Assert.Null(service.GetTrustedHostKey(server.Host, port));
            var prompts = 0;
            service.ConfirmHostKeyAsync = (key, _) => { prompts++; Assert.StartsWith("SHA256:", key.Fingerprint); return Task.FromResult(true); };
            var client = await service.ConnectAsync(server, token);
            Assert.True(client.IsConnected); Assert.Equal(1, prompts); Assert.Contains(client.ListDirectory("/"), item => item.Name == "fixture.txt");
            service.SaveServer(server); service.Disconnect(server.Id);
            await service.ConnectAsync(server, token);
            Assert.Equal(1, prompts); service.Disconnect(server.Id);
            var trust = File.ReadAllText(Path.Combine(root, "sftp-known-hosts.json"));
            var beforeChange = Authentications(root);
            fixture.Kill(true); await fixture.WaitForExitAsync(token);
            var changedServer = await StartServer(python!, root, "changed", port, token);
            var changed = changedServer.Process;
            Assert.Equal(port, changedServer.Port);
            await Assert.ThrowsAsync<SftpHostKeyException>(() => service.ConnectAsync(server, token));
            Assert.False(service.IsConnected(server.Id)); Assert.Equal(beforeChange, Authentications(root));
            Assert.Equal(trust, File.ReadAllText(Path.Combine(root, "sftp-known-hosts.json"))); Assert.Equal(1, prompts);
            service.ForgetHostKey(server.Host, port);
            service.ConfirmHostKeyAsync = (_, _) => Task.FromResult(false);
            await Assert.ThrowsAsync<SftpHostKeyException>(() => service.ConnectAsync(server, token));
            Assert.Null(service.GetTrustedHostKey(server.Host, port)); Assert.Equal(beforeChange, Authentications(root));
            // Trust persistence failure also stops the real transport before password authentication.
            Directory.CreateDirectory(Path.Combine(root, "sftp-known-hosts.json.tmp"));
            service.ConfirmHostKeyAsync = (_, _) => Task.FromResult(true);
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => service.ConnectAsync(server, token));
            Assert.Equal(beforeChange, Authentications(root)); Assert.False(service.IsConnected(server.Id));
            Directory.Delete(Path.Combine(root, "sftp-known-hosts.json.tmp"));
            await service.ConnectAsync(server, token); Assert.True(service.IsConnected(server.Id));
            service.DisconnectAll();
            changed.Kill(true); await changed.WaitForExitAsync(token);
        }
        finally
        {
            // Children are local fixtures only; terminate them even if an assertion fails.
            foreach (var process in Servers)
            {
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                process.Dispose();
            }
            Servers.Clear();
            Directory.Delete(root, true);
        }
    }

    [Fact(Explicit = true)]
    public async Task EncryptedPrivateKeySupportsOneUseOptInReloadAndCancellation()
    {
        var python = Environment.GetEnvironmentVariable("MACEXPLORER_SFTP_TEST_PYTHON");
        if (python == null) Assert.Skip("Set MACEXPLORER_SFTP_TEST_PYTHON to an isolated Paramiko QA virtualenv.");
        var root = Path.Combine(RuntimePaths.TestRoot ?? Path.GetTempPath(), "sftp-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var token = TestContext.Current.CancellationToken;
        try
        {
            var fixture = await StartServer(python!, root, "host", 0, token);
            using var access = new MacExplorer.Services.DirectoryAccess(Path.Combine(root, "access.json"), true, internalRoots: [root]);
            using var accessOverride = MacExplorer.Services.DirectoryAccess.UseForTests(access);
            var credentials = new PrivacySecurityFollowupTests.MemoryCredentials();
            var config = Path.Combine(root, "servers.json");
            using var service = new RemoteConnectionService(config, credentials);
            service.ConfirmHostKeyAsync = (_, _) => Task.FromResult(true);
            var server = new RemoteServerInfo { Host = "127.0.0.1", Port = fixture.Port, Username = "fixture",
                AuthMethod = RemoteAuthMethod.PrivateKey, PrivateKeyPath = Path.Combine(root, "client-encrypted.pem") };
            service.RequestPrivateKeyPassphraseAsync = (_, _) => Task.FromResult<(string, bool)?>(null);
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.ConnectAsync(server, token));
            Assert.Equal(0, Authentications(root)); Assert.False(File.Exists(config)); Assert.False(service.IsConnected(server.Id));
            service.RequestPrivateKeyPassphraseAsync = (_, _) => Task.FromResult<(string, bool)?>(("wrong-fixture", false));
            await Assert.ThrowsAnyAsync<Exception>(() => service.ConnectAsync(server, token));
            Assert.Equal(0, Authentications(root)); Assert.Empty(service.GetSavedServers());
            server.PrivateKeyPassphrase = "fixture-private-key-secret";
            var client = await service.ConnectAsync(server, token);
            Assert.Contains(client.ListDirectory("/"), file => file.Name == "fixture.txt");
            service.SaveServer(server); service.DisconnectAll();
            Assert.Empty(server.PrivateKeyPassphrase);
            Assert.Null(credentials.Read(server.Id + ":private-key-passphrase"));
            Assert.DoesNotContain("fixture-private-key-secret", File.ReadAllText(config));
            // One-use also expires without restarting the app when the saved object was connected.
            var savedInProcess = Assert.Single(service.GetSavedServers());
            var passphrasePrompts = 0;
            service.RequestPrivateKeyPassphraseAsync = (_, _) =>
            {
                passphrasePrompts++;
                return Task.FromResult<(string, bool)?>(("fixture-private-key-secret", false));
            };
            await service.ConnectAsync(savedInProcess, token);
            service.Disconnect(savedInProcess.Id);
            Assert.Empty(savedInProcess.PrivateKeyPassphrase);
            await service.ConnectAsync(savedInProcess, token);
            Assert.Equal(2, passphrasePrompts);
            service.DisconnectAll();
            Assert.Empty(savedInProcess.PrivateKeyPassphrase);
            Assert.Null(credentials.Read(server.Id + ":private-key-passphrase"));
            using (var reloaded = new RemoteConnectionService(config, credentials))
            {
                var saved = Assert.Single(reloaded.GetSavedServers());
                Assert.Empty(saved.PrivateKeyPassphrase);
                await Assert.ThrowsAsync<Renci.SshNet.Common.SshPassPhraseNullOrEmptyException>(() => reloaded.ConnectAsync(saved, token));
            }
            server.RememberPrivateKeyPassphrase = true;
            server.PrivateKeyPassphrase = "fixture-private-key-secret";
            service.SaveServer(server);
            using (var reloaded = new RemoteConnectionService(config, credentials))
            {
                var saved = Assert.Single(reloaded.GetSavedServers());
                await reloaded.ConnectAsync(saved, token); Assert.True(reloaded.IsConnected(saved.Id));
                reloaded.DisconnectAll();
                // Unencrypted old keys still work, without a prompt.
                saved.PrivateKeyPath = Path.Combine(root, "client-clear.pem"); saved.PrivateKeyPassphrase = "";
                saved.RememberPrivateKeyPassphrase = false;
                await reloaded.ConnectAsync(saved, token); reloaded.SaveServer(saved);
                Assert.Null(credentials.Read(saved.Id + ":private-key-passphrase"));
                saved.AuthMethod = RemoteAuthMethod.Password; saved.Password = "fixture-secret";
                await reloaded.ConnectAsync(saved, token); reloaded.SaveServer(saved);
                Assert.Equal("fixture-secret", credentials.Read(saved.Id));
                reloaded.RemoveServer(saved.Id); Assert.Null(credentials.Read(saved.Id));
            }
            Assert.DoesNotContain("fixture-private-key-secret", File.ReadAllText(config));
        }
        finally
        {
            foreach (var process in Servers) { if (!process.HasExited) process.Kill(true); process.Dispose(); }
            Servers.Clear(); Directory.Delete(root, true);
        }
    }

    private readonly List<Process> Servers = [];
    private async Task<(Process Process, int Port)> StartServer(string python, string root, string key, int port, CancellationToken token)
    {
        var info = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "TestData/sftp-loopback.py"), root, key, port.ToString() }) info.ArgumentList.Add(arg);
        var process = Process.Start(info)!; Servers.Add(process);
        var ready = await process.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(15), token);
        if (!int.TryParse(ready, out var listeningPort))
            throw new InvalidOperationException("SFTP fixture did not start: " + await process.StandardError.ReadToEndAsync(token));
        return (process, listeningPort);
    }
    private static int Authentications(string root)
    {
        var log = Path.Combine(root, "events.jsonl");
        return File.Exists(log) ? File.ReadLines(log).Count(line => JsonDocument.Parse(line).RootElement.GetProperty("event").GetString() == "authentication") : 0;
    }

}
