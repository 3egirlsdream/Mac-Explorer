using System.Collections.Concurrent;
using LibGit2Sharp;
using MacExplorer.Models;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public class GitStatusService : IGitStatusService, IDisposable
{
    private readonly ConcurrentDictionary<string, GitRepoStatus> _cache = new(StringComparer.Ordinal);
    private readonly ILogger<GitStatusService>? _logger;
    private FileSystemWatcher? _watcher;
    private string? _watchedRepo;

    public GitStatusService(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<GitStatusService>();
    }

    public Task<GitRepoStatus?> GetRepoStatusAsync(string directoryPath)
    {
        return Task.Run(() => GetRepoStatus(directoryPath));
    }

    private GitRepoStatus? GetRepoStatus(string directoryPath)
    {
        try
        {
            var repoRoot = Repository.Discover(directoryPath);
            if (string.IsNullOrEmpty(repoRoot)) return null;

            if (_cache.TryGetValue(repoRoot, out var cached) && cached.IsValid)
                return cached;

            using var repo = new Repository(repoRoot);
            var statuses = new Dictionary<string, GitFileStatus>(StringComparer.Ordinal);

            foreach (var item in repo.RetrieveStatus(new StatusOptions { Show = StatusShowOption.IndexAndWorkDir }))
            {
                var path = item.FilePath;
                var status = MapStatus(item.State);
                if (status != GitFileStatus.None)
                    statuses[path] = status;
            }

            var result = new GitRepoStatus
            {
                RepoRoot = repoRoot,
                FileStatuses = statuses,
                LastUpdated = DateTime.UtcNow
            };

            _cache[repoRoot] = result;
            WatchRepo(repoRoot);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get Git status for {Path}", directoryPath);
            return null;
        }
    }

    public void InvalidateCache(string repoRoot)
    {
        _cache.TryRemove(repoRoot, out _);
    }

    private static GitFileStatus MapStatus(FileStatus state)
    {
        if (state.HasFlag(FileStatus.Conflicted)) return GitFileStatus.Conflicted;
        if (state.HasFlag(FileStatus.DeletedFromIndex) || state.HasFlag(FileStatus.DeletedFromWorkdir))
            return GitFileStatus.Deleted;
        if (state.HasFlag(FileStatus.RenamedInIndex) || state.HasFlag(FileStatus.RenamedInWorkdir))
            return GitFileStatus.Renamed;
        if (state.HasFlag(FileStatus.NewInIndex)) return GitFileStatus.Added;
        if (state.HasFlag(FileStatus.ModifiedInIndex)) return GitFileStatus.Staged;
        if (state.HasFlag(FileStatus.ModifiedInWorkdir)) return GitFileStatus.Modified;
        if (state.HasFlag(FileStatus.NewInWorkdir)) return GitFileStatus.Untracked;
        if (state.HasFlag(FileStatus.Ignored)) return GitFileStatus.Ignored;
        return GitFileStatus.Unmodified;
    }

    private void WatchRepo(string repoRoot)
    {
        if (_watchedRepo == repoRoot) return;

        _watcher?.Dispose();
        _watchedRepo = repoRoot;

        var gitDir = Path.Combine(repoRoot, ".git");
        if (!Directory.Exists(gitDir)) return;

        try
        {
            _watcher = new FileSystemWatcher(gitDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Changed += (_, _) => InvalidateCache(repoRoot);
            _watcher.Created += (_, _) => InvalidateCache(repoRoot);
            _watcher.Deleted += (_, _) => InvalidateCache(repoRoot);
            _watcher.Renamed += (_, _) => InvalidateCache(repoRoot);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to watch .git directory for {Repo}", repoRoot);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _cache.Clear();
    }
}
