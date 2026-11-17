using MacExplorer.Services.Impl;
using System.Diagnostics;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "DiskManagement")]
public sealed class DiskInformationNativeTests
{
    [Fact(Explicit = true)]
    public async Task NativeQueryIncludesStartupVolumeWithCapacityAndUniqueIdentities()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var disks = await new DiskInformationService().GetMountedDisksAsync(TestContext.Current.CancellationToken);
        var boot = Assert.Single(disks, disk => disk.Path == "/");
        Assert.False(boot.CanEject);
        Assert.False(boot.IsExternal);
        Assert.True(boot.TotalBytes > 0);
        Assert.True(boot.FreeBytes >= 0);
        Assert.True(boot.AvailableBytes >= boot.FreeBytes);
        Assert.Equal(disks.Count, disks.Select(disk => disk.Identity).Distinct().Count());
        Assert.All(disks, disk => Assert.True(disk.UsedPercent is >= 0 and <= 100));
    }

    [Fact(Explicit = true)]
    public async Task ReadOnlyDiskImageAtCustomMountPointReportsImageAndDisappearsAfterDetach()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "disk-image-" + Guid.NewGuid().ToString("N"))).FullName;
        var image = Path.Combine(root, "fixture.dmg");
        var mount = Directory.CreateDirectory(Path.Combine(root, "mounted")).FullName;
        var attached = false;
        try
        {
            await Run(ct, "create", "-quiet", "-size", "16m", "-fs", "HFS+", "-volname", "DiskManagementFixture", image);
            await Run(ct, "attach", "-readonly", "-noautoopen", "-mountpoint", mount, image);
            attached = true;
            var service = new DiskInformationService();
            var disks = await service.GetMountedDisksAsync(ct);
            var disk = Assert.Single(disks, disk => disk.Path == mount);
            Assert.True(disk.IsDiskImage);
            Assert.True(disk.IsReadOnly);
            Assert.True(disk.CanEject);
            Assert.False(disk.IsExternal);
            Assert.True(disk.TotalBytes > 0);
            await Run(ct, "detach", mount);
            attached = false;
            Assert.DoesNotContain(await service.GetMountedDisksAsync(ct), candidate => candidate.Identity == disk.Identity || candidate.Path == mount);
        }
        finally
        {
            if (attached) await Run(CancellationToken.None, "detach", mount);
            Directory.Delete(root, true);
        }
    }

    private static async Task Run(CancellationToken ct, params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/hdiutil") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await output;
        Assert.True(process.ExitCode == 0, await error);
    }
}
