using System.Diagnostics;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed class GitStatusServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fkfinder-git-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void AvailabilityRequiresASuccessfulGitVersionAndFallsBackToAnotherCandidate()
    {
        var attempts = new List<string>();
        var executable = GitStatusService.FindAvailableGit(["missing", "denied", "usable", "unused"], path =>
        {
            attempts.Add(path);
            return path switch { "denied" => "Operation not permitted", "usable" => "git version 2.50.1", _ => null };
        });
        Assert.Equal("usable", executable);
        Assert.Equal(new[] { "missing", "denied", "usable" }, attempts);
        Assert.Null(GitStatusService.FindAvailableGit(["missing"], _ => null));
        Assert.DoesNotContain("/usr/bin/git", GitStatusService.StoreGitCandidates);
    }

    [Fact]
    public async Task AvailableGitReportsChangesAndFailedReadsAreNotCachedAsClean()
    {
        var git = GitStatusService.StoreGitCandidates.First(File.Exists);
        await InitializeRepositoryAsync(git);
        await File.WriteAllTextAsync(Path.Combine(_root, ".gitignore"), "ignored.txt\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "tracked.txt"), "original\n");
        await RunAsync(git, "add", ".gitignore", "tracked.txt");
        await RunAsync(git, "-c", "user.name=QA", "-c", "user.email=qa@example.invalid", "commit", "-qm", "fixture");
        await File.WriteAllTextAsync(Path.Combine(_root, "tracked.txt"), "modified\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "untracked.txt"), "new\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "ignored.txt"), "ignored\n");
        var config = Path.Combine(_root, ".git", "config");
        var original = await File.ReadAllTextAsync(config);
        if (DistributionChannel.IsAppStore)
            await RunAsync(git, "config", "core.fsmonitor", "touch '" + Path.Combine(_root, "hook-ran") + "'");
        var index = Path.Combine(_root, ".git", "index");
        var indexBefore = await File.ReadAllBytesAsync(index);
        using var access = new DirectoryAccess(Path.Combine(_root, "grants.json"), false);
        using var scope = DirectoryAccess.UseForTests(access);
        using var service = new GitStatusService();
        var status = await service.GetRepoStatusAsync(_root);
        Assert.NotNull(status);
        Assert.Equal(GitFileStatus.Modified, status.FileStatuses["tracked.txt"]);
        Assert.Equal(GitFileStatus.Untracked, status.FileStatuses["untracked.txt"]);
        Assert.Contains("ignored.txt", GitStatusService.GetIgnoredPaths(_root));
        Assert.Contains("untracked.txt", GitStatusService.GetUntrackedPaths(_root));
        if (DistributionChannel.IsAppStore)
        {
            Assert.False(File.Exists(Path.Combine(_root, "hook-ran")));
            Assert.Equal(indexBefore, await File.ReadAllBytesAsync(index));
        }
        await File.WriteAllTextAsync(config, "[invalid\n");
        service.InvalidateCache(_root);
        Assert.Null(await service.GetRepoStatusAsync(_root));
        await File.WriteAllTextAsync(config, original);
        Assert.NotNull(await service.GetRepoStatusAsync(_root));
        if (DistributionChannel.IsAppStore)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, ".gitattributes"), "tracked.txt filter=qa\n");
            await RunAsync(git, "config", "filter.qa.clean", "touch '" + Path.Combine(_root, "filter-ran") + "'; cat");
            service.InvalidateCache(_root);
            Assert.Null(await service.GetRepoStatusAsync(_root));
            Assert.False(File.Exists(Path.Combine(_root, "filter-ran")));
            await RunAsync(git, "config", "--remove-section", "filter.qa");
            await RunAsync(git, "config", "remote.origin.partialclonefilter", "blob:none");
            Assert.Null(await service.GetRepoStatusAsync(_root));
        }
    }

    [Fact]
    public async Task StoreRejectsUnauthorizedRepositoryBeforeReturningCachedStatus()
    {
        if (!DistributionChannel.IsAppStore) return;
        await InitializeRepositoryAsync(GitStatusService.StoreGitCandidates.First(File.Exists));
        using var service = new GitStatusService();
        using (var allowed = new DirectoryAccess(Path.Combine(_root, "allowed.json"), false))
        using (DirectoryAccess.UseForTests(allowed))
            Assert.NotNull(await service.GetRepoStatusAsync(_root));
        using var denied = new DirectoryAccess(Path.Combine(_root, "denied.json"), true, internalRoots: []);
        using var deniedScope = DirectoryAccess.UseForTests(denied);
        Assert.Null(await service.GetRepoStatusAsync(_root));
        Assert.Empty(GitStatusService.GetIgnoredPaths(_root));
        Assert.Empty(GitStatusService.GetUntrackedPaths(_root));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", false)]
    [InlineData("filter.lfs.clean\ngit-lfs clean -- %f\0", true)]
    [InlineData("filter.lfs.clean\ngit-lfs clean -- %f\0filter.lfs.clean\n\0", false)]
    [InlineData("filter.lfs.clean\n\0filter.lfs.clean\nexternal-command\0", true)]
    [InlineData("remote.origin.partialclonefilter\nblob:none\0", true)]
    [InlineData("malformed\0", true)]
    public void ExternalConfigurationUsesEffectiveValuesAndRejectsUnavailableReads(string? output, bool blocked)
        => Assert.Equal(blocked, GitStatusService.HasExternalGitConfiguration(output));

    private async Task InitializeRepositoryAsync(string git)
    {
        await RunAsync(git, "init", "-q");
        // CI machines may enable LFS globally; this fixture has no external filters.
        await RunAsync(git, "config", "filter.lfs.clean", "");
        await RunAsync(git, "config", "filter.lfs.process", "");
    }

    private async Task RunAsync(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
            { WorkingDirectory = _root, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await output;
        Assert.True(process.ExitCode == 0, await error);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
