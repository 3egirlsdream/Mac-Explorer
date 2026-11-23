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
        var output = await NativeHarnessRunner.CompileAndRunAsync(
            "Tests/Native/FileDropBridgeTests.mm",
            ["-fobjc-arc", "-framework", "AppKit", "-framework", "Foundation"],
            isolated: false);
        Assert.Contains("File drop native checks passed", output);
    }
}
