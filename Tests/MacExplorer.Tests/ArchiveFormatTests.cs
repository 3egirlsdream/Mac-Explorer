using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Archive")]
public sealed class ArchiveFormatTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fk-archive-formats-").FullName;
    internal const string SampleText = "archive sample: 中文 / café\n";
    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", "Archives", name);
    private string Output(string name = "output") => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    [Theory]
    [InlineData("sample.txt.gz")]
    [InlineData("sample.txt.bz2")]
    [InlineData("sample.txt.xz")]
    [InlineData("sample.txt.lz")]
    [InlineData("sample.txt.zst")]
    [InlineData("sample.txt.Z")]
    [InlineData("sample.tar")]
    [InlineData("sample.tar.gz")]
    [InlineData("sample.tar.bz2")]
    [InlineData("sample.tar.xz")]
    [InlineData("sample.tar.lz")]
    [InlineData("sample.tar.zst")]
    [InlineData("sample.tar.Z")]
    public async Task CompressedFilesAndTarWrappersBrowsePreviewAndExtract(string fixture)
    {
        var service = new ArchiveService();
        var path = Fixture(fixture);
        Assert.True(service.IsArchiveFile(path));
        Assert.False(service.IsEncrypted(path));
        var files = await ListFiles(service, path);
        var isTar = fixture.StartsWith("sample.tar", StringComparison.Ordinal);
        Assert.Equal(isTar ? new[] { "docs/中文说明.txt", "empty.txt" } : new[] { "sample.txt" }, files.Keys.Order().ToArray());
        var destination = Output();
        var reports = new List<ArchiveProgress>();
        await service.ExtractAsync(path, destination, new InlineProgress(reports.Add));
        foreach (var key in files.Keys)
        {
            var expected = key == "empty.txt" ? "" : SampleText;
            Assert.Equal(expected, await File.ReadAllTextAsync(Path.Combine(destination, key)));
            var preview = await service.ExtractEntryToTempAsync(path, key);
            try { Assert.Equal(expected, await File.ReadAllTextAsync(preview)); }
            finally { Directory.Delete(Path.GetDirectoryName(preview)!, true); }
        }
        Assert.Equal(100, reports[^1].Percentage);
        Assert.False(reports[^1].IsIndeterminate);
        Assert.True(reports[0].IsIndeterminate);
        Assert.Equal(!isTar, reports.Skip(1).Any(r => r.IsIndeterminate));
        Assert.Empty(Directory.GetFiles(destination, "*.fkfinder-tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("Arc.uncompressed.arc")]
    [InlineData("Arc.crunched.arc")]
    [InlineData("Arc.squeezed.arc")]
    [InlineData("Arj.store.arj")]
    [InlineData("Arj.method1.arj")]
    [InlineData("Arj.method4.arj")]
    [InlineData("Ace.store.ace")]
    [InlineData("Rar.rar")]
    [InlineData("Rar.solid.rar")]
    [InlineData("Rar5.solid.rar")]
    [InlineData("Rar.multi.solid.part01.rar")]
    [InlineData("7Zip.LZMA2.7z")]
    public async Task LegacyAndSolidArchivesMatchIndependentOriginalHashes(string fixture)
    {
        using var expected = JsonDocument.Parse(await File.ReadAllTextAsync(Fixture("upstream-files.json")));
        var service = new ArchiveService();
        var path = Fixture(fixture);
        Assert.False(service.IsEncrypted(path));
        var files = await ListFiles(service, path);
        Assert.Equal(3, files.Count);
        var destination = Output();
        await service.ExtractAsync(path, destination);
        foreach (var (key, _) in files)
        {
            var name = Path.GetFileName(key).ToLowerInvariant();
            var original = expected.RootElement.EnumerateObject().Single(p => Path.GetExtension(p.Name) == Path.GetExtension(name)).Value;
            var extracted = await File.ReadAllBytesAsync(Path.Combine(destination, key));
            var isText = Path.GetExtension(name) == ".txt";
            var hash = original.GetProperty(isText ? "sha256_lf" : "sha256").GetString();
            var actual = isText ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(extracted).Replace("\r\n", "\n")) : extracted;
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(actual)));
            var preview = await service.ExtractEntryToTempAsync(path, key);
            try { Assert.Equal(extracted, await File.ReadAllBytesAsync(preview)); }
            finally { Directory.Delete(Path.GetDirectoryName(preview)!, true); }
        }
    }

    [Theory]
    [InlineData("sample.txt.gz", "测试.GZIP", false)]
    [InlineData("sample.txt.bz2", "测试.BZIP2", false)]
    [InlineData("sample.txt.zst", "测试.ZSTD", false)]
    [InlineData("sample.txt.lz", "测试.LZ", false)]
    [InlineData("sample.tar.gz", "测试.TGZ", true)]
    [InlineData("sample.tar.Z", "测试.TAZ", true)]
    [InlineData("sample.tar.bz2", "测试.TBZ2", true)]
    [InlineData("sample.tar.xz", "测试.TXZ", true)]
    [InlineData("sample.tar.lz", "测试.TLZ", true)]
    [InlineData("sample.tar.zst", "测试.TZSTD", true)]
    [InlineData("sample.tar.xz", "内容实际为XZ.zip", true)]
    public async Task AliasesAndMisleadingExtensionsUseFileContent(string fixture, string name, bool isTar)
    {
        var path = Path.Combine(_root, name);
        File.Copy(Fixture(fixture), path);
        var service = new ArchiveService();
        Assert.True(service.IsArchiveFile(path));
        var files = await ListFiles(service, path);
        Assert.Equal(isTar ? 2 : 1, files.Count);
        if (!isTar) Assert.Equal("测试", Assert.Single(files).Key);
        await service.ExtractAsync(path, Output());
        Assert.Equal(SampleText, await File.ReadAllTextAsync(Path.Combine(Output(), isTar ? "docs/中文说明.txt" : "测试")));
    }

    [Fact]
    public async Task EmbeddedGzipNameAndLongUnicodeFallbackArePreserved()
    {
        var path = Path.Combine(_root, "renamed.gz");
        using (var output = File.Create(path))
        using (var gzip = new SharpCompress.Compressors.Deflate.GZipStream(output, SharpCompress.Compressors.CompressionMode.Compress))
        {
            gzip.FileName = "原始.txt";
            gzip.Write(Encoding.UTF8.GetBytes(SampleText));
        }
        var service = new ArchiveService();
        Assert.Equal("原始.txt", Assert.Single(await ListFiles(service, path)).Key);
        var name = new string('长', 40) + ".txt";
        path = Path.Combine(_root, name + ".xz");
        File.Copy(Fixture("sample.txt.xz"), path);
        Assert.Equal(name, Assert.Single(await ListFiles(service, path)).Key);
    }

    [Theory]
    [InlineData("Ace.method1.ace")]
    [InlineData("Arc.squashed.arc")]
    public async Task UnsupportedMethodsDoNotBecomePasswordPromptsOrLeavePartialFiles(string fixture)
    {
        var service = new ArchiveService();
        Assert.False(service.IsEncrypted(Fixture(fixture)));
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => service.ExtractAsync(Fixture(fixture), Output()));
        Assert.Equal(LocalizationText.Get("archive.unsupported_method"), error.Message);
        Assert.Empty(Directory.GetFiles(Output(), "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("..")]
    public async Task UnsafeEmbeddedGzipNamesFallBackToSourceName(string name)
    {
        var original = await File.ReadAllBytesAsync(Fixture("sample.txt.gz"));
        original[3] |= 8;
        var path = Path.Combine(_root, "safe.txt.gz");
        await File.WriteAllBytesAsync(path, original[..10].Concat(Encoding.UTF8.GetBytes(name + "\0")).Concat(original[10..]).ToArray());
        var service = new ArchiveService();
        Assert.Equal("safe.txt", Assert.Single(await ListFiles(service, path)).Key);
        await service.ExtractAsync(path, Output());
        Assert.Equal(SampleText, await File.ReadAllTextAsync(Path.Combine(Output(), "safe.txt")));
    }

    [Theory]
    [InlineData("Ace.encrypted.ace")]
    [InlineData("Arj.encrypted.arj")]
    public async Task UnsupportedLegacyEncryptionFailsWithoutRequestingAPassword(string fixture)
    {
        var service = new ArchiveService();
        Assert.False(service.IsEncrypted(Fixture(fixture)));
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => service.GetArchiveContentsAsync(Fixture(fixture)));
        Assert.Equal(LocalizationText.Get("archive.encryption_unsupported"), error.Message);
    }

    [Fact]
    public async Task DuplicateAndCaseCollidingPathsKeepAllFileContents()
    {
        var path = Path.Combine(_root, "duplicates.tar.gz");
        WriteTarGzip(path, ("folder/file.txt", "first"), ("folder/file.txt", "second"),
            ("folder/FILE.txt", "third"), ("folder/file 2.txt", "reserved"));
        await new ArchiveService().ExtractAsync(path, Output());
        var contents = new List<string>();
        foreach (var file in Directory.GetFiles(Output(), "*", SearchOption.AllDirectories))
            contents.Add(await File.ReadAllTextAsync(file));
        Assert.Equal(new[] { "first", "reserved", "second", "third" }, contents.Order().ToArray());
    }

    [Theory]
    [InlineData("sample.txt.gz")]
    [InlineData("sample.txt.xz")]
    public async Task CorruptChecksumsDoNotCommitPartialFiles(string fixture)
    {
        var path = Path.Combine(_root, fixture);
        var bytes = await File.ReadAllBytesAsync(Fixture(fixture));
        bytes[^5] ^= 0x40;
        await File.WriteAllBytesAsync(path, bytes);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ArchiveService().ExtractAsync(path, Output()));
        Assert.Equal(LocalizationText.Get("archive.invalid_data"), error.Message);
        Assert.Empty(Directory.GetFiles(Output()));
    }

    [Theory]
    [InlineData("fake.xz", "not an archive")]
    [InlineData("fake.arj", "bad arj")]
    public async Task InvalidArchivesReportFormatErrors(string name, string contents)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, contents);
        var service = new ArchiveService();
        Assert.False(service.IsEncrypted(path));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.GetArchiveContentsAsync(path));
        Assert.Equal(LocalizationText.Get("archive.invalid_data"), error.Message);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("..\\outside.txt")]
    public async Task TarExtractionAndPreviewRejectUnsafePaths(string name)
    {
        var path = Path.Combine(_root, "unsafe.tar.gz");
        WriteTarGzip(path, ("safe.txt", "safe"), (name, "unsafe"));
        var service = new ArchiveService();
        await Assert.ThrowsAsync<IOException>(() => service.ExtractAsync(path, Output()));
        await Assert.ThrowsAsync<IOException>(() => service.ExtractEntryToTempAsync(path, name));
        Assert.Empty(Directory.GetFiles(Output(), "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public async Task LinksAndExistingDestinationSymlinksCannotEscapeExtractionRoot()
    {
        var path = Path.Combine(_root, "link.tar");
        using (var output = File.Create(path))
        using (var writer = new TarWriter(output))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "escape") { LinkName = "../outside" });
        var service = new ArchiveService();
        await Assert.ThrowsAsync<IOException>(() => service.ExtractAsync(path, Output()));
        await Assert.ThrowsAsync<IOException>(() => service.ExtractEntryToTempAsync(path, "escape"));
        var outside = Output("outside");
        Directory.CreateSymbolicLink(Path.Combine(Output(), "docs"), outside);
        await service.ExtractAsync(Fixture("sample.tar.xz"), Output());
        Assert.Empty(Directory.GetFiles(outside));
        Assert.Equal(SampleText, await File.ReadAllTextAsync(Path.Combine(Output(), "docs 2", "中文说明.txt")));
    }

    [Fact]
    public async Task ConflictMappingReservesNamesAlreadyUsedByOtherArchiveEntries()
    {
        var path = Path.Combine(_root, "conflicts.tar.gz");
        WriteTarGzip(path, ("file.txt", "first"), ("file 2.txt", "second"), ("./folder/item.txt", "nested"));
        await File.WriteAllTextAsync(Path.Combine(Output(), "file.txt"), "existing");
        var service = new ArchiveService();
        await service.ExtractAsync(path, Output());
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(Output(), "file.txt")));
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(Output(), "file 3.txt")));
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(Output(), "file 2.txt")));
        Assert.Equal("nested", await File.ReadAllTextAsync(Path.Combine(Output(), "folder/item.txt")));
    }

    [Fact]
    public async Task CancellingInsideSingleFileStreamRemovesPartialOutput()
    {
        var path = WriteLargeGzip();
        using var cts = new CancellationTokenSource();
        var reports = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArchiveService().ExtractAsync(path, Output(),
            new InlineProgress(p => { if (p.CurrentFile == "large.bin" && ++reports == 2) cts.Cancel(); }), cts.Token));
        Assert.True(reports >= 2);
        Assert.Empty(Directory.GetFiles(Output()));
    }

    [Fact]
    public async Task PauseAndResumePreserveDecodedContentAndUnknownProgress()
    {
        var path = WriteLargeGzip();
        var manager = new BackgroundTaskManager();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TasksChanged += () =>
        {
            if (manager.Tasks.Any(t => t.State == BackgroundTaskState.Paused)) paused.TrySetResult();
        };
        var reports = 0;
        var operation = new ArchiveService(manager).ExtractAsync(path, Output(), new InlineProgress(p =>
        {
            if (p.CurrentFile == "large.bin" && ++reports == 2) manager.PauseTask(Assert.Single(manager.Tasks).Id);
        }));
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var task = Assert.Single(manager.Tasks);
            Assert.True(task.IsIndeterminate);
            Assert.False(File.Exists(Path.Combine(Output(), "large.bin")));
            manager.ResumeTask(task.Id);
            await operation.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(BackgroundTaskState.Completed, task.State);
            Assert.Equal(2 * 1024 * 1024, new FileInfo(Path.Combine(Output(), "large.bin")).Length);
        }
        finally { if (!operation.IsCompleted) { manager.CancelTask(manager.Tasks[0].Id); try { await operation; } catch (OperationCanceledException) { } } }
    }

    [Fact]
    public async Task RetryKeepsDestinationMapAndDoesNotClaimPreexistingTemporaryFiles()
    {
        var manager = new BackgroundTaskManager();
        var service = new ArchiveService();
        var destination = Output();
        var source = Path.Combine(_root, "sample.tar.xz");
        File.Copy(Fixture("sample.tar.xz"), source);
        var first = true;
        string? temporary = null;
        Directory.CreateDirectory(Path.Combine(destination, "docs"));
        File.WriteAllText(Path.Combine(destination, "docs", "user.txt"), "existing");
        var operation = manager.RunAsync(new FileTaskRequest(BackgroundTaskKind.Extract, "extract", [source], destination), async context =>
        {
            if (first)
            {
                first = false;
                temporary = Path.Combine(destination, "empty.txt." + context.Task.Id + ".fkfinder-tmp");
                File.WriteAllText(temporary, "owned elsewhere");
            }
            await service.ExtractAsync(source, destination, ct: context.CancellationToken);
            return true;
        });
        await Assert.ThrowsAsync<IOException>(() => operation);
        Assert.Equal("owned elsewhere", await File.ReadAllTextAsync(temporary!));
        Assert.Equal(SampleText, await File.ReadAllTextAsync(Path.Combine(destination, "docs 2", "中文说明.txt")));
        File.Delete(temporary!);
        var task = Assert.Single(manager.Tasks);
        manager.RetryTask(task.Id);
        await manager.WaitForCompletionAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(BackgroundTaskState.Completed, task.State);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination, "docs", "user.txt")));
        Assert.Equal(SampleText, await File.ReadAllTextAsync(Path.Combine(destination, "docs 2", "中文说明.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, "docs 3")));
        Assert.Equal(0, new FileInfo(Path.Combine(destination, "empty.txt")).Length);
        Assert.Empty(Directory.GetFiles(destination, "*.fkfinder-tmp", SearchOption.AllDirectories));
    }

    private string WriteLargeGzip()
    {
        var path = Path.Combine(_root, "large.bin.gz");
        using var output = File.Create(path);
        using var gzip = new GZipStream(output, CompressionLevel.NoCompression);
        gzip.Write(new byte[2 * 1024 * 1024]);
        return path;
    }

    private static void WriteTarGzip(string path, params (string name, string content)[] files)
    {
        using var output = File.Create(path);
        using var gzip = new GZipStream(output, CompressionLevel.Optimal);
        using var tar = new TarWriter(gzip);
        foreach (var (name, content) in files)
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
    }

    private static async Task<Dictionary<string, FileSystemEntry>> ListFiles(ArchiveService service, string path, string prefix = "")
    {
        var files = new Dictionary<string, FileSystemEntry>();
        foreach (var entry in await service.GetArchiveContentsAsync(path, prefix))
        {
            var key = ArchivePathHelper.Parse(entry.FullPath).internalPath;
            if (entry.IsDirectory)
                foreach (var child in await ListFiles(service, path, key)) files.Add(child.Key, child.Value);
            else files.Add(key, entry);
        }
        return files;
    }

    private sealed class InlineProgress(Action<ArchiveProgress> callback) : IProgress<ArchiveProgress>
    { public void Report(ArchiveProgress value) => callback(value); }
    public void Dispose() => Directory.Delete(_root, true);
}
