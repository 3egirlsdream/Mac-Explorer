using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Git")]
public sealed class GitMenuTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public async Task FileAndBackgroundMenusUseLoadedMembershipWithoutQueryingGit()
    {
        var root = Path.Combine(RuntimePaths.TestRoot!, "git-menu");
        var git = new TestGit(root) { Changes = [new("a.txt", 'M', 'M')] };
        var files = new FakeFileService(root);
        var entry = new FileSystemEntry { FullPath = Path.Combine(root, "a.txt"), Name = "a.txt", GitStatus = GitFileStatus.Modified, GitRepoRoot = root };
        files.Seed(entry);
        using var vm = CreateViewModel(files, gitStatusService: git);
        await vm.ShowFileContextMenuAsync(entry, 0, 0);
        var initial = vm.ContextMenuActions.ToArray();
        var menu = Assert.Single(initial, a => a.Tag == "git-menu");
        Assert.Equal(LocalizationText.Get("标签"), initial[Array.IndexOf(initial, menu) - 1].Label);
        Assert.Empty(git.LastPaths);
        var completed = await vm.LoadCompleteFileContextMenuAsync(entry, preserveGitMenu: true);
        Assert.Same(menu, Assert.Single(completed, a => a.Tag == "git-menu"));
        Assert.Empty(git.LastPaths);
        var commands = await menu.LoadSubItemsAsync!(CancellationToken.None, _ => { });
        Assert.Equal(10, commands.Count);
        Assert.All(commands.Take(2), a => Assert.True(a.IsEnabled));
        Assert.Equal(new[] { entry.FullPath }, git.LastPaths);
        Assert.All(new[] { 2, 4, 5, 6, 8, 9 }, i => Assert.NotEmpty(commands[i].IconSvg));
        git.Changes = [new("a.txt", '?', '?')];
        var refreshed = await menu.LoadSubItemsAsync!(CancellationToken.None, _ => { });
        Assert.Equal(LocalizationText.Get("git.add"), refreshed[4].Label);
        Assert.True(refreshed[4].IsEnabled); Assert.False(refreshed[0].IsEnabled); Assert.False(refreshed[6].IsEnabled);
        await vm.RefreshAsync();
        await Until(() => vm.Entries.Any(e => e.GitRepoRoot == root));
        git.LastPaths = [];
        await vm.ShowBackgroundContextMenuAsync(0, 0);
        Assert.Equal("git-menu", vm.ContextMenuActions.Last().Tag);
        Assert.Empty(git.LastPaths);
        await vm.LoadCompleteBackgroundContextMenuAsync();
        Assert.Empty(git.LastPaths);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task LateGitDecorationDoesNotInsertAnItemIntoAnAlreadyOpenMenu()
    {
        var root = Path.Combine(RuntimePaths.TestRoot!, "late-git-menu");
        var git = new TestGit(root);
        var entry = new FileSystemEntry { FullPath = Path.Combine(root, "a.txt"), Name = "a.txt" };
        using var vm = CreateViewModel(new FakeFileService(root), gitStatusService: git);
        await vm.ShowFileContextMenuAsync(entry, 0, 0);
        Assert.DoesNotContain(vm.ContextMenuActions, a => a.Tag == "git-menu");
        entry.GitStatus = GitFileStatus.Modified; entry.GitRepoRoot = root;
        Assert.DoesNotContain(await vm.LoadCompleteFileContextMenuAsync(entry, preserveGitMenu: true), a => a.Tag == "git-menu");
        await vm.ShowFileContextMenuAsync(entry, 0, 0);
        Assert.Contains(vm.ContextMenuActions, a => a.Tag == "git-menu");
        entry.GitStatus = GitFileStatus.Ignored;
        await vm.ShowFileContextMenuAsync(entry, 0, 0);
        Assert.DoesNotContain(vm.ContextMenuActions, a => a.Tag == "git-menu");
        Assert.Empty(git.LastPaths);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task CancelingDiscardDoesNotCallMutationAndConflictsDisableWrites()
    {
        using var theme = new FastListTestTheme();
        var root = Path.Combine(RuntimePaths.TestRoot!, "git-confirm");
        var git = new TestGit(root) { Changes = [new("a.txt", ' ', 'M')] };
        var entry = new FileSystemEntry { FullPath = Path.Combine(root, "a.txt"), Name = "a.txt", GitStatus = GitFileStatus.Modified, GitRepoRoot = root };
        using var vm = CreateViewModel(new FakeFileService(root), gitStatusService: git);
        var owner = new AppWindow { Width = 800, Height = 600 }; owner.Show(); vm.SetOwnerWindow(owner);
        try
        {
            var menu = Assert.Single(await vm.LoadCompleteFileContextMenuAsync(entry), a => a.Tag == "git-menu");
            var commands = await menu.LoadSubItemsAsync!(CancellationToken.None, _ => { });
            var pending = commands[6].Execute!();
            await Until(() => owner.OwnedWindows.OfType<ConfirmDialog>().Any());
            var confirm = owner.OwnedWindows.OfType<ConfirmDialog>().Single();
            var cancel = confirm.GetVisualDescendants().OfType<AppButton>().Single(b => b.Content?.ToString() == LocalizationText.Get("取消"));
            Assert.True(cancel.IsFocused);
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await pending;
            Assert.Equal(0, git.Mutations);
            git.Changes = [new("a.txt", 'U', 'U')];
            var conflict = await menu.LoadSubItemsAsync!(CancellationToken.None, _ => { });
            Assert.All(conflict.Skip(4).Take(3), a => Assert.False(a.IsEnabled));
        }
        finally { owner.Close(); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false, 1080)]
    [InlineData(true, 1080)]
    [InlineData(false, 440)]
    [InlineData(true, 440)]
    public async Task HistoryUsesVirtualizedListAndIgnoresOldPreviewResults(bool dark, int width)
    {
        using var theme = new FastListTestTheme();
        var git = new TestGit(RuntimePaths.TestRoot!);
        var dialog = new GitHistoryDialog(git, git.Context([RuntimePaths.TestRoot!]))
        { Width = width, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            dialog.Show(); dialog.UpdateLayout();
            await Until(() => dialog.GetVisualDescendants().OfType<ListBox>().Single().ItemCount == 2);
            var list = dialog.GetVisualDescendants().OfType<ListBox>().Single();
            await Until(() => git.DiffRequests == 1);
            list.SelectedIndex = 1;
            await Until(() => git.DiffRequests == 2);
            git.FirstDiff.TrySetResult("--- a/a\n+++ b/a\n@@ -1 +1 @@\n-old\n+STALE\n");
            await Until(() => dialog.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Any(e => e.Document.Text.Contains("current")));
            dialog.UpdateLayout();
            var preview = dialog.GetVisualDescendants().OfType<DiffPreviewView>().Single();
            Assert.True(preview.Bounds.Width > 100 && preview.Bounds.Height > 100);
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>(), e => e.Document.Text.Contains("STALE"));
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<AppButton>(), b => b.Content?.ToString() == "关闭");
            dialog.Close(); Assert.False(dialog.IsVisible);
        }
        finally { dialog.Close(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); }
    }

    private sealed class TestGit(string root) : IGitStatusService
    {
        public IReadOnlyList<GitPathState> Changes { get; set; } = [];
        public string[] LastPaths { get; set; } = [];
        public int Mutations { get; private set; }
        public int DiffRequests { get; private set; }
        public TaskCompletionSource<string> FirstDiff { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<string>? RepositoryChanged { add { } remove { } }
        public GitContext Context(IReadOnlyList<string> paths) => new(root, paths.Select(p => Path.GetRelativePath(root, p)).ToArray(), Changes, new string('a', 40), paths.Count == 1 && Path.HasExtension(paths[0]));
        public Task<GitContext?> GetContextAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        { LastPaths = paths.ToArray(); return Task.FromResult<GitContext?>(Context(paths)); }
        public Task<GitRepoStatus?> GetRepoStatusAsync(string directoryPath) => Task.FromResult<GitRepoStatus?>(new() { RepoRoot = root });
        public void InvalidateCache(string repoRoot) { }
        public Task<string> GetDiffAsync(GitContext context, bool staged, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task ApplyAsync(GitContext context, GitMutation mutation, CancellationToken cancellationToken = default) { Mutations++; return Task.CompletedTask; }
        public Task<GitHistoryPage> GetHistoryAsync(GitContext context, int skip, CancellationToken cancellationToken = default)
            => Task.FromResult(new GitHistoryPage([new(new string('a', 40), "", "QA", DateTimeOffset.UtcNow, "A long commit subject for layout validation", context.Paths), new(new string('b', 40), "", "QA", DateTimeOffset.UtcNow, "Second commit", context.Paths)], false));
        public Task<string> GetCommitDiffAsync(GitContext context, GitCommit commit, CancellationToken cancellationToken = default)
            => ++DiffRequests == 1 ? FirstDiff.Task : Task.FromResult("--- a/a\n+++ b/a\n@@ -1 +1 @@\n-before\n+current\n");
    }
}
