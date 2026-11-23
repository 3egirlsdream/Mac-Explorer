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
        var output = await NativeHarnessRunner.CompileAndRunAsync(
            "Tests/Native/WindowShadowTests.mm",
            ["-fobjc-arc", "-framework", "AppKit", "-framework", "Foundation", "-framework", "QuartzCore"]);
        Assert.Contains("Window shadow native checks passed", output);
    }
}
