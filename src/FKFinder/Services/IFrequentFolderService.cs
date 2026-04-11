namespace FKFinder.Services;

/// <summary>
/// Tracks frequently visited folders for quick navigation.
/// </summary>
public interface IFrequentFolderService
{
    /// <summary>
    /// Records a folder visit, incrementing its visit count.
    /// </summary>
    Task RecordVisitAsync(string folderPath);

    /// <summary>
    /// Gets the top N most frequently visited folders, ordered by visit count descending.
    /// </summary>
    Task<IReadOnlyList<FrequentFolder>> GetTopFoldersAsync(int count = 10);
}

public record FrequentFolder(string Path, string Name, int VisitCount, DateTime LastVisited);
