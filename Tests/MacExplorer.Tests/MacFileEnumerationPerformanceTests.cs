using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using Xunit;

namespace MacExplorer.Tests;

public sealed class MacFileEnumerationPerformanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"file-enumeration-performance-{Guid.NewGuid():N}");

    public MacFileEnumerationPerformanceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CachedEnumeration_PreservesMetadataHiddenEntriesAndBundlePresentation()
    {
        var token = TestContext.Current.CancellationToken;
        foreach (var name in new[] { "ordinary.txt", "PHOTO.JPG", ".hidden", "空 格.md" })
        {
            var path = Path.Combine(_root, name);
            await File.WriteAllBytesAsync(path, new byte[123], token);
            File.SetLastWriteTimeUtc(path, new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        }
        foreach (var name in new[] { "folder", "Example.app", "Photo Booth Library", ".hidden-folder" })
            Directory.CreateDirectory(Path.Combine(_root, name));

        var service = new MacFileService();
        var legacy = await service.GetDirectoryContentsAsync(_root, token);
        var batched = await ReadAllAsync(service, _root, token);
        Assert.Equal(8, legacy.Count);
        Assert.Equal(8, batched.Count);
        foreach (var actual in legacy.Concat(batched))
        {
            var expected = await service.GetEntryAsync(actual.FullPath);
            Assert.NotNull(expected);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.IsDirectory, actual.IsDirectory);
            Assert.Equal(expected.Size, actual.Size);
            Assert.Equal(expected.Extension, actual.Extension);
            Assert.Equal(expected.IconKey, actual.IconKey);
            Assert.Equal(expected.IsHidden, actual.IsHidden);
            Assert.Equal(expected.IsSymbolicLink, actual.IsSymbolicLink);
            Assert.Equal(expected.IsWritable, actual.IsWritable);
            Assert.Equal(File.GetLastWriteTime(actual.FullPath), actual.LastModified);
            Assert.Equal(File.GetCreationTime(actual.FullPath), actual.Created);
            Assert.True(actual.IsReadable);
        }
        foreach (var snapshot in new[] { legacy, batched })
        {
            foreach (var name in new[] { "ordinary.txt", "PHOTO.JPG", ".hidden", "空 格.md" })
            {
                var file = Assert.Single(snapshot, entry => entry.Name == name);
                Assert.Equal(Path.Combine(_root, name), file.FullPath);
                Assert.False(file.IsDirectory);
                Assert.False(file.IsSymbolicLink);
                Assert.Equal(123, file.Size);
                Assert.True(file.IsReadable);
                Assert.True(file.IsWritable);
                Assert.Equal(name == ".hidden", file.IsHidden);
            }
            foreach (var name in new[] { "folder", "Example.app", "Photo Booth Library", ".hidden-folder" })
            {
                var folder = Assert.Single(snapshot, entry => entry.Name == name);
                Assert.True(folder.IsDirectory);
                Assert.False(folder.IsSymbolicLink);
                Assert.Equal(0, folder.Size);
                Assert.Equal(name == ".hidden-folder", folder.IsHidden);
                Assert.Equal(name is "Example.app" or "Photo Booth Library" ? "app-bundle" : "folder", folder.IconKey);
            }
            Assert.Equal(".jpg", snapshot.Single(entry => entry.Name == "PHOTO.JPG").Extension);
            Assert.Equal("file-image", snapshot.Single(entry => entry.Name == "PHOTO.JPG").IconKey);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(2048)]
    public async Task Batches_PreserveEveryEntryAndRespectSizeClamping(int batchSize)
    {
        var token = TestContext.Current.CancellationToken;
        const int count = 257;
        for (var i = 0; i < count; i++)
            await File.WriteAllBytesAsync(Path.Combine(_root, $"file-{i}.txt"), Array.Empty<byte>(), token);
        var service = new MacFileService();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var received = 0;
        await foreach (var batch in service.EnumerateDirectoryBatchesAsync(_root, batchSize, token))
        {
            Assert.InRange(batch.Count, 1, Math.Clamp(batchSize, 32, 1024));
            foreach (var entry in batch) Assert.True(paths.Add(entry.FullPath));
            received += batch.Count;
        }
        Assert.Equal(count, received);
    }

    [Fact]
    public async Task CancelledEnumeration_DoesNotReturnACompletedSnapshot()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "file.txt"), "test", cancelled.Token);
        cancelled.Cancel();
        var service = new MacFileService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.GetDirectoryContentsAsync(_root, cancelled.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ReadAllAsync(service, _root, cancelled.Token);
        });
    }

    [Fact]
    public async Task StoppingAfterTheFirstBatch_CanDisposeTheProducer()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        for (var i = 0; i < 257; i++)
            await File.WriteAllBytesAsync(Path.Combine(_root, $"file-{i}.txt"), Array.Empty<byte>(), timeout.Token);
        var enumerator = new MacFileService().EnumerateDirectoryBatchesAsync(_root, 32, timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        try
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(32, enumerator.Current.Count);
        }
        finally
        {
            await enumerator.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task SymbolicLinks_KeepTheirOwnPathAndDirectoryClassification()
    {
        if (OperatingSystem.IsWindows()) return; // This service targets macOS/Unix semantics.
        var token = TestContext.Current.CancellationToken;
        var targetFile = Path.Combine(_root, "target.txt");
        var targetDirectory = Path.Combine(_root, "target-directory");
        await File.WriteAllBytesAsync(targetFile, new byte[37], token);
        Directory.CreateDirectory(targetDirectory);
        var fileLink = Path.Combine(_root, "file-link.txt");
        var directoryLink = Path.Combine(_root, "directory-link");
        File.CreateSymbolicLink(fileLink, targetFile);
        Directory.CreateSymbolicLink(directoryLink, targetDirectory);
        File.CreateSymbolicLink(Path.Combine(_root, "dangling-link"), Path.Combine(_root, "missing-target"));
        var service = new MacFileService();

        foreach (var entries in new[]
        {
            await service.GetDirectoryContentsAsync(_root, token),
            await ReadAllAsync(service, _root, token)
        })
        {
            var file = Assert.Single(entries, entry => entry.FullPath == fileLink);
            Assert.True(file.IsSymbolicLink);
            Assert.False(file.IsDirectory);
            Assert.Equal(new FileInfo(fileLink).Length, file.Size);
            var directory = Assert.Single(entries, entry => entry.FullPath == directoryLink);
            Assert.True(directory.IsSymbolicLink);
            Assert.True(directory.IsDirectory);
            Assert.Contains(entries, entry => entry.FullPath == targetFile);
            Assert.Contains(entries, entry => entry.FullPath == targetDirectory);
        }
    }

    [Fact]
    public async Task EmptyAndMissingDirectories_ReturnNoEntries()
    {
        var token = TestContext.Current.CancellationToken;
        var service = new MacFileService();
        foreach (var path in new[] { _root, Path.Combine(_root, "absent") })
        {
            Assert.Empty(await service.GetDirectoryContentsAsync(path, token));
            Assert.Empty(await ReadAllAsync(service, path, token));
        }
    }

    [Fact]
    public async Task EnumerationFactory_SkipsADisappearedFileWithoutReturningAPartialEntry()
    {
        var path = Path.Combine(_root, "disappearing.txt");
        await File.WriteAllTextAsync(path, "gone", TestContext.Current.CancellationToken);
        var info = new FileInfo(path); // Do not prime the metadata cache before deletion.
        File.Delete(path);
        var service = new MacFileService();
        Assert.Null(service.CreateEntryForEnumeration(info));

        var surviving = Path.Combine(_root, "surviving.txt");
        await File.WriteAllTextAsync(surviving, "present", TestContext.Current.CancellationToken);
        Assert.NotNull(service.CreateEntryForEnumeration(new FileInfo(surviving)));
        Assert.Single(await service.GetDirectoryContentsAsync(_root, TestContext.Current.CancellationToken));
        Assert.Single(await ReadAllAsync(service, _root, TestContext.Current.CancellationToken));
    }

    private static async Task<IReadOnlyList<FileSystemEntry>> ReadAllAsync(
        MacFileService service, string path, CancellationToken token)
    {
        var entries = new List<FileSystemEntry>();
        await foreach (var batch in service.EnumerateDirectoryBatchesAsync(path, cancellationToken: token))
            entries.AddRange(batch);
        return entries;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
