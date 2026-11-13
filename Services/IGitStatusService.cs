using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IGitStatusService
{
    Task<GitRepoStatus?> GetRepoStatusAsync(string directoryPath);
    void InvalidateCache(string repoRoot);
    Task<string> GetDiffAsync(string path, bool staged, CancellationToken cancellationToken = default)
        => Task.FromResult("");
    event Action<string>? RepositoryChanged;
    Task<GitContext?> GetContextAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    Task<string> GetDiffAsync(GitContext context, bool staged, CancellationToken cancellationToken = default);
    Task ApplyAsync(GitContext context, GitMutation mutation, CancellationToken cancellationToken = default);
    Task<GitHistoryPage> GetHistoryAsync(GitContext context, int skip, CancellationToken cancellationToken = default);
    Task<string> GetCommitDiffAsync(GitContext context, GitCommit commit, CancellationToken cancellationToken = default);
}
