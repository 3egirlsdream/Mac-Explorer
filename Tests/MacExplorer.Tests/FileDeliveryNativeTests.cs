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
        var output = await NativeHarnessRunner.CompileAndRunAsync(
            "Tests/Native/FileDeliveryActivationTests.mm",
            ["-fobjc-arc", "-framework", "AppKit", "-framework", "Foundation", "-framework", "QuartzCore"]);
        Assert.Contains("File delivery activation native checks passed", output);
    }
}
