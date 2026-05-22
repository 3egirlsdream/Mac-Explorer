using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IAiTagService : IDisposable
{
    // Analysis status
    Task<bool> IsFileAnalyzedAsync(string filePath, long fileModifiedTicks);
    Task<IReadOnlyList<(string Path, long ModifiedTicks)>> GetUnanalyzedFilesAsync(
        IReadOnlyList<string> filePaths, IReadOnlyList<long> modifiedTicks);
    Task<IReadOnlyList<string>> GetAnalyzedPathsInDirectoryAsync(string parentPath);

    // Save & delete
    Task SaveAnalysisResultAsync(string filePath, long fileModifiedTicks, ImageAnalysisResult result);
    Task DeleteAnalysisForFileAsync(string filePath);
    Task DeleteAnalysisForFilesAsync(IReadOnlyList<string> filePaths);
    Task DeleteAnalysisForPathPrefixAsync(string pathPrefix);
    Task UpdateFilePathAsync(string oldPath, string newPath);

    // Tag queries
    Task<IReadOnlyList<AiTag>> GetTagsForFileAsync(string filePath);
    Task<IReadOnlyList<string>> SearchByTagAsync(string tagValue, string? tagType = null, int limit = 200);
    Task<IReadOnlyList<AiCategory>> SearchCategoriesAsync(string query, int limit = 5);
    Task<IReadOnlyList<AiCategory>> GetPopularTextTagsAsync(int limit = 40, int minLength = 2);

    // Face clusters
    Task<IReadOnlyList<FaceCluster>> GetAllFaceClustersAsync();
    Task<IReadOnlyList<string>> GetFilePathsForClusterAsync(int clusterId);
    Task SetClusterNameAsync(int clusterId, string name);
    Task MergeClustersAsync(int targetClusterId, int sourceClusterId);
    Task RunClusteringAsync(float distanceThreshold = 0.5f);

    // Categories
    Task<IReadOnlyList<AiCategory>> GetCategoriesByTypeAsync(string tagType);
    Task<IReadOnlyList<string>> GetAllTagTypesAsync();
    Task<IReadOnlyList<string>> GetFilePathsForCategoryAsync(string tagType, string tagValue);
}
