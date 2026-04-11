namespace FKFinder.Services;

public interface IRatingService
{
    int GetRatingCached(string filePath);
    Task SetRatingAsync(string filePath, int rating);
    Task BatchLoadRatingsAsync(IEnumerable<string> filePaths);
}
