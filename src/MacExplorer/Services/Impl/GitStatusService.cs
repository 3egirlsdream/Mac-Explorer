using System.Collections.Concurrent;
using System.Diagnostics;
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
            var repoRoot = FindGitRoot(directoryPath);
            if (repoRoot == null) return null;

            if (_cache.TryGetValue(repoRoot, out var cached) && cached.IsValid)
                return cached;

            var statuses = new Dictionary<string, GitFileStatus>(StringComparer.Ordinal);
            RunGitStatus(repoRoot, statuses);

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

    private static string? FindGitRoot(string path)
    {
        var dir = Path.GetFullPath(path);
        while (dir != null)
        {
            var gitPath = Path.Combine(dir, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent!;
        }
        return null;
    }

    private static void RunGitStatus(string repoRoot, Dictionary<string, GitFileStatus> statuses)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/git",
            Arguments = "status --porcelain -z",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return;

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);

        if (string.IsNullOrEmpty(output)) return;

        var entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Length < 4) continue;
            var xy = entries[i][..2];
            var path = entries[i][3..]; // porcelain format: "XY PATH" — skip XY + space

            if (xy[0] == 'R')
            {
                if (i + 1 < entries.Length) { i++; path = entries[i]; statuses[path] = GitFileStatus.Renamed; }
                continue;
            }

            var status = ParsePorcelainStatus(xy);
            if (status != GitFileStatus.Unmodified)
                statuses[path] = status;
        }
    }

    private static GitFileStatus ParsePorcelainStatus(string xy)
    {
        var (x, y) = (xy[0], xy[1]);
        return (x, y) switch
        {
            ('?', '?') => GitFileStatus.Untracked,
            ('!', _) or (_, '!') => GitFileStatus.Ignored,
            ('A', _) or ('C', _) => GitFileStatus.Added,
            ('D', _) or (_, 'D') => GitFileStatus.Deleted,
            ('M', _) => GitFileStatus.Staged,
            (_, 'M') => GitFileStatus.Modified,
            ('U', _) or (_, 'U') or ('A', 'A') or ('D', 'U') or ('U', 'D') or ('D', 'D') => GitFileStatus.Conflicted,
            _ => GitFileStatus.Unmodified
        };
    }

    public void InvalidateCache(string repoRoot) => _cache.TryRemove(repoRoot, out _);

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
        catch (Exception ex) { _logger?.LogWarning(ex, "Failed to watch .git for {Repo}", repoRoot); }
    }

    public void Dispose() { _watcher?.Dispose(); _cache.Clear(); }
}
