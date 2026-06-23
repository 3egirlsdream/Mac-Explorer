using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MacExplorer.Models;

public class FileSystemEntry : INotifyPropertyChanged
{
    private string? _iconUrl;
    private string? _thumbnailUrl;
    private bool _isCut;
    private bool _isSelected;
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
    public string? IconUrl
    {
        get => _iconUrl;
        set => SetField(ref _iconUrl, value);
    }

    public string? ThumbnailUrl
    {
        get => _thumbnailUrl;
        set => SetField(ref _thumbnailUrl, value);
    }

    public bool IsCut
    {
        get => _isCut;
        set => SetField(ref _isCut, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    // Virtual folder properties for AI view entries
    public bool IsVirtual { get; init; }
    public string? VirtualFolderType { get; init; }
    public string? VirtualFolderKey { get; init; }
    public int VirtualItemCount { get; init; }
    public GitFileStatus GitStatus { get; init; }
    public bool HasGitChanges { get; init; }

    public string DisplayName => IconKey == "app-bundle" ? Path.GetFileNameWithoutExtension(Name) : Name;
    public string FormattedSize => IsVirtual ? $"{VirtualItemCount} 项" : FormatSize(Size, IsDirectory);
    public string KindText => IsVirtual ? VirtualFolderType switch
    {
        "face" => "人物",
        "scene" => "场景",
        "object" => "物品",
        "animal" => "动物",
        "location" => "地点",
        "date" => "日期",
        _ => "AI 分类"
    } : IconKey == "app-bundle" ? "应用程序"
      : IsDirectory ? "文件夹"
      : Extension.TrimStart('.').ToUpperInvariant();
    public string VirtualCountText => IsVirtual ? $"{VirtualItemCount} 张照片" : string.Empty;
    public bool HasGitBadge => GitStatus is not GitFileStatus.None and not GitFileStatus.Ignored
                               || IsDirectory && HasGitChanges;
    public string GitBadgeText => GitStatus switch
    {
        GitFileStatus.Modified or GitFileStatus.Staged => "M",
        GitFileStatus.Added => "A",
        GitFileStatus.Deleted => "D",
        GitFileStatus.Renamed => "R",
        GitFileStatus.Untracked => "?",
        GitFileStatus.Conflicted => "!",
        _ => string.Empty
    };
    public string GitBadgeColor => GitStatus switch
    {
        GitFileStatus.Staged or GitFileStatus.Added or GitFileStatus.Unmodified => "#4CAF50",
        GitFileStatus.Deleted => "#F44336",
        GitFileStatus.Renamed => "#2196F3",
        GitFileStatus.Untracked => "#9E9E9E",
        GitFileStatus.Conflicted => "#FF5722",
        _ => "#E2B714"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

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
