using System.ComponentModel;
using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FileTasks")]
[Trait("Area", "Memory")]
public sealed class FileCreationDiagnosticsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fk-create-diagnostics-").FullName;
    private string LogDirectory => Path.Combine(_root, "logs");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationFailureWritesPathMessageAndStackWithoutChangingTheException(bool folder)
    {
        var parent = Path.Combine(_root, "file-not-directory");
        await File.WriteAllTextAsync(parent, "keep");
        var manager = new BackgroundTaskManager();
        var service = new MacFileService();
        var label = folder ? "新建文件夹" : "新建文件";
        IOException error;
        using (MemoryDiagnostics.Start(LogDirectory))
        {
            error = await Assert.ThrowsAnyAsync<IOException>(() => manager.RunAsync(
                new FileTaskRequest(BackgroundTaskKind.Create, label, [], parent),
                _ => folder ? service.CreateFolderAsync(parent, "new") : service.CreateFileAsync(parent, "new.txt")));
        }

        var task = Assert.Single(manager.Tasks);
        Assert.Equal(BackgroundTaskState.NeedsAttention, task.State);
        Assert.True(task.CanRetry);
        var record = Assert.Single(Failures());
        Assert.Equal(task.Id, record.GetProperty("taskId").GetString());
        Assert.Equal(label, record.GetProperty("operation").GetString());
        Assert.Equal(parent, record.GetProperty("targetDirectory").GetString());
        Assert.Equal(error.Message, record.GetProperty("message").GetString());
        Assert.Equal(error.GetType().FullName, record.GetProperty("exception").GetString());
        Assert.Equal(error.HResult, record.GetProperty("hResult").GetInt32());
        Assert.Contains(error.Message, record.GetProperty("detail").GetString());
        Assert.NotNull(error.StackTrace);
        Assert.Contains(error.StackTrace.Split('\n')[0], record.GetProperty("detail").GetString());
        Assert.False(record.GetProperty("detailTruncated").GetBoolean());
        Assert.Equal("keep", await File.ReadAllTextAsync(parent));
    }

    [Fact]
    public async Task LongLocalizedErrorsRemainInTheLogWithATruncationMarker()
    {
        var manager = new BackgroundTaskManager();
        var failure = new IOException(new string('错', 20000), new Win32Exception(30, "只读文件系统"));
        using (MemoryDiagnostics.Start(LogDirectory))
        {
            var error = await Assert.ThrowsAsync<IOException>(() => manager.RunAsync<string>(
                new FileTaskRequest(BackgroundTaskKind.Create, "新建文件", [], _root),
                _ => Task.FromException<string>(failure)));
            Assert.Same(failure, error);
        }

        var record = Assert.Single(Failures());
        Assert.Equal(1024, record.GetProperty("message").GetString()!.Length);
        Assert.Equal(4096, record.GetProperty("detail").GetString()!.Length);
        Assert.True(record.GetProperty("detailTruncated").GetBoolean());
        Assert.Equal(30, record.GetProperty("nativeErrorCode").GetInt32());
        Assert.InRange(record.GetRawText().Length, 8193, 48 * 1024);
    }

    [Fact]
    public async Task SuccessfulCancelledAndOtherFailedOperationsDoNotWriteCreationErrors()
    {
        var manager = new BackgroundTaskManager();
        using (MemoryDiagnostics.Start(LogDirectory))
        {
            await manager.RunAsync(new FileTaskRequest(BackgroundTaskKind.Create, "create", [], _root),
                _ => Task.FromResult("ok"));
            await Assert.ThrowsAsync<OperationCanceledException>(() => manager.RunAsync<string>(
                new FileTaskRequest(BackgroundTaskKind.Create, "cancel", [], _root),
                _ => Task.FromException<string>(new OperationCanceledException())));
            await Assert.ThrowsAsync<IOException>(() => manager.RunAsync<string>(
                new FileTaskRequest(BackgroundTaskKind.Copy, "copy", [], _root),
                _ => Task.FromException<string>(new IOException("copy failed"))));
        }
        Assert.Empty(Failures());
    }

    [Fact]
    public async Task NativePublishFailureKeepsItsErrorCodeAndInnerExceptionInTheLog()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var manager = new BackgroundTaskManager();
        using (MemoryDiagnostics.Start(LogDirectory))
        {
            var error = await Assert.ThrowsAsync<IOException>(() => manager.RunAsync(
                new FileTaskRequest(BackgroundTaskKind.Create, "新建文件", [], _root),
                _ => Task.FromResult(NewFileWriter.TryPublish(Path.Combine(_root, "missing"), Path.Combine(_root, "new.txt")))));
            Assert.Equal(2, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
        }
        var record = Assert.Single(Failures());
        Assert.Equal(2, record.GetProperty("nativeErrorCode").GetInt32());
        Assert.Contains(nameof(Win32Exception), record.GetProperty("detail").GetString());
        Assert.Contains(nameof(NewFileWriter), record.GetProperty("detail").GetString());
    }

    private JsonElement[] Failures() => Directory.GetFiles(LogDirectory, "memory-*.jsonl")
        .SelectMany(File.ReadAllLines).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).Where(record => record.GetProperty("name").GetString() == "task.create_failed")
        .Select(record => record.GetProperty("data")).ToArray();

    public void Dispose() => Directory.Delete(_root, true);
}
