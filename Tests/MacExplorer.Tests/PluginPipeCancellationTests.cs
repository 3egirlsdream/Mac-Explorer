using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using MacExplorer.PluginSdk;
using MacExplorer.Services.Plugins;
using Xunit;

namespace MacExplorer.Tests;

[SupportedOSPlatform("macos")]
public sealed class PluginPipeCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerThatNeverReadsStdinCannotBlockTimeoutOrCancellation(bool userCancellation)
    {
        var root = Directory.CreateTempSubdirectory("macexplorer-pipe-").FullName;
        PluginSession? session = null;
        Task<JsonElement>? call = null;
        try
        {
            var plugin = Directory.CreateDirectory(Path.Combine(root, "plugin")).FullName;
            File.Copy(typeof(IFileActionPlugin).Assembly.Location, Path.Combine(plugin, "entry.dll"));
            await File.WriteAllTextAsync(Path.Combine(plugin, "plugin.json"), JsonSerializer.Serialize(new PluginManifest
            {
                Id = "test.pipe", Name = "pipe", Version = "1.0.0", ApiVersion = PluginProtocol.ApiVersion,
                Entry = "entry.dll", Commands = [new() { Id = "run", Title = "run" }]
            }, PluginProtocol.Json));
            var executable = Path.Combine(root, "blocked-worker");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nexec sleep 60\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            session = new PluginSession(executable, "unused.dll", plugin, Path.Combine(root, "work"),
                Path.Combine(root, "worker.log"), () => Task.CompletedTask);
            using var cancellation = new CancellationTokenSource();
            if (userCancellation) cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
            call = session.CallAsync<JsonElement>("execute", new { payload = new string('x', 2 * 1024 * 1024) },
                userCancellation ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(250), cancellation.Token);
            // The outer watchdog is not the assertion: the actual RPC task must finish.
            Assert.Same(call, await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(5))));
            if (userCancellation) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
            else await Assert.ThrowsAsync<TimeoutException>(async () => await call);
        }
        finally
        {
            // Keep the regression bounded even against the old implementation.
            if (session != null)
            {
                try { using var process = Process.GetProcessById(session.ProcessId); if (!process.HasExited) process.Kill(true); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
                if (call != null) { try { await call.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            Directory.Delete(root, true);
        }
    }
}
