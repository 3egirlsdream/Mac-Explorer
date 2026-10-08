using System.Collections.Concurrent;
using System.Diagnostics;
using MacExplorer.Models;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public class GitStatusService : IGitStatusService, IDisposable
{
    internal static readonly string[] StoreGitCandidates =
    [
        "/Library/Developer/CommandLineTools/usr/bin/git",
        "/Applications/Xcode.app/Contents/Developer/usr/bin/git",
        "/opt/homebrew/bin/git",
        "/usr/local/bin/git"
    ];
    private static readonly Lazy<string?> StoreGit = new(() => FindAvailableGit(
        StoreGitCandidates.Where(File.Exists), path => RunGitProcess(path, null, "version")));
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
            if (!RunGitStatus(repoRoot, statuses)) return null;

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
            if (DistributionChannel.IsAppStore &&
                (!DirectoryAccess.Current.CanAccess(dir) || !DirectoryAccess.Current.CanAccess(gitPath))) return null;
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent!;
        }
        return null;
    }

    private static bool RunGitStatus(string repoRoot, Dictionary<string, GitFileStatus> statuses)
    {
        var output = RunGitCommand(repoRoot, "status");
        if (output == null) return false;

        var entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Length < 4) continue;
            var xy = entries[i][..2];
            var path = entries[i][3..];

            if (xy[0] is 'R' or 'C')
            {
                if (path.EndsWith('/')) path = path[..^1];
                statuses[path] = xy[0] == 'R' ? GitFileStatus.Renamed : GitFileStatus.Added;
                if (i + 1 < entries.Length)
                    i++;
                continue;
            }

            // Strip trailing slash from untracked directory paths (e.g. "宣传/" → "宣传")
            // so they match relativePath computed without trailing slash
            if (path.EndsWith('/'))
                path = path[..^1];

            var status = ParsePorcelainStatus(xy);
            if (status != GitFileStatus.Unmodified)
                statuses[path] = status;
        }
        return true;
    }

    /// <summary>Get all ignored paths under repoRoot using git ls-files. Returns set of ignored relative paths (no trailing slashes).</summary>
    public static HashSet<string> GetIgnoredPaths(string repoRoot)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal);

        var output = RunGitCommand(repoRoot, "ignored");
        if (string.IsNullOrEmpty(output)) return ignored;

        foreach (var raw in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // Unquote paths with non-ASCII chars (git quotes as "octal-escaped")
            var path = raw.StartsWith('"') ? UnquotePath(raw) : raw;
            // Strip trailing slash from directories
            if (path.EndsWith('/'))
                path = path[..^1];
            ignored.Add(path);
        }

        return ignored;
    }

    /// <summary>Get untracked (non-ignored) paths under repoRoot. Returns set of relative paths (no trailing slashes).</summary>
    public static HashSet<string> GetUntrackedPaths(string repoRoot)
    {
        var untracked = new HashSet<string>(StringComparer.Ordinal);

        var output = RunGitCommand(repoRoot, "untracked");
        if (string.IsNullOrEmpty(output)) return untracked;

        foreach (var raw in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = raw.StartsWith('"') ? UnquotePath(raw) : raw;
            if (path.EndsWith('/'))
                path = path[..^1];
            untracked.Add(path);
        }

        return untracked;
    }

    internal static string? FindAvailableGit(IEnumerable<string> candidates, Func<string, string?> getVersion)
        => candidates.FirstOrDefault(path => getVersion(path)?.StartsWith("git version ", StringComparison.Ordinal) == true);

    internal static bool HasExternalGitConfiguration(string? output)
    {
        if (output == null) return true;
        // Git emits all scopes in precedence order, including overridden values.
        var effective = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('\n');
            if (separator <= 0) return true;
            effective[entry[..separator]] = entry[(separator + 1)..];
        }
        return effective.Values.Any(value => value.Length != 0);
    }

    private static string? RunGitCommand(string repoRoot, string operation)
    {
        try
        {
            if (DistributionChannel.IsAppStore &&
                (!DirectoryAccess.Current.CanAccess(repoRoot) || !DirectoryAccess.Current.CanAccess(Path.Combine(repoRoot, ".git")))) return null;
            var executable = DistributionChannel.IsAppStore ? StoreGit.Value : "/usr/bin/git";
            // Filters and partial clones may start external programs or fetch
            // missing objects. They cannot supply a passive Store decoration.
            if (DistributionChannel.IsAppStore && executable != null && operation == "status"
                && HasExternalGitConfiguration(RunGitProcess(executable, repoRoot, "filters"))) return null;
            return executable == null ? null : RunGitProcess(executable, repoRoot, operation);
        }
        catch { return null; }
    }

    private static string? RunGitProcess(string executable, string? repoRoot, string operation)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = DistributionChannel.IsAppStore
                    ? Path.Combine(RuntimePaths.BundleExecutableDirectory, "MacExplorer.Git") : executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // The Store helper enters the repository after resolving its bookmarks.
            if (repoRoot != null && !DistributionChannel.IsAppStore) psi.WorkingDirectory = repoRoot;
            if (DistributionChannel.IsAppStore)
            {
                psi.ArgumentList.Add(executable);
                psi.ArgumentList.Add(repoRoot ?? "-");
                psi.ArgumentList.Add(operation);
                if (repoRoot != null) DirectoryAccess.Current.ConfigureHelper(psi, repoRoot, Path.Combine(repoRoot, ".git"));
            }
            else
            {
                string[] arguments = operation switch
                {
                    "status" => ["status", "--porcelain", "-z"],
                    "ignored" => ["ls-files", "-o", "-i", "--exclude-standard", "-z", "--directory"],
                    "untracked" => ["ls-files", "-o", "--exclude-standard", "-z", "--directory"],
                    _ => ["--version"]
                };
                foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            }

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(repoRoot == null ? 1000 : 5000))
            {
                try { proc.Kill(entireProcessTree: true); }
                catch { }
                return null;
            }

            if (!Task.WaitAll([outputTask, errorTask], 1000)) return null;
            return proc.ExitCode == 0 || operation == "filters" && proc.ExitCode == 1 ? outputTask.Result : null;
        }
        catch { return null; }
    }

    private static string UnquotePath(string quoted)
    {
        // Git quotes non-ASCII paths as "\xxx\yyy..." (octal UTF-8)
        // Strip surrounding quotes and decode
        var inner = quoted[1..^1]; // remove leading/trailing "
        using var ms = new MemoryStream();
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 3 <= inner.Length)
            {
                // Parse 3-digit octal
                var octal = inner.Substring(i + 1, 3);
                if (octal.All(c => c >= '0' && c <= '7'))
                {
                    ms.WriteByte(Convert.ToByte(octal, 8));
                    i += 3;
                    continue;
                }
            }
            var b = (byte)inner[i];
            ms.WriteByte(b);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
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
        if (!Directory.Exists(repoRoot)) return;
        try
        {
            // Watch the working tree (not .git) so file edits invalidate the cache,
            // while .git-internal changes (refs, HEAD) don't cause a feedback loop.
            _watcher = new FileSystemWatcher(repoRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, e) => { if (!IsInsideGitDir(e.FullPath)) InvalidateCache(repoRoot); };
            _watcher.Created += (_, e) => { if (!IsInsideGitDir(e.FullPath)) InvalidateCache(repoRoot); };
            _watcher.Deleted += (_, e) => { if (!IsInsideGitDir(e.FullPath)) InvalidateCache(repoRoot); };
            _watcher.Renamed += (_, e) => { if (!IsInsideGitDir(e.FullPath)) InvalidateCache(repoRoot); };
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "Failed to watch repo for {Repo}", repoRoot); }
    }

    private static bool IsInsideGitDir(string fullPath) =>
        fullPath.Contains("/.git/", StringComparison.Ordinal) || fullPath.EndsWith("/.git", StringComparison.Ordinal);

    public void Dispose() { _watcher?.Dispose(); _cache.Clear(); }
}
