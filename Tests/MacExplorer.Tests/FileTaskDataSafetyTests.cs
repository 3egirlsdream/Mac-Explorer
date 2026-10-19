using System.IO.Compression;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FileTasks")]
public sealed class FileTaskDataSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fk-task-data-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task RetryKeepsItsOutputNameAndDoesNotClaimAnExistingTemporaryFile()
    {
        Directory.CreateDirectory(_root); var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var source = Path.Combine(_root, "sample.bin"); await File.WriteAllTextAsync(source, "file contents");
        var manager = new BackgroundTaskManager(); var first = true; string? temporary = null;
        var operation = manager.RunAsync(new FileTaskRequest(BackgroundTaskKind.Copy, "copy", [source], target), async context =>
        {
            temporary = Path.Combine(target, "sample.bin." + context.Task.Id + ".fkfinder-tmp");
            if (first) { first = false; await File.WriteAllTextAsync(temporary, "preexisting contents"); }
            return await new MacFileService().CopyWithProgressAsync(source, target, ct: context.CancellationToken);
        });
        await Assert.ThrowsAsync<AggregateException>(() => operation);
        Assert.Equal("preexisting contents", await File.ReadAllTextAsync(temporary!));
        Assert.Equal("file contents", await File.ReadAllTextAsync(source));
        File.Delete(temporary!); var task = Assert.Single(manager.Tasks);
        manager.RetryTask(task.Id); await manager.WaitForCompletionAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskState.Completed, task.State);
        Assert.Equal("file contents", await File.ReadAllTextAsync(Path.Combine(target, "sample.bin")));
        Assert.Single(Directory.GetFiles(target));
    }
    [Fact]
    public async Task ExtractionRejectsTraversalWithoutTouchingOutsideFiles()
    {
        Directory.CreateDirectory(_root); var output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        var path = Path.Combine(_root, "unsafe.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var stream = new StreamWriter(archive.CreateEntry("../outside.txt").Open())) await stream.WriteAsync("unsafe");
        await Assert.ThrowsAsync<IOException>(() => new ArchiveService().ExtractAsync(path, output));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }
    [Fact]
    public void OnlyConcurrencySettingsSurviveANewManagerInstance()
    {
        var settings = new ShortcutMemorySettings(); var manager = new BackgroundTaskManager(settings: settings);
        manager.MaxConcurrency = 4; manager.AddTask("ephemeral");
        var next = new BackgroundTaskManager(settings: settings);
        Assert.Equal(4, next.MaxConcurrency); Assert.Empty(next.Tasks);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
