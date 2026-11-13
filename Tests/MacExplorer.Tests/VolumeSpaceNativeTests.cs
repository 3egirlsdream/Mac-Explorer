using MacExplorer.Platforms.MacOS;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "VolumeSpace")]
public sealed class VolumeSpaceNativeTests
{
    [Fact(Explicit = true)]
    public void NativeCapacitySupportsUnicodePathsAndDirectoryLinks()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "Requires macOS Foundation.");
        var root = Directory.CreateTempSubdirectory("volume-space-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, "容量 # test")).FullName;
            var link = Path.Combine(root.FullName, "linked-folder");
            Directory.CreateSymbolicLink(link, directory);
            long? expectedTotal = null;
            foreach (var path in new[] { directory, link })
            {
                Assert.True(MacSandboxNative.TryGetVolumeSpace(path, out var available, out var free, out var total));
                Assert.True(total > 0);
                Assert.InRange(free, 0, total);
                Assert.InRange(available, free, total);
                if (expectedTotal.HasValue) Assert.Equal(expectedTotal.Value, total);
                expectedTotal = total;
                var status = FileListStatusFormatter.GetLocalLocationStatus(path, [new(path, free, total, available)]);
                Assert.NotNull(status);
                Assert.DoesNotContain("file_list.storage_free", status.Value.Tooltip);
                Assert.Contains('\n', status.Value.Tooltip);
            }
        }
        finally { root.Delete(true); }
    }
}
