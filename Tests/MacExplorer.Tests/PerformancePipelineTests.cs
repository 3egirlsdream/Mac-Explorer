using MacExplorer.Indexing;
using MacExplorer.Models;
using Xunit;

namespace MacExplorer.Tests;

public sealed class PerformancePipelineTests
{
    [Fact]
    public void FileSystemEntry_IconDisplayNamePreservesTheNameAndExtensionEnds()
    {
        var entry = new FileSystemEntry
        {
            Name = "a-very-long-file-name.csproj"
        };

        Assert.Equal(entry.Name, entry.IconDisplayName);
        Assert.Equal("a-very-long-file-name.csproj", entry.DisplayName);
        Assert.Equal("a-very-long-file-name.csproj", entry.Name);

        entry = new FileSystemEntry { Name = "a-much-longer-file-name-with-a-distinct-ending.csproj" };
        Assert.StartsWith("a-much-longer", entry.IconDisplayName);
        Assert.EndsWith("csproj", entry.IconDisplayName);
        Assert.Contains("…", entry.IconDisplayName);
    }

    [Fact]
    public async Task IndexUpdate_ObservesCancellation()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"macexplorer-index-{Guid.NewGuid():N}.db");
        try
        {
            using var index = new SqliteFileIndex(databasePath);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                index.UpdateDirectoryAsync("/tmp", [], cts.Token));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
        }
    }
}
