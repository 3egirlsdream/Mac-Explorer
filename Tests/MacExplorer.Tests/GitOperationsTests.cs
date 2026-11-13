using System.Diagnostics;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Integration")]
[Trait("Area", "Git")]
public sealed class GitOperationsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fkfinder-git-menu-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly GitStatusService _service = new();
    private string FilePath(string name) => Path.Combine(_root, name);
    private async Task<GitContext> Context(params string[] paths) => Assert.IsType<GitContext>(await _service.GetContextAsync(paths.Length == 0 ? [_root] : paths.Select(FilePath).ToArray()));

    [Fact(Explicit = true)]
    public async Task StageUnstageAndDiscardKeepOtherFilesAndStagedContent()
    {
        await Init();
        await File.WriteAllTextAsync(FilePath("selected.txt"), "base\n");
        await File.WriteAllTextAsync(FilePath("other.txt"), "base\n");
        await Git("add", "."); await Commit("base");
        await File.WriteAllTextAsync(FilePath("selected.txt"), "staged\n");
        await File.WriteAllTextAsync(FilePath("other.txt"), "other\n");
        var changed = 0; _service.RepositoryChanged += _ => changed++;
        await _service.ApplyAsync(await Context("selected.txt"), GitMutation.Stage);
        await File.WriteAllTextAsync(FilePath("selected.txt"), "unstaged\n");
        var both = await Context("selected.txt");
        Assert.True(both.HasStaged && both.HasUnstaged && both.CanStage);
        Assert.Contains("staged", await _service.GetDiffAsync(both, true));
        Assert.Contains("unstaged", await _service.GetDiffAsync(both, false));
        await _service.ApplyAsync(both, GitMutation.Discard);
        Assert.Equal("staged\n", await File.ReadAllTextAsync(FilePath("selected.txt")));
        Assert.Equal("other\n", await File.ReadAllTextAsync(FilePath("other.txt")));
        Assert.True((await Context("selected.txt")).HasStaged);
        await _service.ApplyAsync(await Context("selected.txt"), GitMutation.Unstage);
        Assert.False((await Context("selected.txt")).HasStaged);
        Assert.Equal("staged\n", await File.ReadAllTextAsync(FilePath("selected.txt")));
        Assert.Equal(3, changed);
    }

    [Fact(Explicit = true)]
    public async Task DirectoryScopeHandlesDeletedFilesButKeepsNewAndLaterChangesWhenDiscarding()
    {
        await Init(); Directory.CreateDirectory(FilePath("folder"));
        await File.WriteAllTextAsync(FilePath("folder/a.txt"), "a\n");
        await File.WriteAllTextAsync(FilePath("folder/b.txt"), "b\n");
        await File.WriteAllTextAsync(FilePath("outside.txt"), "outside\n");
        await Git("add", "."); await Commit("base");
        File.Delete(FilePath("folder/a.txt"));
        await File.WriteAllTextAsync(FilePath("folder/new.txt"), "new\n");
        var confirmed = await Context("folder");
        await File.WriteAllTextAsync(FilePath("folder/b.txt"), "later\n");
        await _service.ApplyAsync(confirmed, GitMutation.Discard);
        Assert.Equal("a\n", await File.ReadAllTextAsync(FilePath("folder/a.txt")));
        Assert.Equal("later\n", await File.ReadAllTextAsync(FilePath("folder/b.txt")));
        Assert.True(File.Exists(FilePath("folder/new.txt")));
        File.Delete(FilePath("folder/a.txt"));
        await File.WriteAllTextAsync(FilePath("outside.txt"), "changed\n");
        await _service.ApplyAsync(await Context("folder"), GitMutation.Stage);
        var root = await Context();
        Assert.True(root.Changes.Single(c => c.Path == "folder/a.txt").HasStaged);
        Assert.False(root.Changes.Single(c => c.Path == "outside.txt").HasStaged);
    }

    [Fact(Explicit = true)]
    public async Task UnbornRepositoryAndLiteralSpecialNamesCanBeStagedAndUnstaged()
    {
        await Init();
        foreach (var name in new[] { "中文 [1].txt", "-option.txt", "line\nbreak.txt" }) await File.WriteAllTextAsync(FilePath(name), "new\n");
        var context = await Context(); Assert.Null(context.Head);
        Assert.Empty((await _service.GetHistoryAsync(context, 0)).Commits);
        await _service.ApplyAsync(context, GitMutation.Stage);
        await File.WriteAllTextAsync(FilePath("-option.txt"), "edited after staging\n");
        await _service.ApplyAsync(await Context(), GitMutation.Unstage);
        Assert.All((await Context()).Changes, c => Assert.True(c.IsUntracked));
        Assert.Equal("edited after staging\n", await File.ReadAllTextAsync(FilePath("-option.txt")));
    }

    [Fact(Explicit = true)]
    public async Task IgnoredMetadataCrossRepositoryAndSymlinkDirectoriesAreRejected()
    {
        await Init();
        await File.WriteAllTextAsync(FilePath(".gitignore"), "ignored/\n");
        Directory.CreateDirectory(FilePath("ignored")); await File.WriteAllTextAsync(FilePath("ignored/a"), "x");
        Assert.Null(await _service.GetContextAsync([FilePath("ignored/a")]));
        Assert.Null(await _service.GetContextAsync([FilePath(".git/config")]));
        Directory.CreateDirectory(FilePath("nested")); await Git("-C", FilePath("nested"), "init", "-q");
        Assert.Null(await _service.GetContextAsync([FilePath(".gitignore"), FilePath("nested")]));
        Directory.CreateSymbolicLink(FilePath("alias"), FilePath("ignored"));
        await Assert.ThrowsAsync<IOException>(() => _service.GetContextAsync([FilePath("alias/a")]));
    }

    [Fact(Explicit = true)]
    public async Task RenameHistoryUsesHistoricalPathsAndRootCommitDiff()
    {
        await Init();
        await File.WriteAllTextAsync(FilePath("before.txt"), "original\n"); await Git("add", "."); await Commit("initial");
        await Git("mv", "before.txt", "after.txt");
        var renamed = await Context("after.txt");
        Assert.Equal("before.txt", Assert.Single(renamed.Changes).OriginalPath);
        Assert.Contains("rename from before.txt", await _service.GetDiffAsync(renamed, true));
        await Commit("rename");
        await File.WriteAllTextAsync(FilePath("after.txt"), "updated\n"); await Git("add", "."); await Commit("update");
        var context = await Context("after.txt");
        var page = await _service.GetHistoryAsync(context, 0);
        Assert.Equal(new[] { "update", "rename", "initial" }, page.Commits.Select(c => c.Subject));
        var original = page.Commits.Last(); Assert.Contains("before.txt", original.Paths);
        Assert.Contains("+original", await _service.GetCommitDiffAsync(context, original));
        Assert.Contains("rename from before.txt", await _service.GetCommitDiffAsync(context, page.Commits[1]));
    }

    [Fact(Explicit = true)]
    public async Task HistoryPagesStayOnTheirOriginalHeadAndMergeDiffUsesFirstParent()
    {
        await Init(); await File.WriteAllTextAsync(FilePath("a.txt"), "a\n"); await Git("add", "."); await Commit("initial");
        for (var i = 0; i < 101; i++)
        {
            await File.WriteAllTextAsync(FilePath("a.txt"), "page " + i + "\n");
            await Git("add", "a.txt"); await Commit("page " + i);
        }
        var context = await Context(); var first = await _service.GetHistoryAsync(context, 0);
        Assert.Equal(100, first.Commits.Count); Assert.True(first.HasMore);
        await Git("commit", "--allow-empty", "-qm", "later");
        var second = await _service.GetHistoryAsync(context, 100);
        Assert.Equal(2, second.Commits.Count); Assert.False(second.HasMore);
        Assert.Empty(first.Commits.Select(c => c.Id).Intersect(second.Commits.Select(c => c.Id)));
        var branch = (await Git("branch", "--show-current")).Trim();
        await Git("checkout", "-qb", "side"); await File.WriteAllTextAsync(FilePath("side.txt"), "side\n"); await Git("add", "."); await Commit("side");
        await Git("checkout", branch); await Git("merge", "--no-ff", "side", "-m", "merge");
        context = await Context(); var merge = (await _service.GetHistoryAsync(context, 0)).Commits.First();
        Assert.Equal(2, merge.Parents.Split(' ').Length);
        Assert.Contains("+side", await _service.GetCommitDiffAsync(context, merge));
    }

    [Fact(Explicit = true)]
    public async Task ConflictAndIndexLockDoNotOverwriteFilesAndFailureInvalidatesStatus()
    {
        await Init(); await File.WriteAllTextAsync(FilePath("a.txt"), "base\n"); await Git("add", "."); await Commit("base");
        var branch = (await Git("branch", "--show-current")).Trim();
        await Git("checkout", "-qb", "side"); await File.WriteAllTextAsync(FilePath("a.txt"), "side\n"); await Git("add", "."); await Commit("side");
        await Git("checkout", branch); await File.WriteAllTextAsync(FilePath("a.txt"), "main\n"); await Git("add", "."); await Commit("main");
        await Git(false, "merge", "side");
        var conflict = await Context(); Assert.True(conflict.HasConflicts);
        foreach (var operation in Enum.GetValues<GitMutation>()) await Assert.ThrowsAsync<IOException>(() => _service.ApplyAsync(conflict, operation));
        await Git("merge", "--abort");
        await File.WriteAllTextAsync(FilePath("a.txt"), "changed\n");
        var notified = false; _service.RepositoryChanged += _ => notified = true;
        await File.WriteAllTextAsync(FilePath(".git/index.lock"), "lock");
        var locked = await Context();
        await Assert.ThrowsAsync<IOException>(() => _service.ApplyAsync(locked, GitMutation.Stage));
        Assert.True(notified); Assert.True(File.Exists(FilePath(".git/index.lock")));
        Assert.Equal("changed\n", await File.ReadAllTextAsync(FilePath("a.txt")));
    }

    [Fact(Explicit = true)]
    public async Task BinaryAndOversizedDiffsAreBoundedAndConcurrentWritesStayScoped()
    {
        await Init();
        await File.WriteAllBytesAsync(FilePath("binary.dat"), [0, 1, 2]);
        await File.WriteAllTextAsync(FilePath("large.txt"), new string('a', 2300000) + "\n");
        await Git("add", "."); await Commit("base");
        await File.WriteAllBytesAsync(FilePath("binary.dat"), [0, 3, 4]);
        await File.WriteAllTextAsync(FilePath("large.txt"), new string('b', 2300000) + "\n");
        Assert.Contains("Binary files ", await _service.GetDiffAsync(await Context("binary.dat"), false));
        var large = await Context("large.txt");
        var error = await Assert.ThrowsAsync<IOException>(() => _service.GetDiffAsync(large, false));
        Assert.Equal(LocalizationText.Get("git.diff-too-large"), error.Message);
        var binary = await Context("binary.dat");
        await Task.WhenAll(_service.ApplyAsync(large, GitMutation.Stage), _service.ApplyAsync(binary, GitMutation.Stage));
        Assert.All((await Context()).Changes, change => Assert.True(change.HasStaged));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.GetHistoryAsync(large, 0, canceled.Token));
    }

    [Fact(Explicit = true)]
    public async Task LinkedWorktreeUsesItsOwnIndexAndHistory()
    {
        await Init(); await File.WriteAllTextAsync(FilePath("a.txt"), "base\n"); await Git("add", "."); await Commit("base");
        var linked = FilePath("linked"); await Git("worktree", "add", "-qb", "linked-test", linked);
        var path = Path.Combine(linked, "a.txt"); await File.WriteAllTextAsync(path, "linked\n");
        var context = Assert.IsType<GitContext>(await _service.GetContextAsync([path]));
        Assert.Equal(linked, context.RepoRoot);
        await _service.ApplyAsync(context, GitMutation.Stage);
        Assert.True((await _service.GetContextAsync([path]))!.HasStaged);
        Assert.DoesNotContain((await Context()).Changes, c => c.Path == "a.txt");
        Assert.Single((await _service.GetHistoryAsync(context, 0)).Commits);
    }

    private async Task Init()
    {
        await Git("init", "-q");
        await Git("config", "user.name", "QA"); await Git("config", "user.email", "qa@example.invalid");
        foreach (var key in new[] { "filter.lfs.clean", "filter.lfs.smudge", "filter.lfs.process", "core.hooksPath" }) await Git("config", key, key == "core.hooksPath" ? "/dev/null" : "");
        await Git("config", "commit.gpgsign", "false");
    }
    private Task<string> Commit(string message) => Git("commit", "-qm", message);
    private Task<string> Git(params string[] args) => Git(true, args);
    private async Task<string> Git(bool success, params string[] args)
    {
        var start = new ProcessStartInfo("/usr/bin/git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(true); }
        Assert.Equal(success, process.ExitCode == 0);
        return await output + await error;
    }
    public void Dispose() { _service.Dispose(); Directory.Delete(_root, true); }
}
