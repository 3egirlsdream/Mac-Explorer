using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IMetadataService
{
    Task<FileMetadata> GetMetadataAsync(string path);
}
