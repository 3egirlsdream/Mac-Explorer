using FKFinder.Models;

namespace FKFinder.Services;

public interface IMetadataService
{
    Task<FileMetadata> GetMetadataAsync(string path);
}
