namespace MacExplorer.Services;

public interface IRatingService
{
    Task TransferPathAsync(string source, string destination, bool move) => Task.CompletedTask;
    Task DeletePathAsync(string path) => Task.CompletedTask;
    int GetRatingCached(string filePath);
    Task SetRatingAsync(string filePath, int rating);
    Task BatchLoadRatingsAsync(IEnumerable<string> filePaths);
}
