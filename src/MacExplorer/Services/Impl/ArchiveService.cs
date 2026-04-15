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
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".tgz", ".zst"
    };

    public bool IsArchiveFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && ArchiveExtensions.Contains(ext);
    }

    public Task<IReadOnlyList<FileSystemEntry>> GetArchiveContentsAsync(
        string archivePath, string internalPath = "")
    {
        return Task.Run(() =>
        {
            var normalizedPrefix = NormalizePath(internalPath);
            if (!string.IsNullOrEmpty(normalizedPrefix) && !normalizedPrefix.EndsWith('/'))
                normalizedPrefix += "/";

            using var archive = ArchiveFactory.Open(archivePath, new ReaderOptions
            {
                ArchiveEncoding = new ArchiveEncoding(Encoding.UTF8, Encoding.UTF8)
            });

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

    public async Task ExtractAsync(
        string archivePath, string destinationPath,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            using var archive = ArchiveFactory.Open(archivePath, new ReaderOptions
            {
                ArchiveEncoding = new ArchiveEncoding(Encoding.UTF8, Encoding.UTF8)
            });

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

            var renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rootName in rootNames)
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

            var totalEntries = archive.Entries.Count(e => !e.IsDirectory);
            var processed = 0;

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Key == null) continue;

                var entryPath = NormalizePath(entry.Key);

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

                var fullDest = Path.Combine(destinationPath, entryPath);

                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(fullDest);
                    continue;
                }

                var parentDir = Path.GetDirectoryName(fullDest);
                if (parentDir != null)
                    Directory.CreateDirectory(parentDir);

                using var entryStream = entry.OpenEntryStream();
                using var fileStream = File.Create(fullDest);
                entryStream.CopyTo(fileStream);

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

    public async Task<string> ExtractEntryToTempAsync(string archivePath, string entryKey)
    {
        return await Task.Run(() =>
        {
            var tempBase = Path.Combine(Path.GetTempPath(), "MacExplorer-archive-temp");
            var sessionDir = Path.Combine(tempBase, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sessionDir);

            var normalizedKey = NormalizePath(entryKey);
            // Remove trailing slash for file matching
            if (normalizedKey.EndsWith('/'))
                normalizedKey = normalizedKey[..^1];

            using var archive = ArchiveFactory.Open(archivePath, new ReaderOptions
            {
                ArchiveEncoding = new ArchiveEncoding(Encoding.UTF8, Encoding.UTF8)
            });

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

    public async Task CompressAsync(
        CompressOptions options,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var ext = options.Format switch
            {
                ArchiveFormat.Zip => ".zip",
                ArchiveFormat.TarGz => ".tar.gz",
                ArchiveFormat.TarBz2 => ".tar.bz2",
                _ => ".zip"
            };

            var outputPath = Path.Combine(options.OutputDirectory, options.ArchiveName + ext);
            outputPath = GetUniqueFilePath(outputPath);

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

            var writerOptions = new WriterOptions(compressionType)
            {
                ArchiveEncoding = new ArchiveEncoding(Encoding.UTF8, Encoding.UTF8)
            };

            var stream = File.Create(outputPath + ".fkfinder-tmp");
            var writer = WriterFactory.Open(stream, archiveType, writerOptions);

            var total = filesToCompress.Count;
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (fullPath, relativePath) = filesToCompress[i];
                writer.Write(relativePath, fullPath);

                progress?.Report(new ArchiveProgress
                {
                    IsActive = true,
                    Percentage = total > 0 ? (double)(i + 1) / total * 100 : 0,
                    CurrentFile = relativePath,
                    OperationLabel = "正在压缩..."
                });
            }

            // Flush and close archive before rename
            writer.Dispose();
            stream.Dispose();
            File.Move(outputPath + ".fkfinder-tmp", outputPath);
        }, ct);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static bool IsImmediateChild(string key, string prefix)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var relative = key[prefix.Length..];
        if (string.IsNullOrEmpty(relative)) return false;
        // Remove trailing slash for directory entries
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

        // Handle double extensions like .tar.gz
        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            ext = ".tar.gz";
            name = Path.GetFileNameWithoutExtension(path[..^3]); // remove .gz first, then get name without .tar
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
