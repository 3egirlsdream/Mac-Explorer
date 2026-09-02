using MacExplorer.Platforms.MacCatalyst.Services;
using Xunit;

namespace MacExplorer.Tests;

public sealed class MacVolumeMonitorServiceTests
{
    [Theory]
    [InlineData(".migration-timemachine")]
    [InlineData(".timemachine")]
    [InlineData(".hidden-volume")]
    [InlineData("com.apple.TimeMachine.localsnapshots")]
    [InlineData("Preboot")]
    [InlineData("Recovery")]
    [InlineData("Update")]
    [InlineData("VM")]
    public void InternalMacOsVolumesAreHiddenFromSidebar(string name)
    {
        Assert.False(MacVolumeMonitorService.ShouldShowInSidebar(name));
    }

    [Theory]
    [InlineData("Work SSD")]
    [InlineData("USB Drive")]
    [InlineData("Time Machine Backups")]
    public void UserVolumesRemainVisibleInSidebar(string name)
    {
        Assert.True(MacVolumeMonitorService.ShouldShowInSidebar(name));
    }
}
