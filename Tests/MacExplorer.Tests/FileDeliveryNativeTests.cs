using System.Diagnostics;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "FileDelivery")]
public sealed class FileDeliveryNativeTests
{
    [Fact(Explicit = true)]
    public async Task FirstDragPreventsOrderingBeforeManagedPreviewAndPreservesClicks()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS AppKit.");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "MacExplorer.csproj")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        var root = Directory.CreateTempSubdirectory("delivery-activation-native-").FullName;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var executable = Path.Combine(root, "FileDeliveryActivationTests");
            await RunAsync("/usr/bin/clang++", ["-fobjc-arc", "-framework", "AppKit", "-framework", "Foundation",
                Path.Combine(repository.FullName, "Tests/Native/FileDeliveryActivationTests.mm"), "-o", executable], timeout.Token);
            var output = await RunAsync("/bin/bash", [Path.Combine(repository.FullName, "Tools/Testing/run-isolated.sh"), executable], timeout.Token);
            Assert.Contains("File delivery activation native checks passed", output);
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
