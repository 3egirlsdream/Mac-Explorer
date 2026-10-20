using System.Text;
using MacExplorer.Indexing;
using MacExplorer.Models;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;

namespace MacExplorer.Services.Impl;

public class ArchiveService : IArchiveService
{
    private readonly IBackgroundTaskManager? _tasks;
    public ArchiveService(IBackgroundTaskManager? tasks = null) { _tasks = tasks; }
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

            var normalizedPrefix = NormalizePath(internalPath);
            if (!string.IsNullOrEmpty(normalizedPrefix) && !normalizedPrefix.EndsWith('/'))
                normalizedPrefix += "/";

            var allKeys = new HashSet<string>();
            var entryMap = new Dictionary<string, ArchiveReadEntry>();

            while (archive.MoveNext())
            {
                var entry = archive.Entry;
                var key = NormalizePath(entry.Key);
                if (key.Length == 0) continue;
                if (entry.IsDirectory) key = key.TrimEnd('/') + "/";
                allKeys.Add(key);
                entryMap[key] = entry;
            }

            // Collect unique immediate children at the given level
            var children = new Dictionary<string, (bool isDir, long size, DateTime modified)>(StringComparer.Ordinal);

            foreach (var key in allKeys)
            {
                if (!IsImmediateChild(key, normalizedPrefix))
                    continue;

                var childName = GetChildName(key, normalizedPrefix);
                if (string.IsNullOrEmpty(childName)) continue;

                var isDir = key.EndsWith('/') || HasChildren(key, allKeys);

                if (!children.ContainsKey(childName))
                {
                    long size = 0;
                    var modified = DateTime.Now;
                    if (entryMap.TryGetValue(key, out var archEntry))
                    {
                        size = archEntry.Size ?? 0;
                        modified = archEntry.LastModifiedTime ?? DateTime.Now;
                    }
                    children[childName] = (isDir, size, modified);
                }
            }

            // Also detect implicit directories from deeper paths
            foreach (var key in allKeys)
            {
                if (!key.StartsWith(normalizedPrefix, StringComparison.Ordinal) || key == normalizedPrefix)
                    continue;
                var relative = key[normalizedPrefix.Length..];
                var slashIdx = relative.IndexOf('/');
                if (slashIdx < 0) continue;
                var dirName = relative[..slashIdx];
                if (!string.IsNullOrEmpty(dirName) && !children.ContainsKey(dirName))
                {
                    children[dirName] = (true, 0, DateTime.Now);
                }
            }

