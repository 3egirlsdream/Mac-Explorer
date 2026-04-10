using FKFinder.Models;
using FKFinder.Services;

namespace FKFinder.Platforms.MacCatalyst.Services;

public class MacFileService : IFileService
{
    public string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string RootDirectory => "/";

    public async Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string path, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var entries = new List<FileSystemEntry>();

            try
            {
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
        return new FileSystemEntry
        {
            FullPath = dir.FullName,
            Name = dir.Name,
            IsDirectory = true,
            Size = 0,
            LastModified = dir.LastWriteTime,
            Created = dir.CreationTime,
            Extension = "",
            IsHidden = dir.Name.StartsWith('.'),
            IsSymbolicLink = dir.Attributes.HasFlag(FileAttributes.ReparsePoint),
            IsReadable = true,
            IsWritable = !dir.Attributes.HasFlag(FileAttributes.ReadOnly),
            IconKey = "folder"
        };
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
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tiff" or ".svg" or ".webp" or ".heic" or ".ico" => "file-image",
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".flv" or ".webm" => "file-video",
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".wma" => "file-audio",
            ".pdf" => "file-pdf",
            ".doc" or ".docx" or ".txt" or ".rtf" or ".odt" or ".pages" => "file-document",
            ".xls" or ".xlsx" or ".csv" or ".numbers" => "file-spreadsheet",
            ".ppt" or ".pptx" or ".key" => "file-presentation",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".dmg" => "file-archive",
            ".md" or ".mdx" or ".markdown" => "file-markdown",
            ".json" or ".yaml" or ".yml" or ".toml" or ".ini" or ".cfg" or ".conf" or ".env" or ".plist" or ".xml" => "file-config",
            ".cs" or ".js" or ".ts" or ".tsx" or ".jsx" or ".py" or ".java" or ".cpp" or ".c" or ".h" or ".hpp"
                or ".go" or ".rs" or ".swift" or ".kt" or ".rb" or ".php" or ".lua" or ".r" or ".m" or ".mm"
                or ".scala" or ".clj" or ".ex" or ".erl" or ".hs" or ".dart" or ".v" or ".zig" => "file-code",
            ".html" or ".htm" or ".css" or ".scss" or ".sass" or ".less" or ".vue" or ".svelte" => "file-code",
            ".sh" or ".bash" or ".zsh" or ".fish" or ".bat" or ".cmd" or ".ps1" => "file-code",
            ".sql" or ".graphql" or ".gql" => "file-code",
            ".app" => "file-executable",
            ".pkg" or ".deb" or ".rpm" => "file-package",
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
}
