using System.Diagnostics;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "FileDrop")]
public sealed class FileDropNativeTests
{
    [Fact(Explicit = true)]
    public async Task NativeBridgeReceivesPromisedFilesAndImagesWithoutOverwriting()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS AppKit.");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "MacExplorer.csproj")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        var root = Path.Combine(Path.GetTempPath(), "file-drop-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var executable = Path.Combine(root, "FileDropBridgeTests");
            await RunAsync("/usr/bin/clang++", ["-fobjc-arc", "-framework", "AppKit", "-framework", "Foundation",
                Path.Combine(repository.FullName, "Tests/Native/FileDropBridgeTests.mm"), "-o", executable], timeout.Token);
            var output = await RunAsync(executable, [root], timeout.Token);
            Assert.Contains("File drop native checks passed", output);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var text = await output;
        Assert.True(process.ExitCode == 0, text + await error);
        return text;
    }
}
