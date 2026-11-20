using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacExplorer.Services.Impl;

/// <summary>Stage locally, then publish without replacing an existing entry; unsupported volumes use an exclusive copy.</summary>
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
        var staging = Path.Combine(directory, ".MacExplorer-create-" + Guid.NewGuid().ToString("N") + ".fkfinder-tmp");
        try
        {
            await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await write(stream, token).ConfigureAwait(false);

            for (var index = 1; ; index++)
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(directory, CandidateName(name, index));
                if (await TryPublishFileAsync(staging, destination, token).ConfigureAwait(false)) return destination;
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

    internal static async Task WriteContentAsync(Stream stream, byte[] content, CancellationToken token)
    {
        var context = FileTaskContext.Current;
        if (context?.Task.Request?.Operation is not { Kind: Models.FileOperationPresentation.Paste } operation)
        {
            await stream.WriteAsync(content, token);
            return;
        }
        for (var offset = 0; offset < content.Length;)
        {
            await context.CheckpointAsync();
            var chunk = Math.Min(content.Length - offset, 128 * 1024);
            await stream.WriteAsync(content.AsMemory(offset, chunk), token);
            offset += chunk;
            operation.DoneBytes = offset;
        }
    }

    internal static async Task<bool> TryPublishFileAsync(string staging, string destination, CancellationToken token)
    {
        try { return TryPublish(staging, destination); }
        catch (IOException ex) when (OperatingSystem.IsMacOS()
            && ex.InnerException is Win32Exception { NativeErrorCode: 45 or 78 or 102 })
        {
            // A volume without exclusive rename still supports exclusive file creation.
            using var source = File.OpenRead(staging);
            return await TryCopyNewFileAsync(source, destination, token).ConfigureAwait(false);
        }
    }

    internal static async Task<bool> TryCopyNewFileAsync(Stream source, string destination, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        FileStream output;
        try
        {
            output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)
            || new FileInfo(destination).LinkTarget != null)
        {
            return false;
        }

        string? identity = null;
        using (output)
        {
            try
            {
                if (!OperatingSystem.IsMacOS()) identity = FileIdentitySnapshot.Capture(destination, false)?.Identity;
                if (FileTaskContext.Current is { } context)
                {
                    var buffer = new byte[128 * 1024];
                    while (true)
                    {
                        await context.CheckpointAsync().ConfigureAwait(false);
                        var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                        if (read == 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                }
                else await source.CopyToAsync(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    // exFAT can replace a temporary inode number when data is flushed.
                    // Compare the live handle with the path before closing it.
                    if (OperatingSystem.IsMacOS()) identity = Platforms.MacOS.MacSandboxNative.FileIdentity(output.SafeFileHandle);
                    if (identity != null && FileIdentitySnapshot.Capture(destination, false)?.Identity == identity)
                        File.Delete(destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Debug.WriteLine("清理新建文件副本失败：" + ex); }
                throw;
            }
        }
        return true;
    }

    // Staging and destination must be on the same volume. False means a name
    // collision, not an I/O failure; callers decide whether to choose another name.
    internal static bool TryPublish(string staging, string destination, bool isDirectory = false)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (RenameExclusive(staging, destination, 4 /* RENAME_EXCL */) == 0) return true;
            var error = Marshal.GetLastPInvokeError();
            if (error == 17 /* EEXIST */) return false;
            var cause = new Win32Exception(error);
            throw new IOException("无法保存文件：" + cause.Message, cause);
        }
        try
        {
            if (isDirectory) Directory.Move(staging, destination);
            else File.Move(staging, destination, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)
            || new FileInfo(destination).LinkTarget != null)
        {
            return false;
        }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameExclusive([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);
}
