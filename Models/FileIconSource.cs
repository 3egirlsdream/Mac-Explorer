namespace MacExplorer.Models;

/// <summary>
/// Immutable, UI-independent icon binding input. CachedImagePath is a lookup key,
/// never an instruction to stat, decode, download or generate an image.
/// </summary>
public readonly record struct FileIconSource(
    string IconKey,
    string Extension,
    bool IsDirectory,
    string? CachedImagePath);
