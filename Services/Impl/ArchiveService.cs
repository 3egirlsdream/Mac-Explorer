using System.Text;
using MacExplorer.Indexing;
using MacExplorer.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;

namespace MacExplorer.Services.Impl;

public class ArchiveService : IArchiveService
{
    private readonly IBackgroundTaskManager? _tasks;
    public ArchiveService(IBackgroundTaskManager? tasks = null) { _tasks = tasks; }
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Container aliases are routed through SharpCompress. The parser still
        // validates the actual file contents before the preview exposes it.
        ".zip", ".zipx", ".cbz", ".jar", ".war", ".ear", ".apk", ".xpi", ".vsix",
        ".rar", ".7z", ".tar", ".gz", ".gzip", ".tgz", ".taz", ".bz2", ".bzip2",
        ".tbz", ".tbz2", ".tb2", ".xz", ".txz", ".zst", ".zstd", ".tzst"
    };

    public bool IsArchiveFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && ArchiveExtensions.Contains(ext);
    }

    public bool IsEncrypted(string archivePath)
    {
        DirectoryAccess.Current.EnsureAccess(archivePath);
        try
        {
            using var preparedArchive = PrepareArchive(archivePath);
            using var archive = OpenArchive(preparedArchive.Path, null);
            var firstFile = archive.Entries.FirstOrDefault(e => !e.IsDirectory);
            if (firstFile == null) return false;

            using var stream = firstFile.OpenEntryStream();
            _ = stream.ReadByte();
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
        return Task.Run(() =>
        {
            using var preparedArchive = PrepareArchive(archivePath);
            using var archive = OpenArchive(preparedArchive.Path, password);

            var normalizedPrefix = NormalizePath(internalPath);
            if (!string.IsNullOrEmpty(normalizedPrefix) && !normalizedPrefix.EndsWith('/'))
                normalizedPrefix += "/";

            var allKeys = new HashSet<string>();
            var entryMap = new Dictionary<string, IArchiveEntry>();

            foreach (var entry in archive.Entries)
            {
                if (entry.Key == null) continue;
                var key = NormalizePath(entry.Key);
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
                        size = archEntry.Size;
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
        await Task.Run(() =>
        {
            using var preparedArchive = PrepareArchive(archivePath);
            using var archive = OpenArchive(preparedArchive.Path, password);

            // Build rename map for root-level entries that conflict with existing items
            var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (entry.Key == null) continue;
                var normalized = NormalizePath(entry.Key);
                var firstSlash = normalized.IndexOf('/');
                var rootName = firstSlash >= 0 ? normalized[..firstSlash] : normalized;
                if (!string.IsNullOrEmpty(rootName))
                    rootNames.Add(rootName);
            }

            var renameMap = FileTaskContext.Current?.Remember("extract-map:" + archivePath + ":" + destinationPath,
                () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rootMapReady = FileTaskContext.Current?.IsDone("extract-map:" + archivePath + ":" + destinationPath) == true;
            foreach (var rootName in rootMapReady ? Enumerable.Empty<string>() : rootNames)
            {
                var targetPath = Path.Combine(destinationPath, rootName);
                if (File.Exists(targetPath) || Directory.Exists(targetPath))
                {
                    var counter = 2;
                    string newName;
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(rootName);
                    var ext = Path.GetExtension(rootName);
                    do
                    {
                        newName = string.IsNullOrEmpty(ext)
                            ? $"{rootName} {counter}"
                            : $"{nameWithoutExt} {counter}{ext}";
                        counter++;
                    } while (File.Exists(Path.Combine(destinationPath, newName))
                          || Directory.Exists(Path.Combine(destinationPath, newName)));
                    renameMap[rootName] = newName;
                }
            }

            FileTaskContext.Current?.MarkDone("extract-map:" + archivePath + ":" + destinationPath);
            var totalEntries = archive.Entries.Count(e => !e.IsDirectory);
            var processed = 0;
            if (FileTaskContext.Current is { } taskContext) taskContext.Task.TotalItems = archive.Entries.Count();

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                FileTaskContext.Checkpoint();
                if (entry.Key == null) continue;

                var entryPath = NormalizePath(entry.Key);
                ValidateExtractionPath(destinationPath, entryPath);

                // Apply rename map to root segment
                if (renameMap.Count > 0)
                {
                    var firstSlash = entryPath.IndexOf('/');
                    var rootSegment = firstSlash >= 0 ? entryPath[..firstSlash] : entryPath;
                    if (renameMap.TryGetValue(rootSegment, out var newRoot))
                    {
                        entryPath = firstSlash >= 0
                            ? newRoot + entryPath[firstSlash..]
                            : newRoot;
                    }
                }

                var fullDest = ValidateExtractionPath(destinationPath, entryPath);

                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(fullDest);
                    FileTaskContext.Current?.ReportItem(entryPath, fullDest, FileTaskItemState.Completed);
                    continue;
                }

                var parentDir = Path.GetDirectoryName(fullDest);
                if (parentDir != null)
                    Directory.CreateDirectory(parentDir);

                var context = FileTaskContext.Current;
                var key = "extract-entry:" + archivePath + ":" + fullDest;
                if (context?.IsDone(key) == true) { processed++; continue; }
                var temporary = fullDest + "." + (context?.Task.Id ?? Guid.NewGuid().ToString("N")) + ".fkfinder-tmp";
                var temporaryOwned = false;
                try
                {
                    using (var entryStream = entry.OpenEntryStream())
                    using (var fileStream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                    { temporaryOwned = true; entryStream.CopyTo(fileStream); }
                    File.Move(temporary, fullDest, false);
                    context?.MarkDone(key);
                    context?.ReportItem(entryPath, fullDest, FileTaskItemState.Completed);
                }
                finally { if (temporaryOwned) TryDeleteFile(temporary); }

                processed++;
                progress?.Report(new ArchiveProgress
                {
                    IsActive = true,
                    Percentage = totalEntries > 0 ? (double)processed / totalEntries * 100 : 0,
                    CurrentFile = entryPath,
                    OperationLabel = "正在解压..."
                });
            }
        }, ct);
    }

    public async Task<string> ExtractEntryToTempAsync(string archivePath, string entryKey, string? password = null)
    {
        DirectoryAccess.Current.EnsureAccess(archivePath);
        return await Task.Run(() =>
        {
            var tempBase = Path.Combine(Path.GetTempPath(), "MacExplorer-archive-temp");
            var sessionDir = Path.Combine(tempBase, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sessionDir);

            var normalizedKey = NormalizePath(entryKey);
            if (normalizedKey.EndsWith('/'))
                normalizedKey = normalizedKey[..^1];

            using var preparedArchive = PrepareArchive(archivePath);
            using var archive = OpenArchive(preparedArchive.Path, password);

            foreach (var entry in archive.Entries)
            {
                if (entry.Key == null || entry.IsDirectory) continue;
                var key = NormalizePath(entry.Key);
                if (key == normalizedKey || key == normalizedKey + "/")
                {
                    var fileName = Path.GetFileName(entry.Key);
                    var destFile = Path.Combine(sessionDir, fileName);
                    using var entryStream = entry.OpenEntryStream();
                    using var fileStream = File.Create(destFile);
                    entryStream.CopyTo(fileStream);
                    return destFile;
                }
            }

            throw new FileNotFoundException($"归档中未找到文件: {entryKey}");
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

    private static IArchive OpenArchive(string archivePath, string? password)
    {
        var options = new ReaderOptions
        {
            ArchiveEncoding = CreateUtf8ArchiveEncoding()
        };
        if (!string.IsNullOrEmpty(password))
            options.Password = password;

        return ArchiveFactory.OpenArchive(archivePath, options);
    }

    private static PreparedArchive PrepareArchive(string archivePath)
    {
        if (!IsTarGZip(archivePath))
            return new PreparedArchive(archivePath, null);

        // The TAR reader needs a seekable stream, while gzip streams are
        // sequential. Expand only the TAR payload into a private temporary
        // file and remove it immediately after the archive operation finishes.
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"MacExplorer-targz-{Guid.NewGuid():N}.tar");
        try
        {
            using var source = File.OpenRead(archivePath);
            using var gzip = new System.IO.Compression.GZipStream(
                source,
                System.IO.Compression.CompressionMode.Decompress);
            using var destination = File.Create(temporaryPath);
            gzip.CopyTo(destination);
            return new PreparedArchive(temporaryPath, temporaryPath);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static bool IsTarGZip(string path)
        => path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".tar.gzip", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".taz", StringComparison.OrdinalIgnoreCase);

    private sealed class PreparedArchive(string path, string? temporaryPath) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
                TryDeleteFile(temporaryPath);
        }
    }

    private static void CheckCompressionSource(string path)
    {
        var file = new FileInfo(path);
        var stamp = (file.Length, file.LastWriteTimeUtc, FileIdentitySnapshot.Capture(path, false));
        if (FileTaskContext.Current is { } context && context.Remember("compress-source:" + path, () => stamp) != stamp)
            throw new IOException(LocalizationText.Get("源项目已变化，请重新创建任务。"));
    }

    private static string ValidateExtractionPath(string directory, string entry)
    {
        var root = Path.GetFullPath(directory).TrimEnd('/') + "/";
        var target = Path.GetFullPath(Path.Combine(directory, entry));
        if (Path.IsPathRooted(entry) || !target.StartsWith(root, StringComparison.Ordinal))
            throw new IOException(LocalizationText.Get("压缩包包含越界路径。"));
        var realRoot = OperatingSystem.IsMacOS() ? Platforms.MacOS.MacSandboxNative.RealPath(directory) : Path.GetFullPath(directory);
        var realTarget = OperatingSystem.IsMacOS() ? Platforms.MacOS.MacSandboxNative.RealPath(target) : target;
        if (!realTarget.StartsWith(realRoot.TrimEnd('/') + "/", StringComparison.Ordinal))
            throw new IOException(LocalizationText.Get("压缩包包含越界路径。"));
        DirectoryAccess.Current.EnsureAccess(target);
        return target;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
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
