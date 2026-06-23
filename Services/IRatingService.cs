namespace MacExplorer.Services;

public interface IRatingService
{
    int GetRatingCached(string filePath);
    Task SetRatingAsync(string filePath, int rating);
    Task BatchLoadRatingsAsync(IEnumerable<string> filePaths);
    List<string> GetCustomTags(string filePath);
    List<string> GetSystemTags(string filePath);
    void AddCustomTag(string filePath, string tag);
    void RemoveCustomTag(string filePath, string tag);
    void ToggleSystemTag(string filePath, string tagName);
}
