#if DEBUG
using System.Diagnostics;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Tools.Testing;

internal static class FeatureUpgradeVolumeSmoke
{
    private sealed class Replace : IFileConflictResolver
    {
        public Task<FileConflictDecision> ResolveAsync(FileConflict conflict, CancellationToken cancellationToken)
            => Task.FromResult(new FileConflictDecision(FileConflictAction.Replace));
    }
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "volume-fixture")).FullName;
        var image = Path.Combine(root, "volume.dmg"); var mount = Directory.CreateDirectory(Path.Combine(root, "mounted")).FullName;
        await Run("create", "-quiet", "-size", "24m", "-fs", "HFS+", "-volname", "FKFeatureFixture", image);
        await Run("attach", "-nobrowse", "-noautoopen", "-mountpoint", mount, image);
        var tasks = new BackgroundTaskManager(); var local = new MacFileService(); FileOperationHistoryService? history = null;
        var files = new CompositeFileService(local, null!, tasks, history: () => history, conflicts: new Replace());
        history = new FileOperationHistoryService(files, tasks: tasks);
        try
        {
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var source = Path.Combine(sourceDirectory, "replace.txt"); var target = Path.Combine(mount, "replace.txt");
            await File.WriteAllTextAsync(source, "new mounted contents"); await File.WriteAllTextAsync(target, "old mounted contents");
            check(local.IsCrossVolume(source, mount), "按设备标识识别自定义挂载位置的跨卷操作");
            await files.CopyWithProgressAsync(source, mount);
            check(await history.UndoLastAsync() && File.ReadAllText(target) == "old mounted contents", "挂载卷替换可撤销并恢复原内容");

            var folder = Directory.CreateDirectory(Path.Combine(sourceDirectory, "folder")).FullName;
            await File.WriteAllTextAsync(Path.Combine(folder, "move.txt"), "cross-volume contents");
            await files.MoveAsync(folder, mount);
            check(!Directory.Exists(folder) && File.ReadAllText(Path.Combine(mount, "folder/move.txt")) == "cross-volume contents", "跨卷移动校验后删除来源");
            check(await history.UndoLastAsync() && File.ReadAllText(Path.Combine(folder, "move.txt")) == "cross-volume contents", "跨卷移动可安全撤销");

            var large = Path.Combine(sourceDirectory, "full.bin"); var oldTarget = Path.Combine(mount, "full.bin");
            await using (var stream = File.Create(large)) stream.SetLength(32 * 1024 * 1024);
            await File.WriteAllTextAsync(oldTarget, "keep on full disk"); var failed = false;
            try { await files.CopyWithProgressAsync(large, mount); }
            catch (Exception ex) when (ex is IOException || ex is AggregateException aggregate && aggregate.Flatten().InnerExceptions.All(e => e is IOException))
            { failed = true; }
            check(failed && File.ReadAllText(oldTarget) == "keep on full disk" && File.Exists(large), "真实空间不足时替换失败，原目标与来源均保留");
        }
        finally
        {
            await tasks.ShutdownAsync(); await history.ClearHistoryAsync();
            await Run("detach", mount);
        }
    }
    private static async Task Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/hdiutil") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await output;
        if (process.ExitCode != 0) throw new IOException(await error);
    }
}
#endif
