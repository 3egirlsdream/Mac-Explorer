using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

internal readonly record struct ZipRevision(FileIdentitySnapshot Identity, long Size, DateTime Modified);

internal static partial class ZipMutationService
{
    private sealed class Writer
    {
        internal readonly SemaphoreSlim Gate = new(1);
        internal int Users;
    }

    private static readonly Dictionary<string, Writer> Writers = new(StringComparer.Ordinal);
    private const string Library = "libzip.dylib";
    private const uint Utf8 = 2048;
    private const uint KeepEmptyArchive = 16;

    internal static bool IsCandidate(string path) => OperatingSystem.IsMacOS()
        && Path.IsPathFullyQualified(path) && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    internal static ZipRevision? Probe(string path)
    {
        if (!IsCandidate(path)) return null;
        try
        {
            DirectoryAccess.Current.EnsureAccess(path);
            var revision = Capture(path);
            using var archive = Open(path);
            return Capture(path) == revision ? revision : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    internal static bool HasRevision(string path, ZipRevision revision)
    {
        try { return Capture(path) == revision; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static Task<byte[]> ReadTextAsync(string path, string key, int maximum, CancellationToken token) =>
        Task.Run(() =>
        {
            DirectoryAccess.Current.EnsureAccess(path);
            using var archive = Open(path);
            return archive.ReadText(key, maximum, token);
        }, token);

    internal static Task ReplaceTextAsync(string path, string key, byte[] expectedHash, byte[] bytes, int maximum,
        CancellationToken token, Action<double>? progress) =>
        UpdateAsync(path, (archive, ct) =>
        {
            var current = archive.ReadText(key, maximum, ct);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, SHA256.HashData(current)))
                throw new IOException(LocalizationText.Get("archive.edit_changed"));
            archive.Replace(key, bytes);
            return true;
        }, token, progress);

    internal static Task DeleteAsync(string path, IReadOnlyList<string> keys, ZipRevision expected, CancellationToken token,
        Action<double>? progress) => UpdateAsync(path, (archive, _) =>
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!key.EndsWith('/'))
            {
                if (!archive.Entries.ContainsKey(key)) throw Changed();
                entries.Add(key);
                continue;
            }
            if (archive.Kind(key.TrimEnd('/')) != true) throw Changed();
            entries.UnionWith(archive.Entries.Keys.Where(name => name.StartsWith(key, StringComparison.Ordinal)));
        }
        foreach (var key in entries) archive.Delete(key);
        return entries.Count > 0;
    }, token, progress, expected);

    internal static Task<string> RenameAsync(string path, string key, string newName, ZipRevision expected, CancellationToken token,
        Action<double>? progress) => UpdateAsync(path, (archive, _) =>
    {
        if (!SafeKey(newName) || newName.Contains('/')) throw new IOException(LocalizationText.Get("archive.edit_unsupported"));
        var folder = key.EndsWith('/');
        var trimmed = key.TrimEnd('/');
        if (archive.Kind(trimmed) != folder) throw Changed();
        var parentLength = trimmed.LastIndexOf('/') + 1;
        var target = trimmed[..parentLength] + newName;
        if (archive.Kind(target) != null) throw new IOException(LocalizationText.Get("archive.edit_name_exists", newName));
        if (!folder)
        {
            archive.Rename(key, target);
            return target;
        }
        target += "/";
        foreach (var name in archive.Entries.Keys.Where(name => name.StartsWith(key, StringComparison.Ordinal)))
            archive.Rename(name, target + name[key.Length..]);
        return target;
    }, token, progress, expected);

    internal static Task<string> CreateAsync(string path, string directory, string name, byte[]? bytes, bool folder,
        CancellationToken token, Action<double>? progress) => UpdateAsync(path, (archive, _) =>
    {
        archive.EnsureDirectory(directory);
        var key = archive.UniqueName(directory + name, folder);
        if (folder) archive.AddDirectory(key + "/");
        else archive.AddBytes(key, bytes ?? []);
        return key;
    }, token, progress);

    internal static Task<bool> AddAsync(string path, string directory, IReadOnlyList<string> paths, IFileConflictResolver? conflicts,
        CancellationToken token, Action<double>? progress) => UpdateAsync(path, (archive, ct) =>
    {
        archive.EnsureDirectory(directory);
        FileConflictDecision? remaining = null;
        var changed = false;
        foreach (var sourcePath in paths.Distinct(StringComparer.Ordinal))
        {
            if (!Path.IsPathFullyQualified(sourcePath)) continue;
            DirectoryAccess.Current.EnsureAccess(sourcePath);
            var attributes = File.GetAttributes(sourcePath);
            var source = (attributes & FileAttributes.Directory) != 0
                ? (FileSystemInfo)new DirectoryInfo(sourcePath) : new FileInfo(sourcePath);
            if (source.Name.Length == 0 || string.Equals(source.FullName, path, StringComparison.OrdinalIgnoreCase))
                throw new IOException(LocalizationText.Get("archive.edit_unsupported"));
            Add(source, directory + source.Name);
        }
        return changed;

        void Add(FileSystemInfo source, string key)
        {
            ct.ThrowIfCancellationRequested();
            FileTaskContext.Checkpoint();
            if (source.LinkTarget != null || !SafeKey(key))
                throw new IOException(LocalizationText.Get("archive.edit_unsupported"));

            var folder = source is DirectoryInfo;
            var existing = archive.Kind(key);
            if (existing != null)
            {
                var destination = ArchivePathHelper.Build(path, key + (existing.Value ? "/" : ""));
                var canReplace = folder == existing.Value && (folder || archive.Entries[key].Regular);
                var conflict = new FileConflict(
                    new FileSystemEntry { Name = source.Name, FullPath = source.FullName, IsDirectory = folder, LastModified = source.LastWriteTime },
                    new FileSystemEntry { Name = Path.GetFileName(key), FullPath = destination, IsDirectory = existing.Value,
                        LastModified = archive.Entries.GetValueOrDefault(key)?.Modified.LocalDateTime ?? DateTime.MinValue },
                    false, false) { CanReplace = canReplace };
                var decision = remaining ?? ResolveConflict(conflicts, conflict, ct);
                if (decision.ApplyToRemaining) remaining = decision;
                switch (decision.Action)
                {
                    case FileConflictAction.Skip: return;
                    case FileConflictAction.KeepBoth: key = archive.UniqueName(key, folder); existing = null; break;
                    case FileConflictAction.ReplaceOlder when !folder &&
                        source.LastWriteTimeUtc <= archive.Entries.GetValueOrDefault(key)?.Modified.UtcDateTime: return;
                    case FileConflictAction.Replace:
                    case FileConflictAction.ReplaceOlder:
                        if (!canReplace) return;
                        break;
                }
            }

            if (source is DirectoryInfo info)
            {
                if (existing == null) { archive.AddDirectory(key + "/"); changed = true; }
                foreach (var child in info.EnumerateFileSystemInfos())
                    Add(child, key + "/" + child.Name);
            }
            else
            {
                if (string.Equals(source.FullName, path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(LocalizationText.Get("archive.edit_unsupported"));
                archive.AddFile(key, source, replace: existing != null);
                changed = true;
            }
        }
    }, token, progress);

    private static FileConflictDecision ResolveConflict(IFileConflictResolver? resolver, FileConflict conflict, CancellationToken token)
    {
        if (resolver == null) return new(FileConflictAction.KeepBoth);
        var operation = FileTaskContext.Current?.Task.Request?.Operation;
        operation?.BeginConfirmation();
        try { return resolver.ResolveAsync(conflict, token).GetAwaiter().GetResult(); }
        finally { operation?.EndConfirmation(); }
    }

    private static async Task<T> UpdateAsync<T>(string path, Func<ZipHandle, CancellationToken, T> update, CancellationToken token,
        Action<double>? progress, ZipRevision? expected = null)
    {
        var key = BackgroundTaskManager.Normalize(path);
        Writer writer;
        lock (Writers)
        {
            if (!Writers.TryGetValue(key, out writer!)) Writers.Add(key, writer = new());
            writer.Users++;
        }
        var acquired = false;
        try
        {
            await writer.Gate.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            return await Task.Run(() =>
            {
                if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
                DirectoryAccess.Current.EnsureAccess(path);
                var original = Capture(path);
                if (expected != null && expected != original) throw Changed();
                var staging = Path.Combine(Path.GetDirectoryName(path)!, $".fkfinder-zip-{Guid.NewGuid():N}.fkfinder-tmp");
                try
                {
                    T result;
                    using (var streaming = CloneFile(path, staging, 0) == 0
                        ? null : new ZipStreamingSource(path, staging, token))
                    using (var archive = streaming == null ? Open(staging) : streaming.OpenArchive())
                    {
                        result = update(archive, token);
                        if (result is false) return result;
                        archive.Commit(token, progress);
                    }
                    // Preserve permissions, ACLs and Finder tags on the ZIP itself.
                    File.SetUnixFileMode(staging, File.GetUnixFileMode(path));
                    if (CopyFileMetadata(path, staging, IntPtr.Zero, 0x0001 | 0x0004) != 0)
                        throw new IOException(LocalizationText.Get("archive.edit_metadata"), new Win32Exception(Marshal.GetLastPInvokeError()));
                    using (var output = new FileStream(staging, FileMode.Open, FileAccess.Write)) output.Flush(flushToDisk: true);
                    if (Capture(path) != original) throw Changed();
                    token.ThrowIfCancellationRequested();
                    File.Move(staging, path, overwrite: true);
                    return result;
                }
                finally { File.Delete(staging); }
            }, token).ConfigureAwait(false);
        }
        finally
        {
            if (acquired) writer.Gate.Release();
            lock (Writers)
                if (--writer.Users == 0)
                {
                    Writers.Remove(key);
                    writer.Gate.Dispose();
                }
        }
    }

    private static ZipRevision Capture(string path)
    {
        var info = new FileInfo(path);
        if (!IsCandidate(path) || !info.Exists || (info.Attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) != 0
            || FileIdentitySnapshot.Capture(path, content: false) is not { } identity)
            throw new IOException(LocalizationText.Get("archive.edit_unsupported"));
        return new(identity, info.Length, info.LastWriteTimeUtc);
    }

    private static IOException Changed() => new(LocalizationText.Get("archive.edit_changed"));

    private static bool SafeKey(string key) => key.Length > 0 && !key.Contains('\\') && !key.Contains('\0')
        && key.TrimEnd('/').Split('/').All(part => part.Length > 0 && part is not ("." or ".."));

    private static ZipHandle Open(string path)
    {
        using (var source = File.OpenRead(path)) ValidateZipSignature(source);
        return new ZipHandle(OpenNative(path, 0, out _));
    }

    private static void ValidateZipSignature(Stream source)
    {
        Span<byte> signature = stackalloc byte[4];
        if (source.Read(signature) != 4 || signature[0] != 'P' || signature[1] != 'K'
            || (signature[2], signature[3]) is not ((3, 4) or (5, 6)))
            throw new IOException(LocalizationText.Get("archive.edit_unsupported"));
    }

    private sealed record Entry(long Index, long Size, DateTimeOffset Modified, bool Regular);

    private sealed class ZipHandle : IDisposable
    {
        private IntPtr _handle;
        private readonly ZipStreamingSource? _streaming;
        internal Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);

        internal ZipHandle(IntPtr handle, ZipStreamingSource? streaming = null)
        {
            _handle = handle;
            _streaming = streaming;
            if (_handle == IntPtr.Zero) throw new IOException(LocalizationText.Get("archive.edit_unsupported"));
            try
            {
                var count = Count(_handle, 0);
                if (count < 0) throw Error();
                for (var index = 0L; index < count; index++)
                {
                    if (StatIndex(_handle, index, 0, out var stat) != 0 || stat.EncryptionMethod != 0
                        || Marshal.PtrToStringUTF8(stat.Name) is not { } name || !SafeKey(name))
                        throw new IOException(LocalizationText.Get("archive.edit_unsupported"));
                    var regular = GetAttributes(_handle, index, 0, out var platform, out var attributes) == 0
                        && (platform != 3 || (attributes >> 16 & 0xF000) is 0 or 0x8000);
                    if (!Entries.TryAdd(name, new Entry(index, (long)stat.Size, DateTimeOffset.FromUnixTimeSeconds(stat.Modified), regular)))
                        throw new IOException(LocalizationText.Get("archive.edit_unsupported"));
                    AddParents(name);
                }
                if (Entries.Keys.Any(key => !key.EndsWith('/') && _directories.Contains(key)))
                    throw new IOException(LocalizationText.Get("archive.edit_unsupported"));
            }
            catch { Dispose(); throw; }
        }

        private void AddParents(string key)
        {
            var separator = key.LastIndexOf('/');
            while (separator >= 0 && _directories.Add(key[..separator]))
                separator = key.LastIndexOf('/', separator - 1);
        }

        internal bool? Kind(string key) => _directories.Contains(key) ? true : Entries.ContainsKey(key) ? false : null;

        internal void EnsureDirectory(string directory)
        {
            if (directory.Length > 0 && !_directories.Contains(directory.TrimEnd('/'))) throw Changed();
        }

        internal string UniqueName(string key, bool folder)
        {
            var extension = folder ? "" : Path.GetExtension(key);
            var stem = key[..(key.Length - extension.Length)];
            var name = key;
            for (var number = 2; Kind(name) != null; number++) name = $"{stem} {number}{extension}";
            return name;
        }

        internal byte[] ReadText(string key, int maximum, CancellationToken token)
        {
            if (!Entries.TryGetValue(key, out var entry)) throw Changed();
            if (key.EndsWith('/') || !entry.Regular) throw new IOException(LocalizationText.Get("archive.edit_unsupported"));
            if (entry.Size < 0 || entry.Size > maximum) throw new IOException(LocalizationText.Get("editor.text_limit"));
            var file = OpenEntry(_handle, entry.Index, 0);
            if (file == IntPtr.Zero) throw Error();
            try
            {
                using var output = new MemoryStream((int)entry.Size);
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var read = ReadEntry(file, buffer, (ulong)Math.Min(buffer.Length, maximum + 1L - output.Length));
                    if (read < 0) throw new IOException(LocalizationText.Get("archive.edit_unreadable"));
                    if (read == 0) break;
                    output.Write(buffer, 0, (int)read);
                    if (output.Length > maximum) throw new IOException(LocalizationText.Get("editor.text_limit"));
                }
                if (output.Length != entry.Size) throw new IOException(LocalizationText.Get("archive.edit_unreadable"));
                return output.ToArray();
            }
            finally { CloseEntry(file); }
        }

        internal void Delete(string key)
        {
            if (DeleteNative(_handle, Entries[key].Index) != 0) throw Error();
        }

        internal void Rename(string key, string replacement)
        {
            if (RenameNative(_handle, Entries[key].Index, replacement, Utf8) != 0) throw Error();
        }

        internal void AddDirectory(string key)
        {
            var index = AddDirectoryNative(_handle, key, Utf8);
            if (index < 0) throw Error();
            Entries.Add(key, new(index, 0, DateTimeOffset.Now, false));
            AddParents(key);
        }

        internal void Replace(string key, byte[] bytes) => PutBytes(key, bytes, Entries[key].Index);
        internal void AddBytes(string key, byte[] bytes) => PutBytes(key, bytes, null);

        private void PutBytes(string key, byte[] bytes, long? index)
        {
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                Put(key, BufferSource(_handle, pin.AddrOfPinnedObject(), (ulong)bytes.Length, 0), index, bytes.Length, DateTimeOffset.Now);
                _pins.Add(pin);
            }
            catch { pin.Free(); throw; }
        }

        private readonly List<GCHandle> _pins = [];

        internal void AddFile(string key, FileSystemInfo source, bool replace)
        {
            var info = (FileInfo)source;
            using var input = Platforms.MacOS.MacSandboxNative.OpenRegularRead(info.FullName);
            Put(key, FileSource(_handle, info.FullName, 0, -1), replace ? Entries[key].Index : null, info.Length, info.LastWriteTimeUtc);
        }

        private void Put(string key, IntPtr source, long? index, long size, DateTimeOffset modified)
        {
            if (source == IntPtr.Zero) throw Error();
            var result = index is { } oldIndex
                ? ReplaceNative(_handle, oldIndex, source, 0)
                : AddFileNative(_handle, key, source, Utf8);
            if (result < 0) { FreeSource(source); throw Error(); }
            var actual = index ?? result;
            if (SetModified(_handle, actual, modified.ToUnixTimeSeconds(), 0) != 0) throw Error();
            Entries[key] = new(actual, size, modified, true);
            AddParents(key);
        }

        internal void Commit(CancellationToken token, Action<double>? progress)
        {
            token.ThrowIfCancellationRequested();
            if (SetFlag(_handle, KeepEmptyArchive, 1) != 0) throw Error();
            CancelCallback cancel = (_, _) =>
            {
                try
                {
                    FileTaskContext.Checkpoint();
                    return token.IsCancellationRequested ? 1 : 0;
                }
                catch (OperationCanceledException) { return 1; }
            };
            ProgressCallback report = (_, value, _) => progress?.Invoke(value);
            if (RegisterCancel(_handle, cancel, IntPtr.Zero, IntPtr.Zero) != 0
                || (progress != null && RegisterProgress(_handle, 0.01, report, IntPtr.Zero, IntPtr.Zero) != 0))
                throw Error();
            var result = Close(_handle);
            GC.KeepAlive(cancel);
            GC.KeepAlive(report);
            if (result != 0)
            {
                token.ThrowIfCancellationRequested();
                throw Error();
            }
            _handle = IntPtr.Zero;
        }

        private IOException Error()
        {
            var failure = _streaming?.Failure;
            var message = failure?.Message ?? Marshal.PtrToStringUTF8(GetError(_handle)) ?? string.Empty;
            return new IOException(LocalizationText.Get("archive.edit_write_error", message), failure);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) Discard(_handle);
            _handle = IntPtr.Zero;
            foreach (var pin in _pins) pin.Free();
            _pins.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ZipStat
    {
        public ulong Valid;
        public IntPtr Name;
        public ulong Index, Size, CompressedSize;
        public long Modified;
        public uint Crc;
        public ushort CompressionMethod, EncryptionMethod;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CancelCallback(IntPtr archive, IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ProgressCallback(IntPtr archive, double value, IntPtr state);

    [DllImport(Library, EntryPoint = "zip_open")]
    private static extern IntPtr OpenNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, out int error);
    [DllImport(Library, EntryPoint = "zip_get_num_entries")]
    private static extern long Count(IntPtr archive, uint flags);
    [DllImport(Library, EntryPoint = "zip_stat_index")]
    private static extern int StatIndex(IntPtr archive, long index, uint flags, out ZipStat stat);
    [DllImport(Library, EntryPoint = "zip_file_get_external_attributes")]
    private static extern int GetAttributes(IntPtr archive, long index, uint flags, out byte platform, out uint attributes);
    [DllImport(Library, EntryPoint = "zip_fopen_index")]
    private static extern IntPtr OpenEntry(IntPtr archive, long index, uint flags);
    [DllImport(Library, EntryPoint = "zip_fread")]
    private static extern long ReadEntry(IntPtr file, [Out] byte[] buffer, ulong length);
    [DllImport(Library, EntryPoint = "zip_fclose")]
    private static extern int CloseEntry(IntPtr file);
    [DllImport(Library, EntryPoint = "zip_file_rename")]
    private static extern int RenameNative(IntPtr archive, long index, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);
    [DllImport(Library, EntryPoint = "zip_delete")]
    private static extern int DeleteNative(IntPtr archive, long index);
    [DllImport(Library, EntryPoint = "zip_dir_add")]
    private static extern long AddDirectoryNative(IntPtr archive, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);
    [DllImport(Library, EntryPoint = "zip_source_buffer")]
    private static extern IntPtr BufferSource(IntPtr archive, IntPtr bytes, ulong size, int free);
    [DllImport(Library, EntryPoint = "zip_source_file")]
    private static extern IntPtr FileSource(IntPtr archive, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ulong start, long length);
    [DllImport(Library, EntryPoint = "zip_file_add")]
    private static extern long AddFileNative(IntPtr archive, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr source, uint flags);
    [DllImport(Library, EntryPoint = "zip_file_replace")]
    private static extern int ReplaceNative(IntPtr archive, long index, IntPtr source, uint flags);
    [DllImport(Library, EntryPoint = "zip_file_set_mtime")]
    private static extern int SetModified(IntPtr archive, long index, long time, uint flags);
    [DllImport(Library, EntryPoint = "zip_source_free")]
    private static extern void FreeSource(IntPtr source);
    [DllImport(Library, EntryPoint = "zip_set_archive_flag")]
    private static extern int SetFlag(IntPtr archive, uint flag, int value);
    [DllImport(Library, EntryPoint = "zip_register_cancel_callback_with_state")]
    private static extern int RegisterCancel(IntPtr archive, CancelCallback callback, IntPtr free, IntPtr state);
    [DllImport(Library, EntryPoint = "zip_register_progress_callback_with_state")]
    private static extern int RegisterProgress(IntPtr archive, double precision, ProgressCallback callback, IntPtr free, IntPtr state);
    [DllImport(Library, EntryPoint = "zip_strerror")]
    private static extern IntPtr GetError(IntPtr archive);
    [DllImport(Library, EntryPoint = "zip_close")]
    private static extern int Close(IntPtr archive);
    [DllImport(Library, EntryPoint = "zip_discard")]
    private static extern void Discard(IntPtr archive);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "clonefile", SetLastError = true)]
    private static extern int CloneFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string target, int flags);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "copyfile", SetLastError = true)]
    private static extern int CopyFileMetadata([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, IntPtr state, uint flags);
}
