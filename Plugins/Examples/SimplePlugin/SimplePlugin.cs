using MacExplorer.PluginSdk;

public sealed class SimplePlugin : IFileActionPlugin
{
    public Task<PluginPreparation> PrepareAsync(PluginInvocation invocation, CancellationToken token)
    {
        if (invocation.CommandId != "copy" || invocation.Files.Length != 1 ||
            invocation.Files[0].Source != "local" || invocation.Files[0].IsDirectory ||
            !string.Equals(Path.GetExtension(invocation.Files[0].Path), ".txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择一个本地文本文件。");
        return Task.FromResult(new PluginPreparation());
    }
    public async Task<PluginResult> ExecuteAsync(PluginInvocation invocation, IProgress<PluginProgress> progress, CancellationToken token)
    {
        await PrepareAsync(invocation, token);
        progress.Report(new("正在生成副本"));
        var output = Path.Combine(invocation.WorkDirectory, "copy.txt");
        await using var source = File.OpenRead(invocation.Files[0].Path);
        await using var destination = File.Create(output);
        await source.CopyToAsync(destination, token);
        return new([new(output, "文本副本.txt")], []);
    }
}
