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
        var output = await NativeHarnessRunner.CompileAndRunAsync(
            "Tests/Native/VideoPreviewTests.mm",
            ["-fobjc-arc", "-std=c++17", "-framework", "AppKit", "-framework", "AVKit",
                "-framework", "AVFoundation", "-framework", "CoreMedia", "-framework", "CoreVideo", "-framework", "QuartzCore"],
            timeoutSeconds: 90);
        Assert.Contains("Video preview native checks passed", output);
    }
}
