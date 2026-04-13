namespace FKFinder.Models;

public class FileSystemEntry
{
    public string FullPath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime LastModified { get; init; }
    public DateTime Created { get; init; }
    public string Extension { get; init; } = string.Empty;
    public bool IsHidden { get; init; }
    public bool IsSymbolicLink { get; init; }
    public bool IsReadable { get; init; } = true;
    public bool IsWritable { get; init; } = true;
    public string IconKey { get; init; } = "file-generic";
    public string? IconUrl { get; set; }
    public string? ThumbnailUrl { get; set; }

    // Virtual folder properties for AI view entries
    public bool IsVirtual { get; init; }
    public string? VirtualFolderType { get; init; }
    public string? VirtualFolderKey { get; init; }
    public int VirtualItemCount { get; init; }

    public string DisplayName => IconKey == "app-bundle" ? Path.GetFileNameWithoutExtension(Name) : Name;
    public string FormattedSize => IsVirtual ? $"{VirtualItemCount} 项" : FormatSize(Size, IsDirectory);

    private static string FormatSize(long bytes, bool isDirectory)
    {
        if (bytes <= 0) return isDirectory ? "--" : "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < units.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {units[order]}";
    }
}
