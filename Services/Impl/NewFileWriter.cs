using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacExplorer.Services.Impl;

/// <summary>Stage locally, then publish a complete new file without replacing an existing entry.</summary>
internal static class NewFileWriter
{
    internal static string CandidateName(string name, int index)
    {
        if (index == 1) return name;
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] + " " + index + name[dot..] : name + " " + index;
    }

    internal static async Task<string> WriteAsync(string preferredPath,
        Func<Stream, CancellationToken, Task> write, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(Path.GetFullPath(preferredPath))!;
        var name = Path.GetFileName(preferredPath);
        var staging = Path.Combine(directory, ".MacExplorer-create-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await write(stream, token).ConfigureAwait(false);

            for (var index = 1; ; index++)
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(directory, CandidateName(name, index));
                if (OperatingSystem.IsMacOS())
                {
                    if (RenameExclusive(staging, destination, 4 /* RENAME_EXCL */) == 0) return destination;
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 17 /* EEXIST */)
                        throw new IOException("无法保存文件：" + new Win32Exception(error).Message);
                }
                else
                {
                    try { File.Move(staging, destination, overwrite: false); return destination; }
                    catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
                }
            }
        }
        finally
        {
            // Cleanup failure must not turn a successful commit into a reported failure,
            // or hide the original write error. Staging names are unique to this operation.
            try { File.Delete(staging); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Debug.WriteLine("清理新建文件暂存失败：" + ex); }
        }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameExclusive([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);
}
