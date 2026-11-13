using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Preview;

namespace MacExplorer.Views.Dialogs;

internal sealed class GitHistoryDialog : DialogWindow
{
    private readonly IGitStatusService _git;
    private readonly GitContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _selection;
    private readonly ObservableCollection<GitCommit> _commits = [];
    private readonly ListBox _list = new() { Classes = { "git-history" } };
    private readonly DiffPreviewView _preview = new();
    private readonly TextBlock _listMessage = Message();
    private readonly TextBlock _diffMessage = Message();
    private readonly AppButton _more = new() { Classes = { "secondary", "compact" }, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly AppButton _retry = new() { Classes = { "secondary", "compact" }, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
    private readonly Grid _layout = new() { ColumnDefinitions = new("260,*"), ColumnSpacing = 12 };
    private readonly Grid _history = new() { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 8 };
    private readonly Grid _detail = new() { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 8 };
    private bool _loading;
    private bool _closed;
    private bool _compact;

    internal GitHistoryDialog(IGitStatusService git, GitContext context)
    {
        _git = git; _context = context;
        Title = LocalizationText.Get("git.history");
        TitleBarContent = new TextBlock { Text = Title, Classes = { "dialog-window-title" } };
        Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MacExplorer/"))
        { Source = new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml") });
        Width = 1080; Height = 680; MinWidth = 420; MinHeight = 440; CanResize = true;
        _list.ItemsSource = _commits;
        _list.ItemTemplate = new FuncDataTemplate<GitCommit>((commit, _) =>
        {
            if (commit == null) return new TextBlock();
            var subject = AppTypography.BindFontSize(new TextBlock { Text = commit.Subject, TextTrimming = TextTrimming.CharacterEllipsis }, AppTypography.Body);
            ToolTip.SetTip(subject, commit.Subject);
            var detail = AppTypography.BindFontSize(new TextBlock { Text = commit.Detail, TextTrimming = TextTrimming.CharacterEllipsis }, AppTypography.Caption);
            detail.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextMutedBrush"));
            return new StackPanel { Spacing = 4, Children = { subject, detail } };
        });
        _more.Content = LocalizationText.Get("git.load-more");
        _retry.Content = LocalizationText.Get("git.retry");
        _list.SelectionChanged += async (_, _) => await LoadDiffAsync();
        _more.Click += async (_, _) => await LoadMoreAsync();
        _retry.Click += async (_, _) => await LoadDiffAsync();
        Grid.SetRow(_list, 1); Grid.SetRow(_more, 2);
        _history.Children.Add(_listMessage); _history.Children.Add(_list); _history.Children.Add(_more);
        Grid.SetRow(_retry, 1); Grid.SetRow(_preview, 2);
        _detail.Children.Add(_diffMessage); _detail.Children.Add(_retry); _detail.Children.Add(_preview);
        Grid.SetColumn(_detail, 1);
        _layout.Children.Add(_history); _layout.Children.Add(_detail);
        Content = new Border { Padding = new Thickness(16), Child = _layout };
        SizeChanged += (_, _) => UpdateColumns();
        Opened += async (_, _) => await LoadMoreAsync();
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); _selection?.Cancel();
            _preview.Clear(); _selection?.Dispose(); _lifetime.Dispose();
        };
    }

    private void UpdateColumns()
    {
        var compact = Bounds.Width < 720;
        if (compact == _compact) return;
        _compact = compact;
        _layout.ColumnDefinitions = new(compact ? "*" : "260,*");
        _layout.RowDefinitions = new(compact ? "160,*" : "*");
        _layout.RowSpacing = compact ? 12 : 0;
        Grid.SetColumn(_detail, compact ? 0 : 1);
        Grid.SetRow(_detail, compact ? 1 : 0);
    }

    private async Task LoadMoreAsync()
    {
        if (_loading || _closed) return;
        _loading = true; _more.IsEnabled = false;
        _listMessage.Text = LocalizationText.Get("git.loading"); _listMessage.IsVisible = true;
        try
        {
            var page = await _git.GetHistoryAsync(_context, _commits.Count, _lifetime.Token);
            if (_closed) return;
            foreach (var commit in page.Commits) _commits.Add(commit);
            _more.Content = LocalizationText.Get("git.load-more"); _more.IsVisible = page.HasMore;
            _listMessage.Text = LocalizationText.Get("git.no-history"); _listMessage.IsVisible = _commits.Count == 0;
            if (_list.SelectedItem == null && _commits.Count > 0) _list.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_closed) return;
            _listMessage.Text = ex.Message; _listMessage.IsVisible = true;
            _more.Content = LocalizationText.Get("git.retry"); _more.IsVisible = true;
        }
        finally { _loading = false; if (!_closed) _more.IsEnabled = true; }
    }

    private async Task LoadDiffAsync()
    {
        _selection?.Cancel(); _selection?.Dispose();
        if (_closed || _list.SelectedItem is not GitCommit commit) return;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _selection = request;
        _preview.Clear(); _retry.IsVisible = false;
        _diffMessage.Text = LocalizationText.Get("git.loading"); _diffMessage.IsVisible = true;
        try
        {
            var patch = await _git.GetCommitDiffAsync(_context, commit, request.Token);
            if (_closed || request.IsCancellationRequested || !ReferenceEquals(request, _selection)) return;
            var binary = patch.Contains("Binary files ", StringComparison.Ordinal) || patch.Contains("GIT binary patch", StringComparison.Ordinal);
            _diffMessage.Text = LocalizationText.Get(binary ? "git.binary-diff" : "git.no-diff");
            _diffMessage.IsVisible = binary || patch.Length == 0;
            if (patch.Length > 0) _preview.SetDocument(DiffPreviewDocument.Parse(patch));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_closed || request.IsCancellationRequested || !ReferenceEquals(request, _selection)) return;
            _diffMessage.Text = ex.Message; _diffMessage.IsVisible = true; _retry.IsVisible = true;
        }
    }

    private static TextBlock Message() => AppTypography.BindFontSize(new TextBlock
    { TextWrapping = TextWrapping.Wrap, Classes = { "settings-description" }, IsVisible = false }, AppTypography.Body);
}
