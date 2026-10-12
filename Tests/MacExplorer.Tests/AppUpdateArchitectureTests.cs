using System.Runtime.InteropServices;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Area", "AppUpdateArchitecture")]
public sealed class AppUpdateArchitectureTests
{
    [Theory]
    [InlineData(Architecture.X64, "macos.zip", "macos-intel.zip")]
    [InlineData(Architecture.X64, "macos-intel.zip", "macos-intel.zip")]
    [InlineData(Architecture.Arm64, "macos.zip", "macos.zip")]
    [InlineData(Architecture.Arm64, "macos-intel.zip", "macos.zip")]
    [InlineData(Architecture.X64, "macos.dmg", "macos-intel.dmg")]
    [InlineData(Architecture.Arm64, "macos-intel.dmg", "macos.dmg")]
    [Trait("Kind", "Unit")]
    public void OfficialAssetsFollowTheRunningArchitectureIncludingRosetta(
        Architecture architecture, string source, string expected)
    {
        var prefix = "https://github.com/3egirlsdream/Mac-Explorer/releases/download/v1.0.55/";
        var result = AppUpdateService.ResolveDownloadUri(
            prefix + "MacExplorer-1.0.55-" + source + "?download=1#asset", "1.0.55", architecture);
        Assert.Equal(prefix + "MacExplorer-1.0.55-" + expected + "?download=1#asset", result.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://example.com/universal.zip")]
    [InlineData("https://example.com/MacExplorer-1.0.54-macos.zip")]
    [InlineData("https://example.com/download?file=MacExplorer-1.0.55-macos.zip")]
    [Trait("Kind", "Unit")]
    public void UnrecognizedOrDifferentVersionUrlsArePreserved(string url)
        => Assert.Equal(url, AppUpdateService.ResolveDownloadUri(url, "1.0.55", Architecture.X64).AbsoluteUri);

    [Theory]
    [InlineData("")]
    [InlineData("/local/update.zip")]
    [InlineData("file:///local/update.zip")]
    [InlineData("ftp://example.com/update.zip")]
    [Trait("Kind", "Unit")]
    public void InvalidDownloadUrlsAreRejected(string url)
        => Assert.Throws<InvalidOperationException>(() =>
            AppUpdateService.ResolveDownloadUri(url, "1.0.55", Architecture.X64));

    [Fact]
    [Trait("Kind", "Unit")]
    public void UnsupportedArchitectureCannotSelectAnOfficialPackage()
        => Assert.Throws<InvalidOperationException>(() => AppUpdateService.ResolveDownloadUri(
            "https://example.com/MacExplorer-1.0.55-macos.zip", "1.0.55", Architecture.X86));

    [Fact(Explicit = true)]
    [Trait("Kind", "Integration")]
    public async Task DownloadedIntelBinaryPassesIntelValidationAndFailsArmValidation()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS.");
        var directory = Path.Combine(Path.GetTempPath(), "fkfinder-update-arch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var binary = Path.Combine(directory, "intel-executable");
            var info = new System.Diagnostics.ProcessStartInfo("/usr/bin/lipo") { UseShellExecute = false };
            foreach (var argument in new[] { "/usr/bin/true", "-thin", "x86_64", "-output", binary })
                info.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(info)!;
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);

            await AppUpdateService.EnsureUpdateArchitectureAsync(binary, Architecture.X64,
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AppUpdateService.EnsureUpdateArchitectureAsync(binary, Architecture.Arm64,
                    TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }

}
