using FKFinder.Models;
using FKFinder.Indexing;
using FKFinder.Services;
using Foundation;
using System.Diagnostics;

namespace FKFinder.Platforms.MacCatalyst.Services;

public class MacFileService : IFileService
{
    private readonly SqliteFileIndex? _iconCache;
    private readonly string _iconCacheDir;
    public MacFileService(SqliteFileIndex? iconCache = null)
    {
        _iconCache = iconCache;
        _iconCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FKFinder", "icon-cache");
        if (!Directory.Exists(_iconCacheDir))
            Directory.CreateDirectory(_iconCacheDir);
    }

    public string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string RootDirectory => "/";
    // Use a sentinel path for trash - actual enumeration uses Finder AppleScript
    public string TrashDirectory => "__system_trash__";

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

                // Use safe enumeration with exception handling per item
                try
                {
                    foreach (var dirPath in Directory.EnumerateDirectories(path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            var dirInfo = new DirectoryInfo(dirPath);
                            entries.Add(CreateEntryFromDirectoryInfo(dirInfo));
                        }
                        catch (UnauthorizedAccessException)
                        {
                            var name = Path.GetFileName(dirPath);
                            entries.Add(CreateInaccessibleEntry(dirPath, name, isDirectory: true));
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Partial directory listing - continue with what we have
                }

                try
                {
                    foreach (var filePath in Directory.EnumerateFiles(path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            var fileInfo = new FileInfo(filePath);
                            entries.Add(CreateEntryFromFileInfo(fileInfo));
                        }
                        catch (UnauthorizedAccessException)
                        {
                            var name = Path.GetFileName(filePath);
                            entries.Add(CreateInaccessibleEntry(filePath, name, isDirectory: false));
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Partial file listing - continue with what we have
                }

                // Merge /System/Applications counterpart when browsing under /Applications
                if (path == "/Applications" || path.StartsWith("/Applications/"))
                {
                    var systemPath = "/System" + path;
                    if (Directory.Exists(systemPath))
                    {
                        try
                        {
                            foreach (var dirPath in Directory.EnumerateDirectories(systemPath))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                try
                                {
                                    var dirInfo = new DirectoryInfo(dirPath);
                                    if (!entries.Any(e => e.Name == dirInfo.Name))
                                        entries.Add(CreateEntryFromDirectoryInfo(dirInfo));
                                }
                                catch { }
                            }
                            foreach (var filePath in Directory.EnumerateFiles(systemPath))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                try
                                {
                                    var fileInfo = new FileInfo(filePath);
                                    if (!fileInfo.Name.StartsWith('.') && !entries.Any(e => e.Name == fileInfo.Name))
                                        entries.Add(CreateEntryFromFileInfo(fileInfo));
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

    private List<FileSystemEntry> EnumerateTrashViaFinder(CancellationToken ct)
    {
        var entries = new List<FileSystemEntry>();
        try
        {
            // Write AppleScript to temp file to avoid shell escaping issues
            var scriptPath = Path.Combine(Path.GetTempPath(), "fkfinder_trash.scpt");
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

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add(scriptPath);
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);

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
        await Task.Run(() =>
        {
            if (moveToTrash)
            {
                // Try to move to trash using NSFileManager via ObjCRuntime
                try
                {
                    var fileManager = Foundation.NSFileManager.DefaultManager;
                    var url = Foundation.NSUrl.CreateFileUrl(path, null);
                    var result = fileManager.TrashItem(url, out var resultingUrl, out var error);
                    if (!result && error != null)
                        throw new IOException(error.LocalizedDescription);
                    return;
                }
                catch (IOException)
                {
                    throw;
                }
                catch
                {
                    // Fallback to permanent delete
                }
            }

            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        });
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
        await Task.Run(() =>
        {
            try
            {
                // Use Finder AppleScript to empty system trash (handles all volumes + permissions)
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "osascript",
                        Arguments = "-e 'tell application \"Finder\" to empty trash'",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.Start();
                process.WaitForExit(30000);
            }
            catch
            {
                // Fallback: manual delete from user trash
                var trashPath = TrashDirectory;
                if (!Directory.Exists(trashPath)) return;
                foreach (var dir in Directory.EnumerateDirectories(trashPath))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
                foreach (var file in Directory.EnumerateFiles(trashPath))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        });
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

    public async Task MoveAsync(string sourcePath, string destinationDirectory)
    {
        await Task.Run(() =>
        {
            var name = Path.GetFileName(sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, name);

            if (Directory.Exists(sourcePath))
                Directory.Move(sourcePath, destinationPath);
            else if (File.Exists(sourcePath))
                File.Move(sourcePath, destinationPath);
        });
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

    public async Task ResolveAppIconsAsync(IEnumerable<FileSystemEntry> entries, Action? onBatchResolved = null)
    {
        var appEntries = entries.Where(e => e.IconKey == "app-bundle" && e.IconUrl == null).ToList();
        if (appEntries.Count == 0) return;

        // Build mapping: appPath → cachedPngPath (skip already cached)
        var toExtract = new List<(FileSystemEntry entry, string appPath, string pngPath)>();
        foreach (var entry in appEntries)
        {
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
            var batch = toExtract.Skip(i).Take(batchSize).ToList();
            await Task.Run(() => ExtractIconsBatchJXA(batch));

            foreach (var (entry, _, pngPath) in batch)
            {
                if (File.Exists(pngPath) && new FileInfo(pngPath).Length > 0)
                    entry.IconUrl = pngPath;
            }
            onBatchResolved?.Invoke();
        }
    }

    private void ExtractIconsBatchJXA(List<(FileSystemEntry entry, string appPath, string pngPath)> batch)
    {
        try
        {
            // Build a single JXA script that processes all apps in this batch
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ObjC.import('AppKit');");
            sb.AppendLine("ObjC.import('Foundation');");
            sb.AppendLine("var ws = $.NSWorkspace.sharedWorkspace;");
            // Create a 128x128 blank image to draw into for proper resizing
            sb.AppendLine("function extractIcon(appPath, outPath) {");
            sb.AppendLine("  try {");
            sb.AppendLine("    var icon = ws.iconForFile(appPath);");
            sb.AppendLine("    var sz = $.NSMakeSize(128, 128);");
            sb.AppendLine("    var newImg = $.NSImage.alloc.initWithSize(sz);");
            sb.AppendLine("    newImg.lockFocus;");
            sb.AppendLine("    icon.drawInRectFromRectOperationFraction($.NSMakeRect(0,0,128,128), $.NSZeroRect, $.NSCompositingOperationSourceOver, 1.0);");
            sb.AppendLine("    newImg.unlockFocus;");
            sb.AppendLine("    var tiff = newImg.TIFFRepresentation;");
            sb.AppendLine("    var rep = $.NSBitmapImageRep.imageRepWithData(tiff);");
            sb.AppendLine("    var png = rep.representationUsingTypeProperties($.NSBitmapImageFileTypePNG, $({}));");
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

            var scriptPath = Path.Combine(Path.GetTempPath(), $"fkfinder_icons_{Guid.NewGuid():N}.js");
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
            process.WaitForExit(30000);

            try { File.Delete(scriptPath); } catch { }
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
        return extension.ToLowerInvariant() switch
        {
            // Image (including RAW formats)
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tiff" or ".tif" or ".svg" or ".webp" or ".heic" or ".ico"
                or ".raw" or ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".orf" or ".rw2" or ".pef" => "file-image",
            // Video
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".flv" or ".webm" => "file-video",
            // Audio
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".wma" => "file-audio",
            // PDF
            ".pdf" => "file-pdf",
            // Microsoft Word style
            ".doc" or ".docx" or ".odt" or ".pages" => "file-word",
            // Microsoft Excel style
            ".xls" or ".xlsx" or ".csv" or ".numbers" => "file-excel",
            // Microsoft PowerPoint style
            ".ppt" or ".pptx" or ".key" => "file-powerpoint",
            // Archive / compressed
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".tgz" or ".zst" => "file-archive",
            // Markdown
            ".md" or ".mdx" or ".markdown" => "file-markdown",
            // Plain text files
            ".txt" or ".rtf" or ".log" or ".nfo" or ".ini" or ".cfg" or ".conf" or ".env" => "file-text",
            // Config / data files
            ".json" or ".yaml" or ".yml" or ".toml" or ".plist" or ".xml" => "file-config",
            // Web pages
            ".html" or ".htm" => "file-web",
            // Code (programming languages)
            ".cs" or ".js" or ".ts" or ".tsx" or ".jsx" or ".py" or ".java" or ".cpp" or ".c" or ".h" or ".hpp"
                or ".go" or ".rs" or ".swift" or ".kt" or ".rb" or ".php" or ".lua" or ".r" or ".m" or ".mm"
                or ".scala" or ".clj" or ".ex" or ".erl" or ".hs" or ".dart" or ".v" or ".zig" => "file-code",
            // Style / template (code-style icon)
            ".css" or ".scss" or ".sass" or ".less" or ".vue" or ".svelte" => "file-code",
            // Shell / script
            ".sh" or ".bash" or ".zsh" or ".fish" or ".bat" or ".cmd" or ".ps1" => "file-code",
            // Query languages
            ".sql" or ".graphql" or ".gql" => "file-code",
            // Certificate files
            ".cer" or ".crt" or ".pem" or ".p12" or ".pfx" or ".der" or ".cert" or ".ca-bundle" => "file-certificate",
            // System installers / packages
            ".pkg" or ".dmg" or ".msi" or ".exe" or ".deb" or ".rpm" or ".appimage" or ".snap" => "file-installer",
            // Font files
            ".ttf" or ".otf" or ".woff" or ".woff2" or ".eot" or ".fon" or ".ttc" => "file-font",
            // Database files
            ".db" or ".sqlite" or ".sqlite3" or ".mdb" or ".accdb" or ".dbf" or ".realm" => "file-database",
            // eBook files
            ".epub" or ".mobi" or ".azw" or ".azw3" or ".fb2" or ".djvu" or ".cbz" or ".cbr" => "file-ebook",
            // Design / vector files
            ".psd" or ".psb" or ".ai" or ".eps" or ".sketch" or ".fig" or ".xd" or ".indd" or ".afdesign" or ".afphoto" => "file-design",
            // Disk image / ISO
            ".iso" or ".img" or ".vhd" or ".vhdx" or ".vmdk" or ".qcow2" => "file-disk-image",
            // 3D model files
            ".obj" or ".fbx" or ".stl" or ".blend" or ".3ds" or ".dae" or ".gltf" or ".glb" or ".usdz" or ".step" or ".stp" => "file-3d",
            // Subtitle files
            ".srt" or ".ass" or ".ssa" or ".sub" or ".vtt" or ".idx" or ".lrc" => "file-subtitle",
            // Visual Studio / C# project files
            ".sln" => "file-vs-solution",
            ".csproj" or ".vbproj" or ".fsproj" => "file-vs-project",
            ".razor" => "file-razor",
            // App bundle
            ".app" => "app-bundle",
            _ => "file-generic"
        };
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
