using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MacExplorer.Services.Plugins;

internal static class PluginOutputCommitter
{
    public static async Task<string[]> CommitAsync(MacExplorer.PluginSdk.PluginOutput[] outputs, string source,
        string workDirectory, CancellationToken token)
    {
        if (outputs is not { Length: > 0 and <= 100 }) throw new InvalidDataException("插件未返回有效输出文件。");
        foreach (var output in outputs)
        {
            var path = PluginPackage.ContainedPath(workDirectory, Path.GetRelativePath(workDirectory, output.Path));
            if (!File.Exists(path) || new FileInfo(path).Length == 0 ||
                string.IsNullOrWhiteSpace(output.SuggestedName) || output.SuggestedName is "." or ".." ||
                Path.GetFileName(output.SuggestedName) != output.SuggestedName || output.SuggestedName.Contains('\\'))
                throw new InvalidDataException("插件输出文件或建议名称无效。");
            for (var current = path; current != Path.GetFullPath(workDirectory); current = Path.GetDirectoryName(current)!)
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("插件输出不允许符号链接。");
        }
        var results = new List<string>();
        foreach (var output in outputs)
        {
            var destination = Path.Combine(Path.GetDirectoryName(source)!, output.SuggestedName);
            // Once the first output is committed, finish the remaining complete outputs.
            results.Add(await CommitOutputAsync(output.Path, destination, Path.GetExtension(destination).TrimStart('.'),
                results.Count == 0 ? token : CancellationToken.None));
        }
        return results.ToArray();
    }

    internal static async Task<string> CommitOutputAsync(string temporaryOutput, string source, string extension, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(source)!;
        // Stage on the destination volume, then rename without overwrite. A cancelled
        // copy never exposes a partial file under the final filename.
        var staging = Path.Combine(directory, ".MacExplorer-convert-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var input = File.OpenRead(temporaryOutput))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, token);
            var stem = Path.GetFileNameWithoutExtension(source);
            for (var index = 1; ; index++)
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(directory, stem + (index == 1 ? "" : " " + index) + (extension.Length == 0 ? "" : "." + extension));
                if (RenameExclusive(staging, target, 4 /* RENAME_EXCL */) == 0) return target;
                var error = Marshal.GetLastPInvokeError();
                if (error != 17 /* EEXIST */) throw new IOException("无法保存转换文件：" + new Win32Exception(error).Message);
            }
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameExclusive([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string target, uint flags);

}
