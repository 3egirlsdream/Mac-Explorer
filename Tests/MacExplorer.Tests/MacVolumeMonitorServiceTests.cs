using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;
using System.Text.Json;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "MacVolumeMonitorService")]
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

    [Fact]
    public void EjectFailurePreservesSystemReasonAndLogsCodesWithoutPrivatePaths()
    {
        var root = Directory.CreateTempSubdirectory("eject-diagnostics-");
        try
        {
            using (MemoryDiagnostics.Start(root.FullName))
            {
                var result = MacSandboxNative.ReadEjectResult(false,
                    """{"message":"Cannot eject /Volumes/private-volume","errors":[{"domain":"NSCocoaErrorDomain","code":512},{"domain":"NSPOSIXErrorDomain","code":16}],"processId":1234}""", 15);
                Assert.False(result.Success);
                Assert.Equal("Cannot eject /Volumes/private-volume", result.ErrorMessage);
                Assert.True(MacSandboxNative.ReadEjectResult(true, null, 20).Success);
            }
            var raw = string.Join('\n', Directory.GetFiles(root.FullName, "*.jsonl").Select(File.ReadAllText));
            Assert.DoesNotContain("private-volume", raw);
            var events = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .Where(record => record.GetProperty("name").GetString() == "volume.eject")
                .Select(record => record.GetProperty("data")).ToArray();
            Assert.Equal(2, events.Length);
            Assert.False(events[0].GetProperty("success").GetBoolean());
            Assert.Equal(1234, events[0].GetProperty("processId").GetInt32());
            Assert.Equal(16, events[0].GetProperty("errors")[1].GetProperty("code").GetInt32());
            Assert.True(events[1].GetProperty("success").GetBoolean());
        }
        finally { root.Delete(true); }
    }

}