            var entries = new List<FileSystemEntry>();
            foreach (var (name, (isDir, size, modified)) in children)
            {
                var childInternalPath = string.IsNullOrEmpty(normalizedPrefix)
                    ? name
                    : normalizedPrefix + name;
                if (isDir && !childInternalPath.EndsWith('/'))
                    childInternalPath += "/";

                var ext = isDir ? "" : Path.GetExtension(name);
                entries.Add(new FileSystemEntry
                {
                    FullPath = ArchivePathHelper.Build(archivePath, childInternalPath),
                    Name = name,
                    IsDirectory = isDir,
                    Size = isDir ? 0 : size,
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

    public Task ExtractAsync(string archivePath, string destinationPath, IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default, string? password = null)
        => FileTaskContext.RunAsync(_tasks, new(BackgroundTaskKind.Extract, LocalizationText.Get("正在解压..."),
            [archivePath], destinationPath), async context =>
            {
                await ExtractCoreAsync(archivePath, destinationPath, progress, context.CancellationToken, password);
                return true;
            }, ct);

    private async Task ExtractCoreAsync(
        string archivePath, string destinationPath,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default,
        string? password = null)
    {
        DirectoryAccess.Current.EnsureAccess(archivePath);
        DirectoryAccess.Current.EnsureAccess(destinationPath);
        await ReadArchiveAsync(() =>
        {
            var context = FileTaskContext.Current;
            if (context != null) context.Task.IsIndeterminate = true;
            progress?.Report(new ArchiveProgress { IsActive = true, IsIndeterminate = true,
                CurrentFile = Path.GetFileName(archivePath), OperationLabel = LocalizationText.Get("正在解压...") });
            CheckCompressionSource(archivePath);
            var metadata = new List<ArchiveReadEntry>();
            using (var scan = OpenArchive(archivePath, password, ct))
            {
                while (scan.MoveNext())
                {
                    ct.ThrowIfCancellationRequested();
                    FileTaskContext.Checkpoint();
                    var entry = scan.Entry with { Key = NormalizePath(scan.Entry.Key) };
                    if (entry.Key.Length == 0) continue;
                    ValidateEntry(destinationPath, entry, resolveLinks: false);
                    metadata.Add(entry);
                }
            }
            CheckCompressionSource(archivePath);

            var rootNames = metadata.Select(e => e.Key.Split('/')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mapKey = "extract-map:" + archivePath + ":" + destinationPath;
            var renameMap = context?.Remember(mapKey, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (context?.IsDone(mapKey) != true)
            {
                renameMap.Clear();
                var reservedNames = new HashSet<string>(rootNames, StringComparer.OrdinalIgnoreCase);
                foreach (var rootName in rootNames)
                {
                    if (!PathExists(Path.Combine(destinationPath, rootName))) continue;
                    var name = Path.GetFileNameWithoutExtension(rootName);
                    var extension = Path.GetExtension(rootName);
                    var counter = 2;
                    string renamed;
                    do { renamed = $"{name} {counter++}{extension}"; }
                    while (reservedNames.Contains(renamed) || PathExists(Path.Combine(destinationPath, renamed)));
                    renameMap[rootName] = renamed;
                    reservedNames.Add(renamed);
                }
                context?.MarkDone(mapKey);
            }

            var totalEntries = metadata.Count(e => !e.IsDirectory);
            var processed = 0;
            var outputPaths = context?.Remember(mapKey + ":paths", () => BuildExtractionPaths(destinationPath, metadata, renameMap))
                ?? BuildExtractionPaths(destinationPath, metadata, renameMap);
            var entryIndex = 0;
            if (context != null) context.Task.TotalItems = metadata.Count;
            using var archive = OpenArchive(archivePath, password, ct);
            while (archive.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                FileTaskContext.Checkpoint();
                var entry = archive.Entry with { Key = NormalizePath(archive.Entry.Key) };
                if (entry.Key.Length == 0) continue;
                ValidateEntry(destinationPath, entry, resolveLinks: false);
                if (entryIndex >= metadata.Count || entry.Key != metadata[entryIndex].Key
                    || entry.IsDirectory != metadata[entryIndex].IsDirectory || entry.Size != metadata[entryIndex].Size)
                    throw new IOException(LocalizationText.Get("源项目已变化，请重新创建任务。"));
                var entryPath = Path.GetRelativePath(destinationPath, outputPaths[entryIndex++]);
                var fullDest = ValidateExtractionPath(destinationPath, entryPath);
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(fullDest);
                    context?.ReportItem(entry.Key, fullDest, FileTaskItemState.Completed);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);
                var key = "extract-entry:" + archivePath + ":" + fullDest;
                if (context?.IsDone(key) == true) { processed++; continue; }
                var temporary = fullDest + "." + (context?.Task.Id ?? Guid.NewGuid().ToString("N")) + ".fkfinder-tmp";
                var temporaryOwned = false;
                void Report(long copied, bool complete)
                {
                    var indeterminate = entry.Size == null && !complete;
                    var fraction = complete ? 1 : entry.Size > 0 ? Math.Min((double)copied / entry.Size.Value, 0.999) : 0;
                    var percentage = totalEntries > 0 ? (processed + fraction) / totalEntries * 100 : 0;
                    if (context != null) context.Task.IsIndeterminate = indeterminate;
                    progress?.Report(new ArchiveProgress { IsActive = true, Percentage = percentage,
                        IsIndeterminate = indeterminate, CurrentFile = entryPath,
                        OperationLabel = LocalizationText.Get("正在解压...") });
                }
                try
                {
                    Report(0, false);
                    using (var entryStream = archive.OpenEntryStream())
                    using (var fileStream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                    {
                        temporaryOwned = true;
                        try
                        {
                            var copied = CopyEntry(entryStream, fileStream, ct, copied => Report(copied, false));
                            if (entry.Size is { } expected && copied != expected)
                                throw new InvalidDataException("Archive entry size mismatch.");
                        }
                        catch { archive.StopReading(); throw; }
                    }
                    ct.ThrowIfCancellationRequested();
                    FileTaskContext.Checkpoint();
                    CheckCompressionSource(archivePath);
                    File.Move(temporary, fullDest, false);
                    context?.MarkDone(key);
                    context?.ReportItem(entry.Key, fullDest, FileTaskItemState.Completed);
                    Report(0, true);
                }
                finally { if (temporaryOwned) TryDeleteFile(temporary); }
                processed++;
            }
            if (entryIndex != metadata.Count)
                throw new IOException(LocalizationText.Get("源项目已变化，请重新创建任务。"));
            if (context != null) context.Task.IsIndeterminate = false;
            progress?.Report(new ArchiveProgress { IsActive = true, Percentage = 100,
                OperationLabel = LocalizationText.Get("正在解压...") });
            return true;
        }, ct);
    }

    public Task<string> ExtractEntryToTempAsync(string archivePath, string entryKey, string? password = null)
    {
        DirectoryAccess.Current.EnsureAccess(archivePath);
        return ReadArchiveAsync(() =>
        {
            var sessionDir = Path.Combine(Path.GetTempPath(), "MacExplorer-archive-temp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionDir);
            try
            {
                var normalizedKey = NormalizePath(entryKey).TrimEnd('/');
                using var archive = OpenArchive(archivePath, password);
                while (archive.MoveNext())
                {
                    var entry = archive.Entry with { Key = NormalizePath(archive.Entry.Key) };
                    if (entry.IsDirectory || entry.Key.TrimEnd('/') != normalizedKey) continue;
                    ValidateEntry(sessionDir, entry);
                    var destFile = ValidateExtractionPath(sessionDir, Path.GetFileName(entry.Key));
                    using (var entryStream = archive.OpenEntryStream())
                    using (var fileStream = new FileStream(destFile, FileMode.CreateNew, FileAccess.Write))
                    {
                        try
                        {
                            var copied = CopyEntry(entryStream, fileStream, default);
                            if (entry.Size is { } expected && copied != expected)
                                throw new InvalidDataException("Archive entry size mismatch.");
                        }
                        catch { archive.StopReading(); throw; }
                    }
                    return destFile;
                }
                throw new FileNotFoundException(LocalizationText.Get("archive.entry_missing", entryKey));
            }
            catch { Directory.Delete(sessionDir, recursive: true); throw; }
        });
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
        foreach (var source in options.SourcePaths) DirectoryAccess.Current.EnsureAccess(source);
        return await Task.Run(() =>
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

            // SharpZipLib writes WinZip AES; SharpCompress retains existing archive reading.
            if (options.Format == ArchiveFormat.Zip && !string.IsNullOrEmpty(options.Password))
            {
                WriteEncryptedZip(outputPath, tempPath, filesToCompress, options.Password, progress, ct);
                return outputPath;
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
                return outputPath;
            }
            catch
            {
                if (temporaryOwned) TryDeleteFile(tempPath);
                throw;
            }
        }, ct);
    }

    private static void WriteEncryptedZip(
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
            File.Move(tempPath, outputPath, overwrite: false);
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
            catch (MultiVolumeExtractionException ex)
            { throw new NotSupportedException(LocalizationText.Get("archive.volumes_unsupported"), ex); }
            catch (NotSupportedException ex)
            { throw new NotSupportedException(LocalizationText.Get("archive.unsupported_method"), ex); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException
                || ex is SharpCompressException and not SharpCompress.Common.CryptographicException and not ReaderCancelledException
                    && !ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            { throw new InvalidDataException(LocalizationText.Get("archive.invalid_data"), ex); }
        }, ct);

    private static bool IsUnsupportedEncryption(Exception ex)
        => ex.Message.Contains("currently not supported", StringComparison.OrdinalIgnoreCase);

    private static long CopyEntry(Stream source, Stream destination, CancellationToken ct, Action<long>? report = null)
    {
        var buffer = new byte[64 * 1024];
        long copied = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            FileTaskContext.Checkpoint();
            var read = source.Read(buffer);
            if (read == 0) break;
            destination.Write(buffer, 0, read);
            copied += read;
            report?.Invoke(copied);
        }
        return copied;
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

    private static bool IsImmediateChild(string key, string prefix)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var relative = key[prefix.Length..];
        if (string.IsNullOrEmpty(relative)) return false;
        var trimmed = relative.TrimEnd('/');
        return !trimmed.Contains('/');
    }

    private static string GetChildName(string key, string prefix)
    {
        var relative = key[prefix.Length..];
        return relative.TrimEnd('/');
    }

    private static bool HasChildren(string key, HashSet<string> allKeys)
    {
        var prefix = key.EndsWith('/') ? key : key + "/";
        return allKeys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal) && k != prefix);
    }

    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            ext = ".tar.gz";
            name = Path.GetFileNameWithoutExtension(path[..^3]);
            name = Path.GetFileNameWithoutExtension(name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
                ? name : name);
        }
        else if (path.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase))
        {
            ext = ".tar.bz2";
            name = Path.GetFileNameWithoutExtension(path[..^4]);
            name = Path.GetFileNameWithoutExtension(name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
                ? name : name);
        }

        int counter = 2;
        string newPath;
        do
        {
            newPath = Path.Combine(dir, $"{name} {counter}{ext}");
            counter++;
        } while (File.Exists(newPath));

        return newPath;
    }

    private static string GetUniqueRelativePath(string name, HashSet<string> usedPaths)
    {
        if (usedPaths.Add(name))
            return name;

        var baseName = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var counter = 2;
        string candidate;
        do
        {
            candidate = $"{baseName} {counter}{ext}";
            counter++;
        } while (!usedPaths.Add(candidate));

        return candidate;
    }
}
