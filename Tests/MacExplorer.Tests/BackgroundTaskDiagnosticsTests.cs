using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FileTasks")]
public sealed class BackgroundTaskDiagnosticsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailureDetailsAreWrittenForQueuedAndLegacyTasks(bool queued, bool reported)
    {
        var root = Directory.CreateTempSubdirectory("fk-task-diagnostics-").FullName;
        try
        {
            var manager = new BackgroundTaskManager();
            var detail = new string('错', 3000) + "\n   at DeveloperOnly.Frame()";
            BackgroundTaskInfo task;
            using (MemoryDiagnostics.Start(root))
            {
                if (queued)
                {
                    var request = new FileTaskRequest(BackgroundTaskKind.Copy, "复制", [], root);
                    if (reported)
                        await manager.RunAsync(request, context =>
                        {
                            manager.FailTask(context.Task.Id, "copy failed", detail);
                            return Task.FromResult(false);
                        });
                    else
                        await Assert.ThrowsAsync<IOException>(() => manager.RunAsync<bool>(request,
                            _ => Task.FromException<bool>(new IOException(detail))));
                    task = Assert.Single(manager.Tasks);
                }
                else
                {
                    task = manager.AddTask("复制", BackgroundTaskKind.Copy);
                    manager.FailTask(task.Id, "copy failed", detail);
                }
            }

            var records = Directory.GetFiles(root, "memory-*.jsonl")
                .SelectMany(File.ReadAllLines).Select(line =>
                {
                    using var document = JsonDocument.Parse(line);
                    return document.RootElement.Clone();
                }).Where(record => record.GetProperty("name").GetString() == "task.failed").ToArray();
            var data = Assert.Single(records).GetProperty("data");
            Assert.Equal(task.Id, data.GetProperty("taskId").GetString());
            Assert.Equal("Copy", data.GetProperty("kind").GetString());
            Assert.Contains(detail, data.GetProperty("detail").GetString());
            Assert.Equal(queued && !reported ? detail[..1024] : "copy failed", data.GetProperty("message").GetString());
        }
        finally { Directory.Delete(root, true); }
    }
}
