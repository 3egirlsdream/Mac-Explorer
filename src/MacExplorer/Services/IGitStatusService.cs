using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IGitStatusService
{
    Task<GitRepoStatus?> GetRepoStatusAsync(string directoryPath);
    void InvalidateCache(string repoRoot);
}
