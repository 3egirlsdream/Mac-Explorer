using MacExplorer.Services.Impl;
using MacExplorer.PluginSdk;

namespace MacExplorer.Services.Plugins;

internal static class PluginOutputCommitter
{
    public static async Task<string[]> CommitAsync(PluginOutput[] outputs, IReadOnlyList<PluginFile> files,
        string workDirectory, CancellationToken token)
    {
        if (outputs is not { Length: > 0 and <= 100 }) throw new InvalidDataException("插件未返回有效输出文件。");
        token.ThrowIfCancellationRequested();
        var sources = files.Select(file => Path.GetFullPath(file.Path)).ToHashSet(StringComparer.Ordinal);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workDirectory));
        var validated = new List<(string Path, string Destination)>(outputs.Length);
        // Validate the entire batch before exposing any output in a user directory.
        foreach (var output in outputs)
        {
            token.ThrowIfCancellationRequested();
            if (output == null) throw new InvalidDataException("插件输出条目为空。");
            var path = PluginPackage.ContainedPath(root, Path.GetRelativePath(root, output.Path));
            if (!File.Exists(path) || new FileInfo(path).Length == 0 ||
                string.IsNullOrWhiteSpace(output.SuggestedName) || output.SuggestedName is "." or ".." ||
                output.SuggestedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Path.GetFileName(output.SuggestedName) != output.SuggestedName || output.SuggestedName.Contains('\\'))
                throw new InvalidDataException("插件输出文件或建议名称无效。");
            for (var current = path; current != root; current = Path.GetDirectoryName(current)!)
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("插件输出不允许符号链接。");
            var source = ResolveSource(output, files, sources);
            validated.Add((path, Path.Combine(Path.GetDirectoryName(source)!, output.SuggestedName)));
        }
        var results = new List<string>(validated.Count);
        foreach (var (path, destination) in validated)
        {
            // Once the first output is committed, finish the remaining complete outputs.
            results.Add(await CopyOutputAsync(path, destination,
                results.Count == 0 ? token : CancellationToken.None));
        }
        return results.ToArray();
    }

    private static string ResolveSource(PluginOutput output, IReadOnlyList<PluginFile> files, HashSet<string> sources)
    {
        if (output.SourcePath is { Length: > 0 } source)
        {
            var path = Path.GetFullPath(source);
            if (!sources.Contains(path)) throw new InvalidDataException("插件输出引用了本次调用之外的来源文件。");
            return path;
        }
        if (files.Count == 1) return Path.GetFullPath(files[0].Path);
        throw new InvalidDataException("插件一次处理多个文件时，每个输出都必须指定来源文件。");
    }

    internal static Task<string> CommitOutputAsync(string temporaryOutput, string source, string extension, CancellationToken token)
    {
        var destination = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source)
            + (extension.Length == 0 ? "" : "." + extension));
        return CopyOutputAsync(temporaryOutput, destination, token);
    }

    private static Task<string> CopyOutputAsync(string temporaryOutput, string destination, CancellationToken token)
        => NewFileWriter.WriteAsync(destination, async (output, cancellationToken) =>
        {
            await using var input = File.OpenRead(temporaryOutput);
            await input.CopyToAsync(output, cancellationToken);
        }, token);
}
