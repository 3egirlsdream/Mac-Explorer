using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using MacExplorer.PluginSdk;
using MacExplorer.Services.Plugins;
using Xunit;

namespace MacExplorer.Tests;

[SupportedOSPlatform("macos")]
public sealed class ReviewPluginLifetimeTests
{
    [Fact]
    public async Task ConcurrentDisposeJoinsTheSameCleanupAndDrainsBlockedRpcBeforeDisposingSynchronization()
    {
        using var fixture = new WorkerFixture("#!/bin/sh\nexec sleep 60\n");
        var released = 0;
        var session = fixture.Start(() => { Interlocked.Increment(ref released); return Task.CompletedTask; });
        Task<JsonElement>? call = null;
        try
        {
            call = session.CallAsync<JsonElement>("execute", new { payload = new string('x', 2 * 1024 * 1024) },
                TimeSpan.FromSeconds(30), default);
            var first = session.DisposeAsync().AsTask();
            var second = session.DisposeAsync().AsTask();
            Assert.Same(first, second);
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(call.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
            Assert.Equal(1, released);
            session.Cancel(); // A late UI cancel callback is harmless after disposal.
        }
        finally
        {
            fixture.Kill();
            if (call != null) { try { await call.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task UnwritableLogStillDrainsStderrSoTheWorkerCanReturnItsResult()
    {
        using var fixture = new WorkerFixture("#!/bin/sh\nIFS= read -r request || exit 1\n" +
            "dd if=/dev/zero bs=65536 count=32 >&2 2>/dev/null\n" +
            "printf '%s\\n' '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"ok\":true}}'\n");
        Directory.CreateDirectory(fixture.LogPath); // Deterministically unwritable as a file, including under root.
        var session = fixture.Start(() => Task.CompletedTask);
        try
        {
            var result = await session.CallAsync<JsonElement>("execute", null, TimeSpan.FromSeconds(3), default)
                .WaitAsync(TimeSpan.FromSeconds(6));
            Assert.True(result.GetProperty("ok").GetBoolean());
        }
        finally
        {
            fixture.Kill();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task PluginReportedCancellationIsNotMisreportedAsAHostTimeout()
    {
        using var fixture = new WorkerFixture("#!/bin/sh\nIFS= read -r request || exit 1\n" +
            "printf '%s\\n' '{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32800,\"message\":\"plugin cancelled\"}}'\n");
        var session = fixture.Start(() => Task.CompletedTask);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                session.CallAsync<JsonElement>("execute", null, TimeSpan.FromSeconds(30), default));
        }
        finally
        {
            fixture.Kill();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class WorkerFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("macexplorer-lifetime-").FullName;
        private readonly string _plugin;
        private readonly string _executable;
        private int? _pid;
        public string LogPath => Path.Combine(_root, "worker.log");
        public WorkerFixture(string script)
        {
            _plugin = Directory.CreateDirectory(Path.Combine(_root, "plugin")).FullName;
            File.Copy(typeof(IFileActionPlugin).Assembly.Location, Path.Combine(_plugin, "entry.dll"));
            File.WriteAllText(Path.Combine(_plugin, "plugin.json"), JsonSerializer.Serialize(new PluginManifest
            {
                Id = "test.lifetime", Name = "Lifetime", Version = "1.0.0", ApiVersion = PluginProtocol.ApiVersion,
                Entry = "entry.dll", Commands = [new() { Id = "run", Title = "Run" }]
            }, PluginProtocol.Json));
            _executable = Path.Combine(_root, "worker");
            File.WriteAllText(_executable, script);
            File.SetUnixFileMode(_executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public PluginSession Start(Func<Task> released)
        {
            var session = new PluginSession(_executable, "unused.dll", _plugin, Path.Combine(_root, "work"), LogPath, released);
            _pid = session.ProcessId;
            return session;
        }
        public void Kill()
        {
            if (_pid is not { } pid) return;
            try { using var process = Process.GetProcessById(pid); if (!process.HasExited) process.Kill(true); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        }
        public void Dispose() { Kill(); Directory.Delete(_root, true); }
    }
}
