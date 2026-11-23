using System.Diagnostics;
using Xunit;

namespace MacExplorer.Tests;

internal static class NativeHarnessRunner
{
    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MacExplorer.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }

    public static async Task<string> CompileAndRunAsync(
        string sourceRelativePath,
        string[] compilerFlags,
        bool isolated = true,
        string[]? runArguments = null,
        int timeoutSeconds = 45,
        string? workingDirectory = null)
    {
        var repository = FindRepositoryRoot();
        var ownRoot = workingDirectory == null;
        var root = workingDirectory ?? Directory.CreateTempSubdirectory("native-harness-").FullName;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var executable = Path.Combine(root, Path.GetFileNameWithoutExtension(sourceRelativePath));
            var compileArgs = new List<string>(compilerFlags)
            {
                Path.Combine(repository, sourceRelativePath),
                "-o",
                executable
            };
            await RunAsync("/usr/bin/clang++", compileArgs, timeout.Token);
            if (isolated)
            {
                var bashArgs = new List<string> { Path.Combine(repository, "Tools/Testing/run-isolated.sh"), executable };
                if (runArguments != null) bashArgs.AddRange(runArguments);
                return await RunAsync("/bin/bash", bashArgs, timeout.Token);
            }
            return await RunAsync(executable, runArguments ?? [root], timeout.Token);
        }
        finally
        {
            if (ownRoot && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    public static async Task<string> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken token,
        bool includeStderrInOutput = false)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var stdout = await output;
        var stderr = await error;
        Assert.True(process.ExitCode == 0, stdout + stderr);
        return includeStderrInOutput ? stdout + stderr : stdout;
    }
}
