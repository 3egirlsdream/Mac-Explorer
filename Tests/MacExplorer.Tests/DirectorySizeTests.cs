using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "DirectorySize")]
public sealed class DirectorySizeTests
{
    [Fact]
    public async Task CountsNestedAndHiddenFilesWithoutFollowingLinksOrChangingTheListSize()
    {
        var root = Directory.CreateTempSubdirectory("directory-size-").FullName;
        using var access = new DirectoryAccess(Path.Combine(root, "unused.json"), false);
        using var scope = DirectoryAccess.UseForTests(access);
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "folder")).FullName;
            Directory.CreateDirectory(Path.Combine(folder, "nested"));
            await File.WriteAllBytesAsync(Path.Combine(folder, "nested", "file"), new byte[2048]);
            await File.WriteAllBytesAsync(Path.Combine(folder, ".hidden"), new byte[17]);
            await File.WriteAllBytesAsync(Path.Combine(root, "outside"), new byte[4096]);
            Directory.CreateSymbolicLink(Path.Combine(folder, "loop"), folder);
            File.CreateSymbolicLink(Path.Combine(folder, "external"), Path.Combine(root, "outside"));

            Assert.Equal(new DirectorySizeResult(2065, true), await DirectorySizeCalculator.CalculateAsync(folder));
            var entry = await new MacFileService().GetEntryAsync(folder);
            Assert.NotNull(entry);
            Assert.Equal(0L, entry.Size);
            Assert.Equal("--", entry.FormattedSize);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnreadableSubdirectoryProducesAPartialSizeInsteadOfAKnownTotal()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("directory-size-partial-").FullName;
        var blocked = Directory.CreateDirectory(Path.Combine(root, "blocked")).FullName;
        using var access = new DirectoryAccess(Path.Combine(root, "unused.json"), false);
        using var scope = DirectoryAccess.UseForTests(access);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "visible"), new byte[20]);
            await File.WriteAllBytesAsync(Path.Combine(blocked, "hidden"), new byte[40]);
            File.SetUnixFileMode(blocked, UnixFileMode.None);
            Assert.Equal(new DirectorySizeResult(20, false), await DirectorySizeCalculator.CalculateAsync(root));
        }
        finally
        {
            File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task EmptyFolderReturnsKnownZeroAndCancelledRequestDoesNotProbeThePath()
    {
        var root = Directory.CreateTempSubdirectory("directory-size-empty-").FullName;
        using var access = new DirectoryAccess(Path.Combine(root, "unused.json"), false);
        using var scope = DirectoryAccess.UseForTests(access);
        try
        {
            Assert.Equal(new DirectorySizeResult(0, true), await DirectorySizeCalculator.CalculateAsync(root));
            Assert.Equal("0 B", FileSystemEntry.FormatSize(0));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DirectorySizeCalculator.CalculateAsync(Path.Combine(root, "absent"), cancellation.Token));
        }
        finally { Directory.Delete(root, true); }
    }
}
