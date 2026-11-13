using System.Diagnostics;
using System.Text;
using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

public partial class GitStatusService
{
    private static readonly SemaphoreSlim[] WriteLocks = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public event Action<string>? RepositoryChanged;

    public async Task<GitContext?> GetContextAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0 || paths.Any(p => !Path.IsPathFullyQualified(p))) return null;
        var fullPaths = paths.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        var roots = await Task.Run(() => fullPaths.Select(p => FindGitRoot(Directory.Exists(p) ? p : Path.GetDirectoryName(p)!)).ToArray(), cancellationToken);
        var root = roots[0];
        if (root == null || roots.Any(r => r != root)) return null;
        if (new DirectoryInfo(root).LinkTarget != null) throw new IOException(LocalizationText.Get("git.invalid-scope"));
        var relative = fullPaths.Select(p => Path.GetRelativePath(root, p)).ToArray();
        if (relative.Any(p => !IsRepositoryPath(p))) return null;
        foreach (var path in fullPaths) ValidateAncestors(root, path);
        if ((await RunOperationAsync(root, "check-ignore", relative, cancellationToken, allowOne: true)).Length != 0) return null;
        var status = await RunOperationAsync(root, "scope-status", ["."], cancellationToken);
        var changes = ParsePathStates(status).Where(s => relative.Any(p => ContainsPath(p, s.Path) || s.OriginalPath != null && ContainsPath(p, s.OriginalPath))).ToArray();
        var head = (await RunOperationAsync(root, "head", [], cancellationToken, allowOne: true)).Trim();
        return new GitContext(root, relative, changes, head.Length == 0 ? null : head,
            fullPaths.Length == 1 && !Directory.Exists(fullPaths[0]));
    }

    private static bool ContainsPath(string scope, string path) => scope == "." || path == scope || path.StartsWith(scope + "/", StringComparison.Ordinal);

    internal static bool IsRepositoryPath(string path) => path.Length > 0 && !Path.IsPathFullyQualified(path)
        && !path.Split('/').Any(s => s is ".." || s.Equals(".git", StringComparison.OrdinalIgnoreCase));

    private static void ValidateAncestors(string root, string path)
    {
        // A symlink file belongs to Git; a symlink directory must not redirect a scoped operation.
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (directory != null && directory != root)
        {
            if (new DirectoryInfo(directory).LinkTarget != null) throw new IOException(LocalizationText.Get("git.invalid-scope"));
            directory = Path.GetDirectoryName(directory);
        }
        if (directory != root) throw new IOException(LocalizationText.Get("git.invalid-scope"));
    }

    internal static IReadOnlyList<GitPathState> ParsePathStates(string output)
    {
        var result = new List<GitPathState>();
        var entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4) continue;
            string? original = null;
            if ((entry[0] is 'R' or 'C' || entry[1] is 'R' or 'C') && i + 1 < entries.Length) original = entries[++i];
            result.Add(new(entry[3..], entry[0], entry[1], original));
        }
        return result;
    }

    private async Task<GitContext> RevalidateAsync(GitContext context, CancellationToken token)
    {
        var current = await GetContextAsync(context.Paths.Select(p => Path.Combine(context.RepoRoot, p)).ToArray(), token);
        if (current == null || current.RepoRoot != context.RepoRoot) throw new IOException(LocalizationText.Get("git.invalid-scope"));
        return current;
    }

    public async Task<string> GetDiffAsync(string path, bool staged, CancellationToken cancellationToken = default)
    {
        var context = await GetContextAsync([path], cancellationToken) ?? throw new IOException(LocalizationText.Get("git.no-repository"));
        return await GetDiffAsync(context, staged, cancellationToken);
    }

    public async Task<string> GetDiffAsync(GitContext context, bool staged, CancellationToken cancellationToken = default)
    {
        var current = await RevalidateAsync(context, cancellationToken);
        return await RunOperationAsync(current.RepoRoot, staged ? "diff-staged" : "diff", ExpandedPaths(current), cancellationToken);
    }

    private static string[] ExpandedPaths(GitContext context) => context.Paths
        .Concat(context.Changes.Where(s => s.OriginalPath != null).Select(s => s.OriginalPath!)).Distinct(StringComparer.Ordinal).ToArray();

    public async Task ApplyAsync(GitContext context, GitMutation mutation, CancellationToken cancellationToken = default)
    {
        var gate = WriteLocks[(uint)StringComparer.Ordinal.GetHashCode(context.RepoRoot) % (uint)WriteLocks.Length];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await RevalidateAsync(context, cancellationToken);
            if (current.HasConflicts) throw new IOException(LocalizationText.Get("git.conflicts"));
            if (mutation == GitMutation.Discard)
            {
                // The confirmation authorizes these exact files, not changes appearing afterwards.
                var confirmed = context.DiscardPaths.ToHashSet(StringComparer.Ordinal);
                var targets = current.DiscardPaths.Where(confirmed.Contains).ToArray();
                if (targets.Length > 0) await RunOperationAsync(current.RepoRoot, "discard", targets, cancellationToken, write: true);
            }
            else if (mutation == GitMutation.Stage && current.CanStage)
                await RunOperationAsync(current.RepoRoot, "stage", ExpandedPaths(current), cancellationToken, write: true);
            else if (mutation == GitMutation.Unstage && current.HasStaged)
                await RunOperationAsync(current.RepoRoot, current.Head == null ? "unstage-new" : "unstage",
                    current.Head == null ? current.Changes.Where(s => s.HasStaged).Select(s => s.Path).ToArray() : ExpandedPaths(current), cancellationToken, write: true);
        }
        finally
        {
            InvalidateCache(context.RepoRoot);
            gate.Release();
            RepositoryChanged?.Invoke(context.RepoRoot);
        }
    }

    public async Task<GitHistoryPage> GetHistoryAsync(GitContext context, int skip, CancellationToken cancellationToken = default)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        await RevalidateAsync(context, cancellationToken);
        if (context.Head == null) return new([], false);
        var output = await RunOperationAsync(context.RepoRoot, "history",
            new[] { context.Head, skip.ToString(System.Globalization.CultureInfo.InvariantCulture), context.FollowFile ? "follow" : "scope" }.Concat(context.Paths).ToArray(), cancellationToken);
        var commits = ParseHistory(output, context);
        return new(commits.Take(100).ToArray(), commits.Count > 100);
    }

    internal static IReadOnlyList<GitCommit> ParseHistory(string output, GitContext context)
    {
        var tokens = output.Split('\0');
        var commits = new List<GitCommit>();
        var i = 0;
        while (i < tokens.Length)
        {
            var id = tokens[i++].TrimStart('\n');
            if (!IsObjectId(id) || i + 3 >= tokens.Length) continue;
            var parents = tokens[i++]; var author = tokens[i++]; var date = DateTimeOffset.Parse(tokens[i++], System.Globalization.CultureInfo.InvariantCulture);
            var subject = tokens[i++]; var paths = new List<string>();
            while (i < tokens.Length && !IsObjectId(tokens[i].TrimStart('\n')))
            {
                var status = tokens[i++].TrimStart('\n');
                if (status.Length == 0) continue;
                if (i >= tokens.Length) break;
                paths.Add(tokens[i++]);
                if (status[0] is 'R' or 'C' && i < tokens.Length) paths.Add(tokens[i++]);
            }
            commits.Add(new(id, parents, author, date, subject, context.FollowFile && paths.Count > 0 ? paths.Distinct().ToArray() : context.Paths));
        }
        return commits;
    }

    internal static bool IsObjectId(string id) => id.Length is 40 or 64 && id.All(Uri.IsHexDigit);

    public async Task<string> GetCommitDiffAsync(GitContext context, GitCommit commit, CancellationToken cancellationToken = default)
    {
        await RevalidateAsync(context, cancellationToken);
        if (!IsObjectId(commit.Id) || commit.Paths.Any(p => !IsRepositoryPath(p))) throw new ArgumentException(nameof(commit));
        return await RunOperationAsync(context.RepoRoot, "commit-diff", new[] { commit.Id }.Concat(commit.Paths).ToArray(), cancellationToken);
    }

    internal static string[] OperationArguments(string operation, IReadOnlyList<string> args) => operation switch
    {
        "scope-status" => ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=dirty", "--", .. args],
        "check-ignore" => ["check-ignore", "--", .. args],
        "head" => ["rev-parse", "--verify", "--quiet", "HEAD"],
        "diff" => ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--", .. args],
        "diff-staged" => ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--cached", "--", .. args],
        "stage" => ["add", "--all", "--", .. args],
        "unstage" => ["restore", "--staged", "--", .. args],
        "unstage-new" => ["rm", "--cached", "-f", "--", .. args],
        "discard" => ["restore", "--worktree", "--", .. args],
        "history" => ["log", "--max-count=101", "--skip=" + args[1], "--format=%H%x00%P%x00%an%x00%aI%x00%s", "--name-status", "--no-ext-diff", "--no-textconv", "-z", "--diff-merges=first-parent", "--find-renames", .. (args[2] == "follow" ? new[] { "--follow" } : Array.Empty<string>()), args[0], "--", .. args.Skip(3)],
        "commit-diff" => ["show", "--format=", "--first-parent", "--root", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames", args[0], "--", .. args.Skip(1)],
        _ => throw new ArgumentException(nameof(operation))
    };

    private async Task<string> RunOperationAsync(string root, string operation, IReadOnlyList<string> args, CancellationToken token, bool allowOne = false, bool write = false)
    {
        DirectoryAccess.Current.EnsureAccess(root);
        DirectoryAccess.Current.EnsureAccess(Path.Combine(root, ".git"));
        var executable = DistributionChannel.IsAppStore ? StoreGit.Value : "/usr/bin/git";
        if (executable == null) throw new IOException(LocalizationText.Get("git.unavailable"));
        if (DistributionChannel.IsAppStore && HasExternalGitConfiguration(await Task.Run(() => RunGitProcess(executable, root, "filters"), token)))
            throw new IOException(LocalizationText.Get("git.external-filter"));
        var start = new ProcessStartInfo(DistributionChannel.IsAppStore ? Path.Combine(RuntimePaths.BundleExecutableDirectory, "MacExplorer.Git") : executable)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (DistributionChannel.IsAppStore)
        {
            foreach (var arg in new[] { executable, root, operation }.Concat(args)) start.ArgumentList.Add(arg);
            DirectoryAccess.Current.ConfigureHelper(start, root, Path.Combine(root, ".git"));
        }
        else
        {
            start.WorkingDirectory = root;
            foreach (var arg in new[] { "--no-pager", "--no-optional-locks", "--literal-pathspecs", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null", "-c", "core.quotepath=false" }.Where(a => operation != "check-ignore" || a != "--literal-pathspecs").Concat(OperationArguments(operation, args))) start.ArgumentList.Add(arg);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(write ? 30 : 10));
        using var process = Process.Start(start) ?? throw new IOException(LocalizationText.Get("git.unavailable"));
        try
        {
            var outputTask = ReadBoundedAsync(process.StandardOutput, process, operation, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, process, operation, timeout.Token);
            var exitTask = process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(outputTask, errorTask, exitTask).WaitAsync(timeout.Token);
            if (process.ExitCode != 0 && !(allowOne && process.ExitCode == 1)) throw new IOException((await errorTask).Trim());
            return await outputTask;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException(LocalizationText.Get("git.timeout")); }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, Process process, string operation, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[8192]; int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (text.Length + count > 4 * 1024 * 1024)
            {
                if (!process.HasExited) process.Kill(true);
                throw new IOException(LocalizationText.Get(operation is "diff" or "diff-staged" or "commit-diff" ? "git.diff-too-large" : "git.output-too-large"));
            }
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
