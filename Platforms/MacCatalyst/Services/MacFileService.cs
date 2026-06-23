using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.Indexing;
using MacExplorer.Services;
// Foundation removed for Avalonia
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacFileService : IFileService
{
    private readonly SqliteFileIndex? _iconCache;
    private readonly string _iconCacheDir;
    public MacFileService(SqliteFileIndex? iconCache = null)
    {
        _iconCache = iconCache;
        _iconCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MacExplorer", "icon-cache");
        if (!Directory.Exists(_iconCacheDir))
            Directory.CreateDirectory(_iconCacheDir);
    }

    public string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string RootDirectory => "/";
    // Use a sentinel path for trash - actual enumeration uses Finder AppleScript
    public string TrashDirectory => VirtualPath.SystemTrash;

    public async Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string path, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var entries = new List<FileSystemEntry>();

            try
            {
                // Trash: use Finder AppleScript (macOS SIP protects ~/.Trash)
                if (path == TrashDirectory)
                {
                    return EnumerateTrashViaFinder(cancellationToken);
                }

                if (!Directory.Exists(path))
                    return entries;

                // Single enumeration: GetFileSystemEntries avoids the directory/file seek gap
                var allPaths = Directory.GetFileSystemEntries(path);
                foreach (var entryPath in allPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var attrs = File.GetAttributes(entryPath);
                        var isDir = attrs.HasFlag(FileAttributes.Directory);
                        if (isDir)
                            entries.Add(CreateEntryFromDirectoryPath(entryPath, attrs));
                        else
                            entries.Add(CreateEntryFromFilePath(entryPath, attrs));
                    }
                    catch (UnauthorizedAccessException)
                    {
                        var name = Path.GetFileName(entryPath);
                        entries.Add(CreateInaccessibleEntry(entryPath, name, isDirectory: false));
                    }
                }

                // Merge /System/Applications counterpart when browsing under /Applications
                if (path == "/Applications" || path.StartsWith("/Applications/"))
                {
                    var systemPath = "/System" + path;
                    if (Directory.Exists(systemPath))
                    {
                        try
                        {
                            foreach (var entryPath in Directory.GetFileSystemEntries(systemPath))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                try
                                {
                                    var attrs = File.GetAttributes(entryPath);
                                    var name = Path.GetFileName(entryPath);
                                    if (name.StartsWith('.') || entries.Any(e => e.Name == name)) continue;
                                    var isDir = attrs.HasFlag(FileAttributes.Directory);
                                    entries.Add(isDir
                                        ? CreateEntryFromDirectoryPath(entryPath, attrs)
                                        : CreateEntryFromFilePath(entryPath, attrs));
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Return whatever entries we collected
            }

            return entries;
        }, cancellationToken);
    }

    public async IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryBatchesAsync(
        string path,
        int batchSize = 256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        batchSize = Math.Clamp(batchSize, 32, 1024);
        if (path == TrashDirectory)
        {
            var trashEntries = await GetDirectoryContentsAsync(path, cancellationToken);
            if (trashEntries.Count > 0) yield return trashEntries;
            yield break;
        }

        if (!Directory.Exists(path)) yield break;
        var batch = new List<FileSystemEntry>(batchSize);
        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemEntry? entry = null;
            try
            {
                var attrs = File.GetAttributes(entryPath);
                entry = attrs.HasFlag(FileAttributes.Directory)
                    ? CreateEntryFromDirectoryPath(entryPath, attrs)
                    : CreateEntryFromFilePath(entryPath, attrs);
            }
            catch (UnauthorizedAccessException)
            {
                entry = CreateInaccessibleEntry(entryPath, Path.GetFileName(entryPath), isDirectory: false);
            }
            catch { }

            if (entry != null)
            {
                batch.Add(entry);
                knownNames.Add(entry.Name);
            }
            if (batch.Count < batchSize) continue;
            yield return batch.ToArray();
            batch.Clear();
            await Task.Yield();
        }

        if (batch.Count > 0)
        {
            yield return batch.ToArray();
            batch.Clear();
        }

        if (path == "/Applications" || path.StartsWith("/Applications/", StringComparison.Ordinal))
        {
            var systemPath = "/System" + path;
            if (Directory.Exists(systemPath))
            {
                foreach (var entryPath in Directory.EnumerateFileSystemEntries(systemPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileSystemEntry? systemEntry = null;
                    try
                    {
                        var name = Path.GetFileName(entryPath);
                        if (name.StartsWith('.') || !knownNames.Add(name)) continue;
                        var attrs = File.GetAttributes(entryPath);
                        systemEntry = attrs.HasFlag(FileAttributes.Directory)
                            ? CreateEntryFromDirectoryPath(entryPath, attrs)
                            : CreateEntryFromFilePath(entryPath, attrs);
                    }
                    catch { }
                    if (systemEntry == null) continue;
                    batch.Add(systemEntry);
                    if (batch.Count < batchSize) continue;
                    yield return batch.ToArray();
                    batch.Clear();
                    await Task.Yield();
                }
            }
        }

        if (batch.Count > 0) yield return batch.ToArray();
    }

    private List<FileSystemEntry> EnumerateTrashViaFinder(CancellationToken ct)
    {
        var entries = new List<FileSystemEntry>();
        var scriptPath = Path.Combine(Path.GetTempPath(), $"fkfinder_trash_{Guid.NewGuid():N}.scpt");
        try
        {
            // Write AppleScript to temp file to avoid shell escaping issues
            File.WriteAllText(scriptPath, @"tell application ""Finder""
set output to """"
repeat with anItem in (get every item of trash)
try
set itemPath to POSIX path of (anItem as alias)
set output to output & itemPath & linefeed
end try
end repeat
return output
end tell");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add(scriptPath);
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!process.WaitForExit(100))
            {
                if (ct.IsCancellationRequested || DateTime.UtcNow >= deadline)
                {
                    TryKillProcess(process);
                    ct.ThrowIfCancellationRequested();
                    return entries;
                }
            }

            _ = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                return entries;

            var output = outputTask.GetAwaiter().GetResult();

            var paths = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var itemPath in paths)
            {
                ct.ThrowIfCancellationRequested();
                // Remove trailing slash if present
                var cleanPath = itemPath.TrimEnd('/');
                if (string.IsNullOrEmpty(cleanPath)) continue;
                try
                {
                    if (Directory.Exists(cleanPath))
                    {
                        entries.Add(CreateEntryFromDirectoryInfo(new DirectoryInfo(cleanPath)));
                    }
                    else if (File.Exists(cleanPath))
                    {
                        entries.Add(CreateEntryFromFileInfo(new FileInfo(cleanPath)));
                    }
                    else
                    {
                        // File exists in trash but we can't stat it - create basic entry
                        var name = Path.GetFileName(cleanPath);
                        entries.Add(CreateInaccessibleEntry(cleanPath, name, isDirectory: false));
                    }
                }
                catch
                {
                    var name = Path.GetFileName(cleanPath);
                    entries.Add(CreateInaccessibleEntry(cleanPath, name, isDirectory: false));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        finally
        {
            TryDeleteFile(scriptPath);
        }
        return entries;
    }

    public Task<FileSystemEntry?> GetEntryAsync(string path)
    {
        return Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var dirInfo = new DirectoryInfo(path);
                    return CreateEntryFromDirectoryInfo(dirInfo);
                }
                else if (File.Exists(path))
                {
                    var fileInfo = new FileInfo(path);
                    return CreateEntryFromFileInfo(fileInfo);
                }
            }
            catch
            {
                // Ignore errors
            }

            return null;
        });
    }

    public Task<bool> ExistsAsync(string path)
    {
        return Task.FromResult(Directory.Exists(path) || File.Exists(path));
    }

    public async Task<string> CreateFolderAsync(string parentPath, string name)
    {
        return await Task.Run(() =>
        {
            var fullPath = Path.Combine(parentPath, name);
            Directory.CreateDirectory(fullPath);
            return fullPath;
        });
    }

    public async Task<string> CreateFileAsync(string parentPath, string name)
    {
        return await Task.Run(() =>
        {
            var fullPath = Path.Combine(parentPath, name);
            File.Create(fullPath).Dispose();
            return fullPath;
        });
    }

    public async Task<string> CreateFileWithContentAsync(string parentPath, string name, byte[] content)
    {
        return await Task.Run(() =>
        {
            var fullPath = Path.Combine(parentPath, name);
            File.WriteAllBytes(fullPath, content);
            return fullPath;
        });
    }

    public async Task DeleteAsync(string path, bool moveToTrash = true)
    {
        if (moveToTrash)
        {
            if (await MoveToTrashWithWorkspaceAsync(path))
                return;

            await MoveToTrashWithFinderAsync(path);
            return;
        }

        await Task.Run(() =>
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        });
    }

    private static async Task<bool> MoveToTrashWithWorkspaceAsync(string path)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        var script = $$"""
ObjC.import('Foundation');

const path = {{JsonSerializer.Serialize(path)}};
const url = $.NSURL.fileURLWithPath(path);
const ok = $.NSFileManager.defaultManager.trashItemAtURLResultingItemURLError(url, null, null);
if (!ok) {
  throw new Error('NSFileManager trashItemAtURL failed');
}
""";

        return await RunJavaScriptForAutomationAsync(script)
            && !File.Exists(path)
            && !Directory.Exists(path);
    }

    private static Task MoveToTrashWithFinderAsync(string path)
    {
        return RunFinderScriptAsync(
            "on run argv\n" +
            "set itemToDelete to POSIX file (item 1 of argv) as alias\n" +
            "tell application \"Finder\" to delete itemToDelete\n" +
            "end run",
            path);
    }

    public async Task DeletePermanentlyAsync(string path)
    {
        await Task.Run(() =>
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        });
    }

    public async Task EmptyTrashAsync()
    {
        await RunFinderScriptAsync("tell application \"Finder\" to empty trash");
    }

    private static async Task RunFinderScriptAsync(string script, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new IOException(string.IsNullOrWhiteSpace(error) ? "Finder 操作失败" : error.Trim());
    }

    private static async Task<bool> RunJavaScriptForAutomationAsync(string script)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };
        process.StartInfo.ArgumentList.Add("-l");
        process.StartInfo.ArgumentList.Add("JavaScript");
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add(script);

        process.Start();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            Debug.WriteLine($"NSWorkspace recycle failed: {error.Trim()}");
            return false;
        }

        return true;
    }

    public async Task RenameAsync(string path, string newName)
    {
        await Task.Run(() =>
        {
            var parentPath = Path.GetDirectoryName(path) ?? "/";
            var newPath = Path.Combine(parentPath, newName);

            if (Directory.Exists(path))
                Directory.Move(path, newPath);
            else if (File.Exists(path))
                File.Move(path, newPath);
        });
    }

    public async Task MoveAsync(string sourcePath, string destinationDirectory, bool overwrite = false)
    {
        await Task.Run(() =>
        {
            var name = Path.GetFileName(sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, name);
            if (PathsEqual(sourcePath, destinationPath))
                return;

            if (Directory.Exists(sourcePath))
            {
                if (Directory.Exists(destinationPath))
                {
                    if (!overwrite)
                        return;
                    MergeDirectory(sourcePath, destinationPath);
                    return;
                }

                if (File.Exists(destinationPath))
                    return;

                Directory.Move(sourcePath, destinationPath);
            }
            else if (File.Exists(sourcePath))
            {
                if (Directory.Exists(destinationPath))
                    return;
                if (File.Exists(destinationPath) && !overwrite)
                    return;

                File.Move(sourcePath, destinationPath, overwrite);
            }
        });
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(first, second, StringComparison.Ordinal);
        }
    }

    private static void MergeDirectory(string sourceDir, string destDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            var destFile = Path.Combine(destDir, fileName);
            if (File.Exists(destFile))
                File.Delete(destFile);
            File.Move(file, destFile);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            var destSubDir = Path.Combine(destDir, dirName);
            if (Directory.Exists(destSubDir))
                MergeDirectory(dir, destSubDir);
            else
                Directory.Move(dir, destSubDir);
        }

        Directory.Delete(sourceDir);
    }

    public async Task CopyAsync(string sourcePath, string destinationDirectory)
    {
        await Task.Run(() =>
        {
            var name = Path.GetFileName(sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, GetUniqueName(destinationDirectory, name));

            if (Directory.Exists(sourcePath))
                CopyDirectoryRecursive(sourcePath, destinationPath);
            else if (File.Exists(sourcePath))
                File.Copy(sourcePath, destinationPath);
        });
    }

    public string GetParentPath(string path)
    {
        if (path == "/")
            return "/";

        var parent = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(parent) ? "/" : parent;
    }

    public string CombinePath(string directory, string name)
    {
        return Path.Combine(directory, name);
    }

    public IReadOnlyList<string> GetVolumes()
    {
        var volumes = new List<string> { "/" };
        try
        {
            var volumesDir = new DirectoryInfo("/Volumes");
            if (volumesDir.Exists)
            {
                foreach (var dir in volumesDir.EnumerateDirectories())
                {
                    try
                    {
                        volumes.Add(dir.FullName);
                    }
                    catch { }
                }
            }
        }
        catch { }

        return volumes;
    }

    private FileSystemEntry CreateEntryFromDirectoryPath(string fullPath, FileAttributes attrs)
    {
        var name = Path.GetFileName(fullPath);
        var ext = Path.GetExtension(name);
        var bundleIconKey = Indexing.SqliteFileIndex.ResolveBundleIconKey(ext);

        if (bundleIconKey == "folder" && IsKnownLibraryBundle(name))
            bundleIconKey = "app-bundle";

        return new FileSystemEntry
        {
            FullPath = fullPath,
            Name = name,
            IsDirectory = true,
            Size = 0,
            LastModified = File.GetLastWriteTime(fullPath),
            Created = File.GetCreationTime(fullPath),
            Extension = ext,
            IsHidden = name.StartsWith('.'),
            IsSymbolicLink = attrs.HasFlag(FileAttributes.ReparsePoint),
            IsReadable = true,
            IsWritable = !attrs.HasFlag(FileAttributes.ReadOnly),
            IconKey = bundleIconKey,
        };
    }

    private FileSystemEntry CreateEntryFromFilePath(string fullPath, FileAttributes attrs)
    {
        var name = Path.GetFileName(fullPath);
        var ext = Path.GetExtension(name).ToLowerInvariant();

        return new FileSystemEntry
        {
            FullPath = fullPath,
            Name = name,
            IsDirectory = false,
            Size = new FileInfo(fullPath).Length,
            LastModified = File.GetLastWriteTime(fullPath),
            Created = File.GetCreationTime(fullPath),
            Extension = ext,
            IsHidden = name.StartsWith('.'),
            IsSymbolicLink = attrs.HasFlag(FileAttributes.ReparsePoint),
            IsReadable = true,
            IsWritable = !attrs.HasFlag(FileAttributes.ReadOnly),
            IconKey = GetIconKeyForExtension(ext)
        };
    }

    private FileSystemEntry CreateEntryFromDirectoryInfo(DirectoryInfo dir)
    {
        var ext = Path.GetExtension(dir.Name);
        var bundleIconKey = Indexing.SqliteFileIndex.ResolveBundleIconKey(ext);

        // Handle known macOS library bundles that have no file extension
        if (bundleIconKey == "folder" && IsKnownLibraryBundle(dir.Name))
            bundleIconKey = "app-bundle";

        return new FileSystemEntry
        {
            FullPath = dir.FullName,
            Name = dir.Name,
            IsDirectory = true,
            Size = 0,
            LastModified = dir.LastWriteTime,
            Created = dir.CreationTime,
            Extension = ext,
            IsHidden = dir.Name.StartsWith('.'),
            IsSymbolicLink = dir.Attributes.HasFlag(FileAttributes.ReparsePoint),
            IsReadable = true,
            IsWritable = !dir.Attributes.HasFlag(FileAttributes.ReadOnly),
            IconKey = bundleIconKey,
            // Icons loaded lazily via ResolveAppIconsAsync
        };
    }

    private static bool IsKnownLibraryBundle(string dirName)
    {
        // Photo Booth library has no extension on macOS
        // Chinese: "Photo Booth图库", English: "Photo Booth Library"
        return dirName.StartsWith("Photo Booth", StringComparison.OrdinalIgnoreCase);
    }

    public async Task ResolveAppIconsAsync(
        IEnumerable<FileSystemEntry> entries,
        Action? onBatchResolved = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var appEntries = entries.Where(e => e.IconKey == "app-bundle" && e.IconUrl == null).ToList();
        if (appEntries.Count == 0) return;

        // Build mapping: appPath → cachedPngPath (skip already cached)
        var toExtract = new List<(FileSystemEntry entry, string appPath, string pngPath)>();
        foreach (var entry in appEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(entry.FullPath))).Substring(0, 16).ToLowerInvariant();
            var cachedPngPath = Path.Combine(_iconCacheDir, $"{hash}.png");

            if (File.Exists(cachedPngPath) && new FileInfo(cachedPngPath).Length > 0)
            {
                entry.IconUrl = cachedPngPath;
            }
            else
            {
                toExtract.Add((entry, entry.FullPath, cachedPngPath));
            }
        }

        // Notify for already-cached icons
        if (appEntries.Any(e => e.IconUrl != null))
            onBatchResolved?.Invoke();

        if (toExtract.Count == 0) return;

        // Process in batches via a single JXA script per batch
        const int batchSize = 20;
        for (int i = 0; i < toExtract.Count; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = toExtract.Skip(i).Take(batchSize).ToList();
            await Task.Run(() => ExtractIconsBatchJXA(batch, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var (entry, _, pngPath) in batch)
            {
                if (File.Exists(pngPath) && new FileInfo(pngPath).Length > 0)
                    entry.IconUrl = pngPath;
            }
            onBatchResolved?.Invoke();
        }
    }

    private void ExtractIconsBatchJXA(
        List<(FileSystemEntry entry, string appPath, string pngPath)> batch,
        CancellationToken cancellationToken)
    {
        string? scriptPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Build a single JXA script that processes all apps in this batch
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ObjC.import('AppKit');");
            sb.AppendLine("ObjC.import('Foundation');");
            sb.AppendLine("var ws = $.NSWorkspace.sharedWorkspace;");
            sb.AppendLine("function extractIcon(appPath, outPath) {");
            sb.AppendLine("  try {");
            sb.AppendLine("    var icon = ws.iconForFile(appPath);");
            sb.AppendLine("    var rep = $.NSBitmapImageRep.alloc.initWithBitmapDataPlanesPixelsWidePixelsHighBitsPerSampleSamplesPerPixelHasAlphaIsPlanarColorSpaceNameBytesPerRowBitsPerPixel(null, 128, 128, 8, 4, true, false, $.NSDeviceRGBColorSpace, 0, 0);");
            sb.AppendLine("    var ctx = $.NSGraphicsContext.graphicsContextWithBitmapImageRep(rep);");
            sb.AppendLine("    $.NSGraphicsContext.saveGraphicsState;");
            sb.AppendLine("    $.NSGraphicsContext.setCurrentContext(ctx);");
            sb.AppendLine("    icon.drawInRectFromRectOperationFraction($.NSMakeRect(0, 0, 128, 128), $.NSZeroRect, $.NSCompositingOperationSourceOver, 1.0);");
            sb.AppendLine("    $.NSGraphicsContext.restoreGraphicsState;");
            sb.AppendLine("    var png = rep.representationUsingTypeProperties(4, $.NSDictionary.dictionary);");
            sb.AppendLine("    png.writeToFileAtomically(outPath, true);");
            sb.AppendLine("  } catch(e) {}");
            sb.AppendLine("}");

            foreach (var (_, appPath, pngPath) in batch)
            {
                var escapedApp = appPath.Replace("\\", "\\\\").Replace("'", "\\'");
                var escapedPng = pngPath.Replace("\\", "\\\\").Replace("'", "\\'");
                sb.AppendLine($"extractIcon('{escapedApp}', '{escapedPng}');");
            }
            sb.AppendLine("'done'");

            scriptPath = Path.Combine(Path.GetTempPath(), $"fkfinder_icons_{Guid.NewGuid():N}.js");
            File.WriteAllText(scriptPath, sb.ToString());

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            process.StartInfo.ArgumentList.Add("-l");
            process.StartInfo.ArgumentList.Add("JavaScript");
            process.StartInfo.ArgumentList.Add(scriptPath);
            process.Start();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!process.WaitForExit(200))
            {
                if (cancellationToken.IsCancellationRequested || DateTime.UtcNow >= deadline)
                {
                    TryKillProcess(process);
                    cancellationToken.ThrowIfCancellationRequested();
                    break;
                }
            }

        }
        catch (OperationCanceledException) { throw; }
        catch { }
        finally
        {
            if (!string.IsNullOrWhiteSpace(scriptPath))
                TryDeleteFile(scriptPath);
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private FileSystemEntry CreateEntryFromFileInfo(FileInfo file)
    {
        return new FileSystemEntry
        {
            FullPath = file.FullName,
            Name = file.Name,
            IsDirectory = false,
            Size = file.Length,
            LastModified = file.LastWriteTime,
            Created = file.CreationTime,
            Extension = file.Extension.ToLowerInvariant(),
            IsHidden = file.Name.StartsWith('.'),
            IsSymbolicLink = file.Attributes.HasFlag(FileAttributes.ReparsePoint),
            IsReadable = true,
            IsWritable = !file.Attributes.HasFlag(FileAttributes.ReadOnly),
            IconKey = GetIconKeyForExtension(file.Extension)
        };
    }

    private static FileSystemEntry CreateInaccessibleEntry(string fullPath, string name, bool isDirectory)
    {
        return new FileSystemEntry
        {
            FullPath = fullPath,
            Name = name,
            IsDirectory = isDirectory,
            IsReadable = false,
            IconKey = isDirectory ? "folder" : "file-generic"
        };
    }

    private static string GetIconKeyForExtension(string extension)
    {
        return FileIconResolver.ResolveIconKey(extension);
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            File.Copy(file, Path.Combine(destinationDir, fileName));
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            CopyDirectoryRecursive(dir, Path.Combine(destinationDir, dirName));
        }
    }

    private static string GetUniqueName(string directory, string name)
    {
        var fullPath = Path.Combine(directory, name);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            return name;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        int counter = 1;

        while (true)
        {
            var newName = $"{nameWithoutExt} 副本{ext}";
            fullPath = Path.Combine(directory, newName);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                return newName;

            counter++;
            newName = $"{nameWithoutExt} 副本 {counter}{ext}";
            fullPath = Path.Combine(directory, newName);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                return newName;
        }
    }

    public bool IsCrossVolume(string sourcePath, string destinationPath)
    {
        return !string.Equals(GetVolumeRoot(sourcePath), GetVolumeRoot(destinationPath), StringComparison.Ordinal);
    }

    private static string GetVolumeRoot(string path)
    {
        // /Volumes/X/... → /Volumes/X
        if (path.StartsWith("/Volumes/", StringComparison.Ordinal))
        {
            var idx = path.IndexOf('/', "/Volumes/".Length);
            return idx < 0 ? path : path[..idx];
        }
        // Everything else is on the root volume
        return "/";
    }

    public async Task MoveWithProgressAsync(IReadOnlyList<string> sourcePaths, string destinationDirectory,
        IProgress<FileOperationProgress>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            // Phase 1: calculate total bytes
            long totalBytes = 0;
            foreach (var src in sourcePaths)
            {
                ct.ThrowIfCancellationRequested();
                totalBytes += CalculateTotalBytes(src);
            }
            if (totalBytes == 0) totalBytes = 1; // avoid division by zero

            long bytesCopied = 0;
            var copiedDestinations = new List<string>();

            try
            {
                // Phase 2: copy with progress
                foreach (var src in sourcePaths)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(src);
                    var destPath = Path.Combine(destinationDirectory, name);

                    if (Directory.Exists(src))
                    {
                        CopyDirectoryWithProgress(src, destPath, ref bytesCopied, totalBytes, progress, ct);
                    }
                    else if (File.Exists(src))
                    {
                        CopyFileWithProgress(src, destPath, ref bytesCopied, totalBytes, progress, ct);
                    }
                    copiedDestinations.Add(destPath);
                }

                // Phase 3: delete sources after all copies succeed
                foreach (var src in sourcePaths)
                {
                    if (Directory.Exists(src))
                        Directory.Delete(src, true);
                    else if (File.Exists(src))
                        File.Delete(src);
                }
            }
            catch (OperationCanceledException)
            {
                // Clean up partially copied files
                foreach (var dest in copiedDestinations)
                {
                    try
                    {
                        if (Directory.Exists(dest))
                            Directory.Delete(dest, true);
                        else if (File.Exists(dest))
                            File.Delete(dest);
                    }
                    catch { }
                }
                throw;
            }
        }, ct);
    }

    private static long CalculateTotalBytes(string path)
    {
        if (File.Exists(path))
            return new FileInfo(path).Length;

        if (!Directory.Exists(path))
            return 0;

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { }
            }
        }
        catch { }
        return total;
    }

    private static void CopyFileWithProgress(string src, string dest, ref long bytesCopied,
        long totalBytes, IProgress<FileOperationProgress>? progress, CancellationToken ct)
    {
        const int bufferSize = 64 * 1024;
        var buffer = new byte[bufferSize];

        using var sourceStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var destStream = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        int bytesRead;
        while ((bytesRead = sourceStream.Read(buffer, 0, bufferSize)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            destStream.Write(buffer, 0, bytesRead);
            bytesCopied += bytesRead;
            progress?.Report(new FileOperationProgress
            {
                Percentage = (double)bytesCopied / totalBytes * 100,
                CurrentFile = Path.GetFileName(src)
            });
        }
    }

    private static void CopyDirectoryWithProgress(string sourceDir, string destDir, ref long bytesCopied,
        long totalBytes, IProgress<FileOperationProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            CopyFileWithProgress(file, Path.Combine(destDir, fileName), ref bytesCopied, totalBytes, progress, ct);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            var dirName = Path.GetFileName(dir);
            CopyDirectoryWithProgress(dir, Path.Combine(destDir, dirName), ref bytesCopied, totalBytes, progress, ct);
        }
    }
}
