using System.Diagnostics;
using System.Text.Json;
using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

internal static class StartupUpdateChecker
{
    internal const string WorkerArgument = "--check-update-worker";

    public static async Task<VersionInfo?> CheckAsync(CancellationToken ct)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定更新检查进程路径");
        var startInfo = CreateStartInfo(executable, typeof(StartupUpdateChecker).Assembly.Location);
        return await CheckAsync(startInfo, ct).ConfigureAwait(false);
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string assemblyPath)
    {
        // nice applies only to the helper; the foreground application's priority stays intact.
        var unix = OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
        var startInfo = new ProcessStartInfo(unix ? "/usr/bin/nice" : executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (unix)
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("19");
            startInfo.ArgumentList.Add(executable);
        }
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add(WorkerArgument);
        return startInfo;
    }

    internal static async Task<VersionInfo?> CheckAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动更新检查进程");
        try
        {
            if (OperatingSystem.IsWindows() && !process.HasExited)
                process.PriorityClass = ProcessPriorityClass.Idle;
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"后台更新检查失败: {error.Trim()}");
            return JsonSerializer.Deserialize<VersionInfo>(output);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    internal static async Task<int> RunWorkerAsync(
        IAppUpdateService updateService, TextWriter output, TextWriter error)
    {
        try
        {
            var version = await updateService.CheckVersionAsync().ConfigureAwait(false);
            await output.WriteAsync(JsonSerializer.Serialize(version)).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
