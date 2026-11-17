using System.ComponentModel;
using System.IO.Compression;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "ExFat")]
public sealed class ExFatFileCreationTests : IDisposable
{
    private readonly string _root;

    public ExFatFileCreationTests()
    {
        var directory = Environment.GetEnvironmentVariable("MACEXPLORER_EXFAT_TEST_DIRECTORY");
        Assert.SkipWhen(!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(directory),
            "Set MACEXPLORER_EXFAT_TEST_DIRECTORY to an explicitly authorized exFAT test directory.");
        Assert.NotNull(RuntimePaths.TestRoot);
        Assert.True(Directory.Exists(directory));
        Assert.Equal("exfat", new DriveInfo(directory).DriveFormat.ToLowerInvariant());
        _root = Directory.CreateDirectory(Path.Combine(directory, "FKFinder-create-test-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    [Theory(Explicit = true)]
    [InlineData(".txt", null)]
    [InlineData(".docx", "word/document.xml")]
    [InlineData(".xlsx", "xl/workbook.xml")]
    [InlineData(".pptx", "ppt/presentation.xml")]
    public async Task ActualCreationTaskCompletesAndKeepsAnUnlistedNameCollision(string extension, string? packageEntry)
    {
        var manager = new BackgroundTaskManager();
        var operations = new FileOpsViewModel(fileService: new MacFileService(), taskManager: manager);
        var createdNames = new List<string>();
        Task Capture(string name) { createdNames.Add(name); return Task.CompletedTask; }
        await operations.CreateNewFileAsync(_root, [], extension, refreshCallback: Capture);
        var original = Path.Combine(_root, Assert.Single(createdNames));
        if (packageEntry == null) Assert.Empty(await File.ReadAllBytesAsync(original));
        else
        {
            using var archive = ZipFile.OpenRead(original);
            Assert.NotNull(archive.GetEntry(packageEntry));
        }
        var originalBytes = await File.ReadAllBytesAsync(original);
        await operations.CreateNewFileAsync(_root, [], extension, refreshCallback: Capture);
        Assert.Equal(2, createdNames.Count);
        Assert.NotEqual(createdNames[0], createdNames[1]);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(original));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(Path.Combine(_root, createdNames[1])));
        Assert.Equal(2, manager.Tasks.Count);
        Assert.All(manager.Tasks, task => Assert.True(task.State == BackgroundTaskState.Completed, task.ErrorDetail));
        Assert.Empty(Directory.GetFiles(_root, ".MacExplorer-create-*"));
    }

    [Fact(Explicit = true)]
    public async Task UnsupportedExclusiveRenameFallsBackWithoutOverwritingConcurrentFiles()
    {
        var source = Path.Combine(_root, "probe.tmp");
        await File.WriteAllTextAsync(source, "probe");
        var nativeFailure = Assert.Throws<IOException>(() => NewFileWriter.TryPublish(source, Path.Combine(_root, "probe.txt")));
        Assert.Equal(45, Assert.IsType<Win32Exception>(nativeFailure.InnerException).NativeErrorCode);
        File.Delete(source);

        var desired = Path.Combine(_root, "同名文件.txt");
        await File.WriteAllTextAsync(desired, "original user's file");
        var paths = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            NewFileWriter.WriteAsync(desired, (output, token) =>
                output.WriteAsync(new byte[] { (byte)index }.AsMemory(), token).AsTask())));
        Assert.Equal(16, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("original user's file", await File.ReadAllTextAsync(desired));
        for (var index = 0; index < paths.Length; index++)
            Assert.Equal(new byte[] { (byte)index }, await File.ReadAllBytesAsync(paths[index]));

        var directoryCollision = Directory.CreateDirectory(Path.Combine(_root, "folder.txt")).FullName;
        var next = await NewFileWriter.WriteAsync(directoryCollision, (_, _) => Task.CompletedTask);
        Assert.NotEqual(directoryCollision, next);
        Assert.True(Directory.Exists(directoryCollision));
        Assert.Empty(Directory.GetFiles(_root, ".MacExplorer-create-*"));
    }

    [Fact(Explicit = true)]
    public async Task CopyFailureAndCancellationCleanUpPartialOutputsOnExFat()
    {
        var failed = Path.Combine(_root, "failed.txt");
        using var broken = new NewFileWriterCompatibilityTests.CallbackSource(async (output, token) =>
        {
            await output.WriteAsync("partial"u8.ToArray(), token);
            Assert.IsType<FileStream>(output).Flush(flushToDisk: true);
            throw new IOException("simulated copy failure");
        });
        await Assert.ThrowsAsync<IOException>(() => NewFileWriter.TryCopyNewFileAsync(broken, failed));
        Assert.False(File.Exists(failed));

        var cancelled = Path.Combine(_root, "cancelled.txt");
        using var cancellation = new CancellationTokenSource();
        using var interrupted = new NewFileWriterCompatibilityTests.CallbackSource(async (output, token) =>
        {
            await output.WriteAsync("partial"u8.ToArray(), token);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewFileWriter.TryCopyNewFileAsync(interrupted, cancelled, cancellation.Token));
        Assert.False(File.Exists(cancelled));
    }

    [Fact(Explicit = true)]
    public async Task FailedCopyDoesNotDeleteAConcurrentReplacementOnExFat()
    {
        var destination = Path.Combine(_root, "replaced.txt");
        using var source = new NewFileWriterCompatibilityTests.CallbackSource(async (output, token) =>
        {
            await output.WriteAsync("partial"u8.ToArray(), token);
            Assert.IsType<FileStream>(output).Flush(flushToDisk: true);
            File.Move(destination, Path.Combine(_root, "our-output.txt"));
            await File.WriteAllTextAsync(destination, "other writer");
            throw new IOException("simulated copy failure");
        });
        await Assert.ThrowsAsync<IOException>(() => NewFileWriter.TryCopyNewFileAsync(source, destination));
        Assert.Equal("other writer", await File.ReadAllTextAsync(destination));
    }

    [Fact(Explicit = true)]
    public async Task FolderCreationStillCompletesOnExFat()
    {
        var manager = new BackgroundTaskManager();
        var operations = new FileOpsViewModel(fileService: new MacFileService(), taskManager: manager);
        await operations.CreateNewFolderAsync(_root, []);
        Assert.Single(Directory.GetDirectories(_root));
        Assert.Equal(BackgroundTaskState.Completed, Assert.Single(manager.Tasks).State);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
