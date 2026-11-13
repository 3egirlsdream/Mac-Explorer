using System.Diagnostics;
using MacExplorer.Platforms.MacOS;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "MacVolumeMonitorService")]
public sealed class VolumeEjectNativeTests
{
    [Theory(Explicit = true)]
    [InlineData("HFS+")]
    [InlineData("APFS")]
    public async Task BusyDiskImageKeepsMountedAndEjectsAfterFileCloses(string fileSystem)
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS volume ejection.");
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("eject-volume-");
        var image = Path.Combine(root.FullName, "fixture.dmg");
        var mount = Directory.CreateDirectory(Path.Combine(root.FullName, "mount")).FullName;
        try
        {
            await RunAsync(token, "create", "-size", "64m", "-fs", fileSystem, "-volname", "Eject Fixture", image);
            await RunAsync(token, "attach", "-nobrowse", "-mountpoint", mount, image);
            var file = Path.Combine(mount, "busy.txt");
            await File.WriteAllTextAsync(file, "test fixture", token);
            using (var stream = File.OpenRead(file))
            {
                var busy = await Task.Run(() => MacSandboxNative.EjectVolumeWithResult(mount), token);
                Assert.False(busy.Success);
                Assert.False(string.IsNullOrWhiteSpace(busy.ErrorMessage));
                Assert.True(File.Exists(file));
            }
            var result = await Task.Run(() => MacSandboxNative.EjectVolumeWithResult(mount), token);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(File.Exists(file));
        }
        finally
        {
            // A failed assertion must still detach only this test's image.
            await RunAsync(CancellationToken.None, "detach", mount, allowFailure: true);
            root.Delete(true);
        }
    }

    private static Task RunAsync(CancellationToken token, params string[] arguments)
        => RunAsync(token, arguments, false);

    private static Task RunAsync(CancellationToken token, string command, string path, bool allowFailure)
        => RunAsync(token, [command, path], allowFailure);

    private static async Task RunAsync(CancellationToken token, string[] arguments, bool allowFailure)
    {
        var start = new ProcessStartInfo("/usr/bin/hdiutil") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var text = await output + await error;
        if (!allowFailure) Assert.True(process.ExitCode == 0, text);
    }
}
