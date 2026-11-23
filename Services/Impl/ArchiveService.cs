using System.Text;
using MacExplorer.Indexing;
using MacExplorer.Models;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;

namespace MacExplorer.Services.Impl;

public partial class ArchiveService : IArchiveService
{
    private readonly IBackgroundTaskManager? _tasks;
    private readonly IFileConflictResolver? _conflicts;
    private readonly ArchivePasswordVault? _passwords;
    public ArchiveService(IBackgroundTaskManager? tasks = null) { _tasks = tasks; }
    internal ArchiveService(IBackgroundTaskManager? tasks, IFileConflictResolver? conflicts, ArchivePasswordVault? passwords)
        : this(tasks) { _conflicts = conflicts; _passwords = passwords; }
    public bool IsArchiveFile(string filePath) => ArchiveFormats.IsArchive(filePath);

    public bool IsEncrypted(string archivePath)
    {
        DirectoryAccess.Current.EnsureAccess(archivePath);
        try
        {
            using var archive = OpenArchive(archivePath, null);
            while (archive.MoveNext())
            {
                if (archive.Entry.IsEncrypted) return true;
                if (archive.Entry.IsDirectory) continue;
                using var stream = archive.OpenEntryStream();
                try { _ = stream.ReadByte(); }
                finally { archive.StopReading(); }
                return false;
            }
            return false;
        }
        catch (SharpCompress.Common.CryptographicException ex) when (IsUnsupportedEncryption(ex))
        {
            return false;
        }
        catch (SharpCompress.Common.CryptographicException)
        {
            return true;
        }
        catch (InvalidFormatException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<IReadOnlyList<FileSystemEntry>> GetArchiveContentsAsync(
        string archivePath, string internalPath = "", string? password = null)
    {
        DirectoryAccess.Current.EnsureAccess(archivePath);
        return ReadArchiveAsync(() =>
        {
            using var archive = OpenArchive(archivePath, password);
            var thumbnailVersion = archive.SupportsAutomaticThumbnails ? GetThumbnailVersion(archivePath) : null;

            var normalizedPrefix = NormalizePath(internalPath);
            if (!string.IsNullOrEmpty(normalizedPrefix) && !normalizedPrefix.EndsWith('/'))
                normalizedPrefix += "/";

            var children = new Dictionary<string, (bool IsDirectory, ArchiveReadEntry? Metadata)>(StringComparer.Ordinal);
            while (archive.MoveNext())
            {
                var entry = archive.Entry;
                var key = NormalizePath(entry.Key);
                if (!key.StartsWith(normalizedPrefix, StringComparison.Ordinal) || key.Length == normalizedPrefix.Length)
                    continue;
                var relative = key.AsSpan(normalizedPrefix.Length);
                var separator = relative.IndexOf('/');
                if (separator == 0) continue;
                var name = (separator < 0 ? relative : relative[..separator]).ToString();
                var isDirectory = separator >= 0 || entry.IsDirectory;
                var direct = separator == -1 || separator == relative.Length - 1;
                children.TryGetValue(name, out var previous);
                children[name] = (isDirectory || previous.IsDirectory, direct ? entry : previous.Metadata);
            }

            var entries = new List<FileSystemEntry>(children.Count);
            var now = DateTime.Now;
            foreach (var (name, (isDir, metadata)) in children)
            {
                var childInternalPath = normalizedPrefix + name + (isDir ? "/" : "");
                var modified = metadata?.LastModifiedTime ?? now;
                var ext = isDir ? "" : Path.GetExtension(name);
                entries.Add(new FileSystemEntry
                {
                    FullPath = ArchivePathHelper.Build(archivePath, childInternalPath),
                    ArchiveThumbnailVersion = !isDir && metadata is { IsEncrypted: false, LinkTarget: null, Size: >= 0 and <= ThumbnailSourceLimit }
                        ? thumbnailVersion : null,
                    Name = name,
                    IsDirectory = isDir,
                    Size = isDir ? 0 : metadata?.Size ?? 0,
                    LastModified = modified,
                    Created = modified,
                    Extension = ext,
                    IconKey = isDir ? "folder" : SqliteFileIndex.ResolveIconKey(ext),
                    IsWritable = false,
                    IsReadable = true
                });
            }

            return (IReadOnlyList<FileSystemEntry>)entries.OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });
    }

    public Task<string> ExtractEntryToTempAsync(string archivePath, string entryKey, string? password = null)
        => ExtractEntryToTempAsync(archivePath, entryKey, password, CancellationToken.None);

    internal async Task<string> ExtractEntryToTempAsync(string archivePath, string entryKey, string? password,
        CancellationToken ct)
        => await ExtractTemporaryEntryAsync(archivePath, entryKey, password, thumbnail: false, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException(LocalizationText.Get("archive.entry_missing", entryKey));

    internal Task<string?> ExtractThumbnailSourceAsync(string archivePath, string entryKey, CancellationToken ct)
        => ExtractTemporaryEntryAsync(archivePath, entryKey, null, thumbnail: true, ct);

    private async Task<string?> ExtractTemporaryEntryAsync(string archivePath, string entryKey, string? password,
        bool thumbnail, CancellationToken ct)
    {
        DirectoryAccess.Current.EnsureAccess(archivePath);
        var source = password != null ? _passwords?.Capture(archivePath) : null;
        var result = await ReadArchiveAsync(() =>
        {
            var normalizedKey = NormalizePath(entryKey).TrimEnd('/');
            using var archive = OpenArchive(archivePath, password, ct);
            if (thumbnail && !archive.SupportsAutomaticThumbnails) return (Path: (string?)null, Verified: false);
            while (archive.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.Entry with { Key = NormalizePath(archive.Entry.Key) };
                if (entry.IsDirectory || entry.Key.TrimEnd('/') != normalizedKey) continue;
                if (thumbnail && (entry.IsEncrypted || entry.Size is not (>= 0 and <= ThumbnailSourceLimit)))
                    return (Path: (string?)null, Verified: false);
                var sessionDir = Path.Combine(RuntimePaths.TemporaryDirectory, "MacExplorer-archive-temp", Guid.NewGuid().ToString("N"));
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(sessionDir);
                else Directory.CreateDirectory(sessionDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                try
                {
                    ValidateEntry(sessionDir, entry);
                    var destFile = ValidateExtractionPath(sessionDir, Path.GetFileName(entry.Key));
                    using var entryStream = archive.OpenEntryStream();
                    using var fileStream = new FileStream(destFile, FileMode.CreateNew, FileAccess.Write);
                    var verified = CopyArchiveEntry(entry, entryStream, fileStream, ct, archive.AuthenticatesEntry,
                        byteLimit: thumbnail ? ThumbnailSourceLimit : long.MaxValue);
                    return (Path: (string?)destFile, Verified: verified);
                }
                catch
                {
                    archive.StopReading();
                    Directory.Delete(sessionDir, recursive: true);
                    throw;
                }
            }
            return (Path: (string?)null, Verified: false);
        }, ct).ConfigureAwait(false);
        try
        {
            if (result.Verified && password != null && _passwords != null)
                await _passwords.SaveAsync(archivePath, source, password, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return result.Path;
        }
        catch
        {
            if (result.Path != null) Directory.Delete(Path.GetDirectoryName(result.Path)!, recursive: true);
            throw;
        }
    }

    internal const long ThumbnailSourceLimit = 16L * 1024 * 1024;

    internal static string? GetThumbnailVersion(string path)
    {
        var info = new FileInfo(path);
        var identity = FileIdentitySnapshot.Capture(path, false);
        return info.Exists && identity != null ? $"{identity.Identity}:{info.Length}:{info.LastWriteTimeUtc.Ticks}" : null;
    }

    public Task<string?> ReadSavedPasswordAsync(string archivePath, CancellationToken ct)
        => _passwords?.ReadAsync(archivePath, ct) ?? Task.FromResult<string?>(null);

    public async Task VerifyBrowsingPasswordAsync(string archivePath, string password, CancellationToken ct)
    {
        if (_passwords?.Enabled != true) return;
        var source = _passwords.Capture(archivePath);
        if (source == null) return;
        var verified = await ReadArchiveAsync(() =>
        {
            using var archive = OpenArchive(archivePath, password, ct);
            if (!archive.SupportsAutomaticThumbnails) return false;
            while (archive.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.Entry;
                if (entry.IsDirectory || !entry.IsEncrypted || entry.Size is not (>= 0 and <= ThumbnailSourceLimit)) continue;
                using var input = archive.OpenEntryStream();
                return CopyArchiveEntry(entry, input, Stream.Null, ct, archive.AuthenticatesEntry,
                    byteLimit: ThumbnailSourceLimit);
            }
            return false;
        }, ct).ConfigureAwait(false);
        if (verified) await _passwords.SaveAsync(archivePath, source, password, ct).ConfigureAwait(false);
    }

    public Task<string> CompressAsync(CompressOptions options, IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
        => FileTaskContext.RunAsync(_tasks, new(BackgroundTaskKind.Compress, LocalizationText.Get("正在压缩..."),
            options.SourcePaths.ToArray(), options.OutputDirectory, options.OutputFileName), context =>
                CompressCoreAsync(options, progress, context.CancellationToken), ct);

    private async Task<string> CompressCoreAsync(
        CompressOptions options,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        DirectoryAccess.Current.EnsureAccess(options.OutputDirectory);
        if (IsOutputInsideSource(options.OutputDirectory, options.SourcePaths))
            throw new IOException(LocalizationText.Get("archive.output_inside_source"));
        foreach (var source in options.SourcePaths) DirectoryAccess.Current.EnsureAccess(source);
        var output = await Task.Run(() =>
        {
            var outputPath = Path.Combine(options.OutputDirectory, options.OutputFileName);
            outputPath = FileTaskContext.Current?.Remember("compress-output", () => GetUniqueFilePath(outputPath))
                ?? GetUniqueFilePath(outputPath);

            var (archiveType, compressionType) = options.Format switch
            {
                ArchiveFormat.Zip => (ArchiveType.Zip, CompressionType.Deflate),
                ArchiveFormat.TarGz => (ArchiveType.Tar, CompressionType.GZip),
                ArchiveFormat.TarBz2 => (ArchiveType.Tar, CompressionType.BZip2),
                _ => (ArchiveType.Zip, CompressionType.Deflate)
            };

            // Collect all files to compress
            var filesToCompress = new List<(string fullPath, string relativePath)>();
            var usedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sourcePath in options.SourcePaths)
            {
                ct.ThrowIfCancellationRequested();
                FileTaskContext.Checkpoint();
                if (File.Exists(sourcePath))
                {
                    var relative = GetUniqueRelativePath(Path.GetFileName(sourcePath), usedRelativePaths);
                    filesToCompress.Add((sourcePath, relative));
                }
                else if (Directory.Exists(sourcePath))
                {
                    var dirName = GetUniqueRelativePath(Path.GetFileName(sourcePath), usedRelativePaths);
                    foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.Combine(dirName, Path.GetRelativePath(sourcePath, file));
                        filesToCompress.Add((file, relative));
                    }
                }
            }

            var tempPath = outputPath + "." + (FileTaskContext.Current?.Task.Id ?? Guid.NewGuid().ToString("N")) + ".fkfinder-tmp";

            if (options.Format == ArchiveFormat.Zip && !string.IsNullOrEmpty(options.Password))
            {
                var source = WriteEncryptedZip(outputPath, tempPath, filesToCompress, options.Password, progress, ct);
                return (Path: outputPath, PasswordSource: source);
            }

            var temporaryOwned = false;
            try
            {
                var writerOptions = new WriterOptions(compressionType)
                {
                    ArchiveEncoding = CreateUtf8ArchiveEncoding()
                };

                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
                using (var writer = WriterFactory.OpenWriter(stream, archiveType, writerOptions))
                {
                    temporaryOwned = true;
                    var total = filesToCompress.Count;
                    for (int i = 0; i < total; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        FileTaskContext.Checkpoint();
                        var (fullPath, relativePath) = filesToCompress[i];
                        CheckCompressionSource(fullPath);
                        writer.Write(relativePath, fullPath);
                        CheckCompressionSource(fullPath);
                        FileTaskContext.Current?.ReportItem(fullPath, outputPath, FileTaskItemState.Completed);

                        progress?.Report(new ArchiveProgress
                        {
                            IsActive = true,
                            Percentage = total > 0 ? (double)(i + 1) / total * 100 : 0,
                            CurrentFile = relativePath,
                            OperationLabel = "正在压缩..."
                        });
                    }
                }

                File.Move(tempPath, outputPath, overwrite: false);
                return (Path: outputPath, PasswordSource: (ArchivePasswordVault.Source?)null);
            }
            catch
            {
                if (temporaryOwned) TryDeleteFile(tempPath);
                throw;
            }
        }, ct);
        if (output.PasswordSource != null && _passwords != null && options.Password is { } password)
            await _passwords.SaveAsync(output.Path, output.PasswordSource, password, ct).ConfigureAwait(false);
        return output.Path;
    }

    internal static bool IsOutputInsideSource(string directory, IReadOnlyList<string> sources)
    {
        var target = Path.TrimEndingDirectorySeparator(ArchiveOutputDirectory(directory));
        foreach (var path in sources.Where(Directory.Exists))
        {
            var source = Path.TrimEndingDirectorySeparator(ArchiveOutputDirectory(path));
            if (target.Equals(source, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(Path.EndsInDirectorySeparator(source) ? source : source + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private ArchivePasswordVault.Source? WriteEncryptedZip(
        string outputPath,
        string tempPath,
        List<(string fullPath, string relativePath)> files,
        string password,
        IProgress<ArchiveProgress>? progress,
        CancellationToken ct)
    {
        var temporaryOwned = false;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipOutputStream(stream) { Password = password })
            {
                temporaryOwned = true;
                var buffer = new byte[64 * 1024];
                var total = files.Count;
                for (int i = 0; i < total; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    FileTaskContext.Checkpoint();
                    var (fullPath, relativePath) = files[i];
                    CheckCompressionSource(fullPath);
                    using var input = File.OpenRead(fullPath);
                    zip.PutNextEntry(new ICSharpCode.SharpZipLib.Zip.ZipEntry(relativePath.Replace('\\', '/'))
                    {
                        AESKeySize = 256,
                        IsUnicodeText = true,
                        DateTime = File.GetLastWriteTime(fullPath)
                    });
                    int read;
                    long copied = 0;
                    while ((read = input.Read(buffer)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        FileTaskContext.Checkpoint();
                        zip.Write(buffer, 0, read);
                        copied += read;
                        progress?.Report(new ArchiveProgress { IsActive = true,
                            Percentage = (i + (input.Length == 0 ? 1 : (double)copied / input.Length)) / total * 100,
                            CurrentFile = relativePath, OperationLabel = "正在压缩..." });
                    }
                    zip.CloseEntry();
                    CheckCompressionSource(fullPath);
                    FileTaskContext.Current?.ReportItem(fullPath, outputPath, FileTaskItemState.Completed);

                    progress?.Report(new ArchiveProgress
                    {
                        IsActive = true,
                        Percentage = total > 0 ? (double)(i + 1) / total * 100 : 0,
                        CurrentFile = relativePath,
                        OperationLabel = "正在压缩..."
                    });
                }

                ct.ThrowIfCancellationRequested();
                FileTaskContext.Checkpoint();
                zip.Finish();
            }

            ct.ThrowIfCancellationRequested();
            var verifiedSource = files.Count > 0 ? _passwords?.Capture(tempPath) : null;
            File.Move(tempPath, outputPath, overwrite: false);
            return verifiedSource == null ? null : verifiedSource with { Paths = [outputPath] };
        }
        catch
        {
            if (temporaryOwned) TryDeleteFile(tempPath);
            throw;
        }
    }

    private static ArchiveReadSession OpenArchive(string archivePath, string? password, CancellationToken ct = default)
        => ArchiveReadSession.Open(archivePath, new ReaderOptions
        {
            ArchiveEncoding = CreateUtf8ArchiveEncoding(),
            Password = password,
            LeaveStreamOpen = false
        }, ct);

    private static Task<T> ReadArchiveAsync<T>(Func<T> read, CancellationToken ct = default)
        => Task.Run(() =>
        {
            try { return read(); }
            catch (SharpCompress.Common.CryptographicException ex) when (IsUnsupportedEncryption(ex))
            { throw new NotSupportedException(LocalizationText.Get("archive.encryption_unsupported"), ex); }
            catch (ICSharpCode.SharpZipLib.Zip.ZipException ex)
            { throw new InvalidDataException(LocalizationText.Get("archive.invalid_data"), ex); }
            catch (MultiVolumeExtractionException ex)
            { throw new NotSupportedException(LocalizationText.Get("archive.missing_volumes"), ex); }
            catch (NotSupportedException ex)
            { throw new NotSupportedException(LocalizationText.Get("archive.unsupported_method"), ex); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException
                || ex is SharpCompressException and not SharpCompress.Common.CryptographicException and not ReaderCancelledException
                    && !ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            { throw new InvalidDataException(LocalizationText.Get("archive.invalid_data"), ex); }
        }, ct);

    private static bool IsUnsupportedEncryption(Exception ex)
        => ex.Message.Contains("currently not supported", StringComparison.OrdinalIgnoreCase);

    private static bool CopyArchiveEntry(ArchiveReadEntry entry, Stream source, Stream destination,
        CancellationToken ct, bool authenticates, Action<long>? report = null, long byteLimit = long.MaxValue)
    {
        var checksum = entry.IsEncrypted && entry.Crc != 0 ? new ICSharpCode.SharpZipLib.Checksum.Crc32() : null;
        var buffer = new byte[64 * 1024];
        long copied = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            FileTaskContext.Checkpoint();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (read > byteLimit - copied) throw new InvalidDataException(LocalizationText.Get("archive.invalid_data"));
            destination.Write(buffer, 0, read);
            checksum?.Update(new ArraySegment<byte>(buffer, 0, read));
            copied += read;
            report?.Invoke(copied);
        }
        // RAR can store a keyed hash in the checksum field. Only a matching plain CRC proves the password here.
        var crcMatches = checksum?.Value == entry.Crc;
        if (entry.Size is { } expected && copied != expected || checksum != null && !crcMatches && !entry.OptionalCrc)
            throw new InvalidDataException(LocalizationText.Get("archive.invalid_data"));
        return entry.IsEncrypted && (authenticates || crcMatches);
    }

    private static void ValidateEntry(string destination, ArchiveReadEntry entry, bool resolveLinks = true)
    {
        ValidateExtractionPath(destination, entry.Key, resolveLinks);
        if (!string.IsNullOrEmpty(entry.LinkTarget))
            throw new IOException(LocalizationText.Get("archive.links_unsupported"));
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string[] BuildExtractionPaths(string destination, List<ArchiveReadEntry> entries, Dictionary<string, string> roots)
    {
        var paths = entries.Select(entry =>
        {
            var slash = entry.Key.IndexOf('/');
            var root = slash >= 0 ? entry.Key[..slash] : entry.Key;
            var path = roots.TryGetValue(root, out var renamed)
                ? renamed + (slash >= 0 ? entry.Key[slash..] : "") : entry.Key;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(destination, path)));
        }).ToArray();
        var reserved = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < paths.Length; i++)
        {
            if (entries[i].IsDirectory) directories.Add(paths[i]);
            for (var parent = Path.GetDirectoryName(paths[i]); parent != null && parent != Path.GetFullPath(destination);
                 parent = Path.GetDirectoryName(parent))
                directories.Add(parent);
        }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < paths.Length; i++)
        {
            if (entries[i].IsDirectory) continue;
            if (!directories.Contains(paths[i]) && used.Add(paths[i])) continue;
            var parent = Path.GetDirectoryName(paths[i])!;
            var name = Path.GetFileNameWithoutExtension(paths[i]);
            var extension = Path.GetExtension(paths[i]);
            var counter = 2;
            string candidate;
            do { candidate = Path.Combine(parent, $"{name} {counter++}{extension}"); }
            while (reserved.Contains(candidate) || PathExists(candidate));
            paths[i] = candidate;
            reserved.Add(candidate);
            used.Add(candidate);
        }
        return paths;
    }

    private static void CheckCompressionSource(string path)
    {
        var file = new FileInfo(path);
        var stamp = (file.Length, file.LastWriteTimeUtc, FileIdentitySnapshot.Capture(path, false));
        if (FileTaskContext.Current is { } context && context.Remember("compress-source:" + path, () => stamp) != stamp)
            throw new IOException(LocalizationText.Get("源项目已变化，请重新创建任务。"));
    }

    private static string ValidateExtractionPath(string directory, string entry, bool resolveLinks = true)
    {
        var root = Path.GetFullPath(directory).TrimEnd('/') + "/";
        var target = Path.GetFullPath(Path.Combine(directory, entry));
        if (Path.IsPathRooted(entry) || entry.Length > 1 && entry[1] == ':'
            || !target.StartsWith(root, StringComparison.Ordinal))
            throw new IOException(LocalizationText.Get("压缩包包含越界路径。"));
        if (!resolveLinks) return target;
        var realRoot = OperatingSystem.IsMacOS() ? Platforms.MacOS.MacSandboxNative.RealPath(directory) : Path.GetFullPath(directory);
        var realTarget = OperatingSystem.IsMacOS() ? Platforms.MacOS.MacSandboxNative.RealPath(target) : target;
        if (!realTarget.StartsWith(realRoot.TrimEnd('/') + "/", StringComparison.Ordinal))
            throw new IOException(LocalizationText.Get("压缩包包含越界路径。"));
        DirectoryAccess.Current.EnsureAccess(target);
        return target;
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized == "." ? "" : normalized;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static ArchiveEncoding CreateUtf8ArchiveEncoding()
    {
        return new ArchiveEncoding
        {
            Default = Encoding.UTF8,
            Password = Encoding.UTF8,
            UTF8 = Encoding.UTF8,
        };
    }

    private static string GetUniqueFilePath(string path)
    {
        if (!PathExists(path) && new FileInfo(path).LinkTarget == null) return path;
        var directory = Path.GetDirectoryName(path)!;
        var name = ArchiveFormats.RemoveExtension(path);
        var extension = Path.GetFileName(path)[name.Length..];
        var counter = 2;
        string candidate;
        do { candidate = Path.Combine(directory, $"{name} {counter++}{extension}"); }
        while (PathExists(candidate) || new FileInfo(candidate).LinkTarget != null);
        return candidate;
    }

    private static string GetUniqueRelativePath(string name, HashSet<string> usedPaths)
    {
        if (usedPaths.Add(name)) return name;
        var baseName = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var counter = 2;
        string candidate;
        do { candidate = $"{baseName} {counter++}{extension}"; }
        while (!usedPaths.Add(candidate));
        return candidate;
    }
}
