using FKFinder.Models;

namespace FKFinder.Services;

public interface ICollectionService
{
    Task<IReadOnlyList<Collection>> GetAllCollectionsAsync();
    Task<Collection> CreateCollectionAsync(string name, string? icon = null);
    Task RenameCollectionAsync(int collectionId, string newName);
    Task DeleteCollectionAsync(int collectionId);
    Task AddFileToCollectionAsync(int collectionId, string filePath);
    Task RemoveFileFromCollectionAsync(int collectionId, string filePath);
    Task<IReadOnlyList<string>> GetFilePathsInCollectionAsync(int collectionId);
}
