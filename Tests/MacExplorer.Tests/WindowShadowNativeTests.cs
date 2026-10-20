using System.Diagnostics;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "Window")]
public sealed class WindowShadowNativeTests
{
    [Fact(Explicit = true)]
    public async Task ShadowStaysOutsideContentTracksWindowAndReleasesOnClose()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS AppKit.");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "MacExplorer.csproj")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        var root = Path.Combine(Path.GetTempPath(), "window-shadow-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var executable = Path.Combine(root, "WindowShadowTests");
            await RunAsync("/usr/bin/clang++", ["-fobjc-arc", "-framework", "AppKit", "-framework", "Foundation",
                "-framework", "QuartzCore",
                Path.Combine(repository.FullName, "Tests/Native/WindowShadowTests.mm"), "-o", executable], timeout.Token);
            var output = await RunAsync("/bin/bash", [Path.Combine(repository.FullName, "Tools/Testing/run-isolated.sh"), executable], timeout.Token);
            Assert.Contains("Window shadow native checks passed", output);
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
