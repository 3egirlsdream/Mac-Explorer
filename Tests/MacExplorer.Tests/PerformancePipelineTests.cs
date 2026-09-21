using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
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
    public async Task DirectoryEnumeration_UsesRequestedBatchSize()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"macexplorer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            for (var i = 0; i < 300; i++)
                await File.WriteAllTextAsync(
                    Path.Combine(directory, $"file-{i:D3}.txt"),
                    "x",
                    TestContext.Current.CancellationToken);

            var service = new MacFileService();
            var batches = new List<IReadOnlyList<FileSystemEntry>>();
            await foreach (var batch in service.EnumerateDirectoryBatchesAsync(
                               directory,
                               256,
                               TestContext.Current.CancellationToken))
                batches.Add(batch);

            Assert.NotEmpty(batches);
            Assert.Equal(256, batches[0].Count);
            Assert.Equal(300, batches.Sum(batch => batch.Count));
            Assert.All(batches, batch => Assert.InRange(batch.Count, 1, 256));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
