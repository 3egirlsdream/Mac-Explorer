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
                try
                {
                    if (TryPublish(staging, destination)) return destination;
                }
                catch (IOException ex) when (OperatingSystem.IsMacOS()
                    && ex.InnerException is Win32Exception { NativeErrorCode: 45 or 78 or 102 })
                {
                    // exFAT may not support RENAME_EXCL. File.Move(false) uses a
                    // check followed by rename on Unix, so it cannot protect this race.
                    await using var source = new FileStream(staging, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (await TryCopyNewFileAsync(source, destination, token).ConfigureAwait(false)) return destination;
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
                await source.CopyToAsync(output, token).ConfigureAwait(false);
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
