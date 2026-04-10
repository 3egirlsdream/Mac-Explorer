namespace FKFinder.Models;

public class FileMetadata
{
    public string FullPath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public long Size { get; init; }
    public string FormattedSize { get; init; } = string.Empty;
    public DateTime Created { get; init; }
    public DateTime LastModified { get; init; }
    public DateTime LastAccessed { get; init; }
    public string Permissions { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public List<string> ExtendedAttributes { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public string ContentType { get; init; } = string.Empty;
    public ImageMetadata? ImageInfo { get; init; }
}
