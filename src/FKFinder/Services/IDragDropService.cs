namespace FKFinder.Services;

public interface IDragDropService
{
    Task<bool> DropFilesAsync(string[] sourcePaths, string targetDirectory, bool forceCopy, bool forceMove);
    bool IsSameVolume(string path1, string path2);
}
