using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "AirDrop")]
public sealed class AirDropNativeTests
{
    [Fact(Explicit = true)]
    public async Task NativeBridgeValidatesFilesAndCompletesSessionsWithoutTransferringFiles()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS AppKit.");
        var output = await NativeHarnessRunner.CompileAndRunAsync(
            "Tests/Native/AirDropBridgeTests.mm",
            ["-fobjc-arc", "-framework", "AppKit", "-framework", "Foundation"],
            isolated: false);
        Assert.Contains("AirDrop native checks passed", output);
    }
}
