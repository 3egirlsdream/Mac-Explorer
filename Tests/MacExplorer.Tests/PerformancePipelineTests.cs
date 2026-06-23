using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using Xunit;

namespace MacExplorer.Tests;

public sealed class PerformancePipelineTests
{
    [Fact]
    public void FileSystemEntry_VisualStateRaisesOnlyItemNotifications()
    {
        var entry = new FileSystemEntry { FullPath = "/tmp/photo.png", Name = "photo.png" };
        var changed = new List<string?>();
        entry.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        entry.ThumbnailUrl = "/tmp/cache/photo.png";
        entry.IsCut = true;
        entry.IsSelected = true;

        Assert.Equal([nameof(FileSystemEntry.ThumbnailUrl), nameof(FileSystemEntry.IsCut), nameof(FileSystemEntry.IsSelected)], changed);
    }

    [Fact]
    public async Task DirectoryEnumeration_YieldsBoundedCompleteBatches()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"macexplorer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            for (var i = 0; i < 75; i++)
                await File.WriteAllTextAsync(Path.Combine(directory, $"file-{i:D3}.txt"), "x");

            var service = new MacFileService();
            var entries = new List<FileSystemEntry>();
            await foreach (var batch in service.EnumerateDirectoryBatchesAsync(directory, 32))
            {
                Assert.InRange(batch.Count, 1, 32);
                entries.AddRange(batch);
            }

            Assert.Equal(75, entries.Count);
            Assert.Equal(75, entries.Select(entry => entry.FullPath).Distinct().Count());
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
