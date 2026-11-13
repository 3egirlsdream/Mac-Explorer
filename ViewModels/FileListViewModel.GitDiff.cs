using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.Views.Dialogs;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    private (string Directory, string Root)? _directoryGitRepository;
    private ContextMenuAction? _contextGitMenu;

    private ContextMenuAction? BuildGitMenuForEntry(FileSystemEntry entry)
    {
        if (IsArchiveView) return null;
        var targets = SelectedEntries.Any(e => e.FullPath == entry.FullPath) ? SelectedEntries.ToArray() : [entry];
        if (targets.Any(e => e.IsVirtual || !e.HasGitBadge || e.GitRepoRoot == null)) return null;
        var root = targets[0].GitRepoRoot!;
        return targets.Any(e => e.GitRepoRoot != root) ? null : BuildGitMenu(targets.Select(e => e.FullPath).ToArray(), root);
    }

    private ContextMenuAction? BuildBackgroundGitMenu()
        => _directoryGitRepository is { } cached && cached.Directory == _navigation.CurrentPath
            ? BuildGitMenu([cached.Directory], cached.Root) : null;

    private ContextMenuAction? BuildGitMenu(IReadOnlyList<string> paths, string root)
    {
        if (_gitStatusService == null || paths.Count == 0 || paths.Any(p => !Path.IsPathFullyQualified(p)
            || !GitStatusService.IsRepositoryPath(Path.GetRelativePath(root, p)))) return null;
        return new ContextMenuAction
        {
            Label = LocalizationText.Get("git.menu"), IconSvg = MacExplorer.Assets.Icons.GitBranch, Tag = "git-menu",
            SubItems = [new ContextMenuAction { Label = LocalizationText.Get("git.loading"), IsEnabled = false }],
            LoadSubItemsAsync = async (token, _) =>
            {
                var current = await _gitStatusService.GetContextAsync(paths, token);
                return current == null ? [new ContextMenuAction { Label = LocalizationText.Get("git.invalid-scope"), IsEnabled = false }] : BuildGitActions(current);
            }
        };
    }

    private IReadOnlyList<ContextMenuAction> BuildGitActions(GitContext context)
    {
        ContextMenuAction Action(string key, bool enabled, Func<Task> execute, string icon = "") => new()
        {
            Label = LocalizationText.Get(key), IsEnabled = enabled, IconSvg = icon,
            Execute = () => RunGitActionAsync(execute)
        };
        return
        [
            Action("git.unstaged", context.HasUnstaged, async () =>
            {
                if (_topLevelWindow != null) await new GitDiffDialog(await _gitStatusService!.GetDiffAsync(context, false, _pluginLifetime.Token), false).ShowDialog(_topLevelWindow);
            }),
            Action("git.staged", context.HasStaged, async () =>
            {
                if (_topLevelWindow != null) await new GitDiffDialog(await _gitStatusService!.GetDiffAsync(context, true, _pluginLifetime.Token), true).ShowDialog(_topLevelWindow);
            }),
            Action("git.history", context.Head != null, async () =>
            {
                if (_topLevelWindow == null) return;
                var current = await _gitStatusService!.GetContextAsync(context.Paths.Select(p => Path.Combine(context.RepoRoot, p)).ToArray(), _pluginLifetime.Token);
                if (current == null) throw new IOException(LocalizationText.Get("git.invalid-scope"));
                await new GitHistoryDialog(_gitStatusService, current).ShowDialog(_topLevelWindow);
            }, MacExplorer.Assets.Icons.History),
            ContextMenuAction.Separator,
            Action(context.Changes.Count > 0 && context.Changes.All(c => c.IsUntracked) ? "git.add" : "git.stage", context.CanStage,
                () => _gitStatusService!.ApplyAsync(context, GitMutation.Stage, _pluginLifetime.Token), MacExplorer.Assets.Icons.Plus),
            Action("git.unstage", context.HasStaged && !context.HasConflicts,
                () => _gitStatusService!.ApplyAsync(context, GitMutation.Unstage, _pluginLifetime.Token), MacExplorer.Assets.Icons.Subtract),
            Action("git.discard", context.HasUnstaged && !context.HasConflicts, async () =>
            {
                if (_topLevelWindow == null) return;
                var current = await _gitStatusService!.GetContextAsync(context.Paths.Select(p => Path.Combine(context.RepoRoot, p)).ToArray(), _pluginLifetime.Token);
                if (current == null || current.HasConflicts || current.DiscardPaths.Count == 0) return;
                var scope = string.Join("\n", current.Paths.Take(5).Select(p => p == "." ? Path.GetFileName(current.RepoRoot) : p).Select(p => p.Replace("\r", "\\r").Replace("\n", "\\n")));
                if (current.Paths.Count > 5) scope += "\n…";
                var confirmed = await new ConfirmDialog(LocalizationText.Get("git.discard-title"),
                    LocalizationText.Get("git.discard-confirm", current.DiscardPaths.Count, scope), LocalizationText.Get("git.discard-accept")).ShowDialog<bool>(_topLevelWindow);
                if (confirmed) await _gitStatusService.ApplyAsync(current, GitMutation.Discard, _pluginLifetime.Token);
            }, MacExplorer.Assets.Icons.ArrowUndo),
            ContextMenuAction.Separator,
            Action("git.open-root", true, () => NavigateToAsync(context.RepoRoot), MacExplorer.Assets.Icons.Folder),
            Action("git.terminal", _launcherService != null, () => _launcherService!.OpenInTerminalAsync(context.RepoRoot), MacExplorer.Assets.Icons.Terminal)
        ];
    }

    private async Task RunGitActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_topLevelWindow != null) AppToast.ShowError(_topLevelWindow, ex.Message); }
    }

    private void OnGitRepositoryChanged(string root)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_disposed) return;
            var current = _navigation.CurrentPath;
            if (current != root && !current.StartsWith(root + "/", StringComparison.Ordinal)
                && !Entries.Any(e => e.FullPath == root || e.FullPath.StartsWith(root + '/', StringComparison.Ordinal))) return;
            await RunGitActionAsync(RefreshAsync);
        });
    }
}
