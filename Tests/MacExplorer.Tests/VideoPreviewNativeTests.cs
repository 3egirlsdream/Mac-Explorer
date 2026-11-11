using System.Diagnostics;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "Preview")]
public sealed class VideoPreviewNativeTests
{
    [Fact(Explicit = true)]
    public async Task NativePlayerStartsPausedSupportsPlaybackAndReleasesResources()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS AVKit.");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "MacExplorer.csproj")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        var root = Directory.CreateTempSubdirectory("video-preview-native-");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var executable = Path.Combine(root.FullName, "VideoPreviewTests");
            await RunAsync("/usr/bin/clang++", ["-fobjc-arc", "-std=c++17", "-framework", "AppKit",
                "-framework", "AVKit", "-framework", "AVFoundation", "-framework", "CoreMedia", "-framework", "CoreVideo", "-framework", "QuartzCore",
                Path.Combine(repository.FullName, "Tests/Native/VideoPreviewTests.mm"), "-o", executable], timeout.Token);
            var output = await RunAsync("/bin/bash", [Path.Combine(repository.FullName, "Tools/Testing/run-isolated.sh"), executable], timeout.Token);
            Assert.Contains("Video preview native checks passed", output);
        }
        finally { root.Delete(true); }
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
