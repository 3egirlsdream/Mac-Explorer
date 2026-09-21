using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public partial class HomeDashboard : UserControl
{
    private FileListViewModel? _navigation;
    private IFileTagService? _tags;
    private HomeWorkspaceService? _workspace;
    private HomeItemActions? _actions;
    private readonly List<HomeFolderCard> _cards = [];
    private readonly SemaphoreSlim _previewGate = new(3, 3);
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _recentCancellation;
    private MainWindow? _expandedOwner;
    private bool _attached;

    public HomeDashboard()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => { _attached = true; Connect(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; Disconnect(); };
        DataContextChanged += (_, _) => { if (_attached) Connect(); };
    }

    private void Connect()
    {
        Disconnect();
        _navigation = DataContext as FileListViewModel;
        var services = App.Services;
        if (_navigation == null || services == null) return;
        _tags = services.GetService<IFileTagService>();
        _workspace = services.GetService<HomeWorkspaceService>();
        var files = services.GetService<IFileService>();
        var runner = services.GetService<HomeScriptRunner>();
        if (_tags == null || _workspace == null || files == null || runner == null)
        {
            StatusMessage.Text = "首页服务尚未初始化。";
            return;
        }
        _actions = new(this, _navigation, files, _tags, _workspace, runner, SetStatus);
        _navigation.PropertyChanged += OnNavigationChanged;
        _tags.TagsChanged += OnTagsChanged;
        _workspace.HistoryChanged += OnHistoryChanged;
        _workspace.PreferencesChanged += OnPreferencesChanged;
        if (_navigation.IsHomePage) _ = RefreshAsync();
    }

    private void Disconnect()
    {
        if (_navigation != null) _navigation.PropertyChanged -= OnNavigationChanged;
        if (_tags != null) _tags.TagsChanged -= OnTagsChanged;
        if (_workspace != null)
        {
            _workspace.HistoryChanged -= OnHistoryChanged;
            _workspace.PreferencesChanged -= OnPreferencesChanged;
        }
        CancelLoads();
        foreach (var card in _cards) card.Dispose();
        _cards.Clear(); FolderItems.Children.Clear();
        RecentItems.Children.Clear();
        _actions?.Dispose();
        _navigation = null; _tags = null; _workspace = null; _actions = null;
    }

    private void CancelLoads()
    {
        _refreshCancellation?.Cancel(); _refreshCancellation?.Dispose(); _refreshCancellation = null;
        _recentCancellation?.Cancel(); _recentCancellation?.Dispose(); _recentCancellation = null;
        _expandedOwner?.CloseHomeFolderImmediately();
    }

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileListViewModel.IsHomePage)) return;
        if (_navigation?.IsHomePage == true) _ = RefreshAsync();
        else CancelLoads();
    }
    private void OnTagsChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => { if (_attached && _navigation?.IsHomePage == true) _ = RefreshAsync(); });
    private void OnHistoryChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => { if (_attached && _navigation?.IsHomePage == true) _ = RefreshRecentAsync(); });
    private void OnPreferencesChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (!_attached || _workspace == null) return;
            foreach (var card in _cards) card.SetPreferredLayout(_workspace.GetLayout(card.TagDefinition));
        });

    private void SetStatus(string text) { StatusMessage.Text = text; StatusMessage.IsVisible = !string.IsNullOrEmpty(text); }

    private async Task RefreshAsync()
    {
        if (!_attached || _navigation?.IsHomePage != true || _tags == null || _actions == null || _workspace == null) return;
        _refreshCancellation?.Cancel(); _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        var token = _refreshCancellation.Token;
        var tagsService = _tags;
        var actions = _actions;
        var workspace = _workspace;
        try
        {
            // Coalesce tag-store/Finder notifications instead of re-querying on every emitted event.
            await Task.Delay(100, token);
            var tags = await Task.Run(() => tagsService.GetSidebarTagsAsync(token), token);
            token.ThrowIfCancellationRequested();
            foreach (var old in _cards) old.Dispose();
            _cards.Clear(); FolderItems.Children.Clear();
            SetStatus("");
            foreach (var tag in tags)
            {
                HomeFolderCard? card = null;
                card = new HomeFolderCard(tag, workspace.GetLayout(tag), entry => actions.CreateItemButton(entry, tag),
                    () => _ = actions.GuardAsync(() => ExpandAsync(card!)),
                    () => _ = actions.GuardAsync(() => actions.AddAsync(tag, folders: false)), actions.CreateFolderMenu(tag));
                card.PreviewRequested += current => _ = LoadPreviewAsync(current, actions, token);
                card.LayoutCommitted += current => workspace.SaveLayout(current.TagDefinition, current.PreferredLayout);
                card.SetAvailableWidth(AvailableCardWidth());
                EnableDrop(card, tag, actions);
                SetCardVisibility(card);
                _cards.Add(card); FolderItems.Children.Add(card);
            }
            _ = RefreshRecentAsync();
            await Task.WhenAll(_cards.Select(card => LoadFolderAsync(card, tagsService, token)));
            token.ThrowIfCancellationRequested();
            var visible = _cards.Count(card => card.IsVisible);
            SetStatus(visible == 0 ? "还没有收藏。新建标签，或把文件拖到收藏夹中。" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) SetStatus("读取收藏夹失败：" + ex.Message); }
    }

    private async Task LoadFolderAsync(HomeFolderCard card, IFileTagService tags, CancellationToken token)
    {
        await _previewGate.WaitAsync(token);
        try
        {
            var paths = await Task.Run(async () => (await tags.FindFilePathsAsync(card.TagDefinition, token))
                .Distinct(StringComparer.Ordinal).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal).ToArray(), token);
            token.ThrowIfCancellationRequested();
            card.SetPaths(paths);
            SetCardVisibility(card);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) card.SetLoadError(ex.Message);
        }
        finally { _previewGate.Release(); }
    }

    private async Task LoadPreviewAsync(HomeFolderCard card, HomeItemActions actions, CancellationToken parent)
    {
        if (parent.IsCancellationRequested) return;
        var token = card.BeginPreview(parent);
        try
        {
            await _previewGate.WaitAsync(token);
            try
            {
                var entries = await actions.LoadEntriesAsync(card.Paths.Take(card.PreviewCapacity).ToArray(), token);
                card.SetEntries(entries, token);
            }
            finally { _previewGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) card.SetLoadError(ex.Message); }
    }

    private async Task RefreshRecentAsync()
    {
        if (_workspace == null || _actions == null || _navigation?.IsHomePage != true) return;
        _recentCancellation?.Cancel(); _recentCancellation?.Dispose();
        _recentCancellation = new CancellationTokenSource();
        var token = _recentCancellation.Token;
        var workspace = _workspace; var actions = _actions;
        try
        {
            await Task.Delay(60, token);
            var history = await workspace.GetUsageAsync(false, cancellationToken: token);
            var entries = await actions.LoadEntriesAsync(history.Select(item => item.Path).ToArray(), token);
            token.ThrowIfCancellationRequested();
            RecentItems.Children.Clear();
            var addedTimes = history.ToDictionary(item => item.Path, item => item.AddedUtc, StringComparer.Ordinal);
            foreach (var entry in entries)
                RecentItems.Children.Add(actions.CreateItemButton(entry, compact: true, addedUtc: addedTimes[entry.FullPath]));
            SizeRecentItems();
            RecentEmpty.IsVisible = entries.Count == 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) SetStatus("读取使用记录失败：" + ex.Message); }
    }

    private void OnRecentAreaSizeChanged(object? sender, SizeChangedEventArgs e) => SizeRecentItems();
    private void SizeRecentItems()
    {
        var available = RecentItems.Bounds.Width > 0 ? RecentItems.Bounds.Width : 600;
        var columns = Math.Max(1, (int)(available / 200));
        var width = Math.Clamp(Math.Floor(available / columns) - 7, 80, 224);
        foreach (var item in RecentItems.Children.OfType<Button>()) item.Width = width;
    }

    private double AvailableCardWidth() => FolderItems.Bounds.Width > 0 ? Math.Max(80, FolderItems.Bounds.Width - 14) : 500;
    private void OnFolderAreaSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        foreach (var card in _cards) card.SetAvailableWidth(AvailableCardWidth());
    }
    private void SetCardVisibility(HomeFolderCard card)
        => card.IsVisible = card.TagDefinition.IsCustom || card.Paths.Count > 0;
    private void EnableDrop(HomeFolderCard card, FileTag tag, HomeItemActions actions)
    {
        DragDrop.SetAllowDrop(card, true);
        card.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            // Copy is the DnD protocol response only. The action below applies a tag; it never copies bytes.
            e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            card.SetDropHighlight(e.DragEffects != DragDropEffects.None);
            e.Handled = true;
        });
        card.AddHandler(DragDrop.DragLeaveEvent, (_, _) => card.SetDropHighlight(false));
        card.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            card.SetDropHighlight(false);
            var paths = e.DataTransfer.TryGetFiles()?.Where(item => item.Path.IsFile).Select(item => item.Path.LocalPath).ToArray() ?? [];
            e.DragEffects = paths.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            await actions.GuardAsync(() => actions.AddPathsAsync(tag, paths));
        });
    }

    private async Task ExpandAsync(HomeFolderCard card)
    {
        if (_expandedOwner != null || _actions == null || _tags == null || TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        _expandedOwner = owner;
        try { await owner.OpenHomeFolderAsync(card, new HomeFolderContent(card.TagDefinition, card.Paths, _tags, _actions, card.PreviewEntries)); }
        finally { _expandedOwner = null; }
    }

}
