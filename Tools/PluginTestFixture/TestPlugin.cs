using System.Diagnostics;
using MacExplorer.PluginSdk;

namespace PluginTestFixture;

public sealed class TestPlugin : IFileActionPlugin
{
    public Task<PluginPreparation> PrepareAsync(PluginInvocation invocation, CancellationToken cancellationToken) => Task.FromResult(new PluginPreparation());
    public async Task<PluginResult> ExecuteAsync(PluginInvocation invocation, IProgress<PluginProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new("fixture-started", 1));
        if (invocation.CommandId == "crash") Environment.Exit(23);
        if (invocation.CommandId == "invalid-wire")
        {
            using var output = new StreamWriter(Console.OpenStandardOutput());
            await output.WriteLineAsync("not-json"); await output.FlushAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        if (invocation.CommandId is "hang" or "helper" or "crash-helper")
        {
            using var helper = Process.Start(new ProcessStartInfo("/bin/sleep", "60") { UseShellExecute = false })!;
            using var track = PluginChildProcesses.Track(helper);
            await File.WriteAllTextAsync(Path.Combine(invocation.WorkDirectory, "helper.pid"), helper.Id.ToString(), cancellationToken);
            if (invocation.CommandId == "crash-helper") Environment.Exit(24);
            try { await Task.Delay(Timeout.Infinite, invocation.CommandId == "hang" ? CancellationToken.None : cancellationToken); }
            finally { PluginChildProcesses.KillAll(); }
        }
        if (invocation.CommandId == "cancel") await Task.Delay(Timeout.Infinite, cancellationToken);
        if (invocation.CommandId == "batch")
        {
            var outputs = new List<PluginOutput>();
            for (var index = 0; index < invocation.Files.Length; index++)
            {
                var file = invocation.Files[index];
                progress.Report(new($"fixture-batch-{index}", (index + 1) * 100.0 / invocation.Files.Length)
                    { ShowInTaskPanel = true, TaskTitle = "测试批量" });
                var batchOutput = Path.Combine(invocation.WorkDirectory, index + ".txt");
                await File.WriteAllTextAsync(batchOutput, await File.ReadAllTextAsync(file.Path, cancellationToken), cancellationToken);
                outputs.Add(new(batchOutput, Path.GetFileName(file.Path) + ".copy") { SourcePath = file.Path });
            }
            return new(outputs.ToArray(), []);
        }
        var path = Path.Combine(invocation.WorkDirectory, "result.txt");
        await File.WriteAllTextAsync(path, Environment.ProcessId.ToString(), cancellationToken);
        return new([new(path, "result.txt")], []);
    }
}
