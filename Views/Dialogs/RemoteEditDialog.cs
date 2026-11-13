using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Icons = MacExplorer.Assets.Icons;
using MacExplorer.Controls;
using MacExplorer.Converters;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views.Dialogs;

internal sealed class RemoteEditDialog : DialogWindow
{
    private readonly IRemoteFileEditService _edits;
    private readonly Func<string, Task> _revealLocalCopy;
    private readonly StackPanel _body = new() { Spacing = 18 };
    private readonly Grid _root = new() { Margin = new Thickness(20, 18, 20, 18), RowDefinitions = new("*,Auto"), RowSpacing = 18 };
    private readonly Grid _footer = new() { RowDefinitions = new("Auto,Auto"), RowSpacing = 12 };
    private readonly StackPanel _sessionActions = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly HashSet<string> _busyPaths = new(StringComparer.Ordinal);
    private AppComboBox? _picker;
    private IReadOnlyList<RemoteEditSession> _renderedSessions = [];
    private string? _selectedPath;
    private Action? _adaptLayout;
    private bool _closed;
    private int _refreshQueued;

    internal RemoteEditDialog(IRemoteFileEditService edits, Func<string, Task>? revealLocalCopy = null)
    {
        Title = LocalizationText.Get("remote-edit.title");
        TitleBarContent = new TextBlock { Text = Title, Classes = { "dialog-window-title", "centered" } };
        Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MacExplorer/"))
        { Source = new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml") });
        Width = 650; MaxHeight = 780; SizeToContent = SizeToContent.Height;
        _edits = edits;
        _revealLocalCopy = revealLocalCopy ?? (path => App.Services.GetRequiredService<IApplicationLauncherService>().RevealInFinderAsync(path));
        MinWidth = 400; CanResize = true;
        ToolTip.SetTip(TitleBarContent, Title);
        _root.Children.Add(new ScrollViewer { Content = _body, MaxHeight = 580, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _footer.Children.Add(Divider());
        Grid.SetRow(_sessionActions, 1); _footer.Children.Add(_sessionActions);
        Grid.SetRow(_footer, 1); _root.Children.Add(_footer); Content = _root;
        _root.SizeChanged += (_, _) => _adaptLayout?.Invoke();
        _edits.Changed += Refresh;
        Opened += (_, _) => _sessionActions.Children.OfType<AppButton>().FirstOrDefault(button => button.IsDefault)?.Focus();
        Closed += (_, _) => { _closed = true; _edits.Changed -= Refresh; };
        Rebuild();
    }

    private void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _refreshQueued, 0); if (!_closed) Rebuild(); });
    }

    private void Rebuild(bool force = false)
    {
        var sessions = _edits.Sessions;
        if (!force && sessions.SequenceEqual(_renderedSessions) && _body.Children.Count > 0) return;
        _renderedSessions = sessions;
        var selected = sessions.FirstOrDefault(session => session.LocalPath == _selectedPath)
            ?? sessions.FirstOrDefault(session => session.State is RemoteEditState.Conflict or RemoteEditState.Failed)
            ?? sessions.FirstOrDefault();
        _selectedPath = selected?.LocalPath;
        _body.Children.Clear(); _sessionActions.Children.Clear(); _picker = null;
        if (sessions.Count > 1)
        {
            _picker = new AppComboBox { Name = "RemoteEditSessionPicker", HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(_picker, LocalizationText.Get("remote-edit.select-file"));
            foreach (var session in sessions)
                _picker.Items.Add(new ComboBoxItem
                {
                    Content = LocalizationText.Get("remote-edit.file", Path.GetFileName(session.LocalPath), LocalizationText.Get("remote-edit.state." + session.State)),
                    Tag = session.LocalPath
                });
            _picker.SelectedIndex = sessions.ToList().FindIndex(session => session.LocalPath == _selectedPath);
            _picker.SelectionChanged += (_, _) =>
            {
                if (_picker?.SelectedItem is ComboBoxItem { Tag: string path } && path != _selectedPath)
                { _selectedPath = path; Rebuild(true); }
            };
            _body.Children.Add(_picker);
        }

        var state = selected?.State;
        var severity = state is RemoteEditState.Conflict or RemoteEditState.Failed ? "warning" : state == RemoteEditState.Synced ? "success" : "info";
        var icon = state switch { RemoteEditState.Conflict => Icons.DocumentSync, RemoteEditState.Failed => Icons.CloudDismiss,
            RemoteEditState.Synced => Icons.CheckmarkCircle, RemoteEditState.Stopped => Icons.Pause, _ => Icons.CloudUpload };
        var badge = new Panel { Width = 60, Height = 60, VerticalAlignment = VerticalAlignment.Top };
        badge.Children.Add(new Border { Classes = { "dialog-status-surface", severity } });
        badge.Children.Add(new PathIcon { Data = Geometry.Parse(icon), Classes = { "dialog-status-icon", severity } });
        var titleKey = state switch { RemoteEditState.Conflict => "remote-edit.heading.conflict", RemoteEditState.Failed => "remote-edit.heading.failed",
            null => "remote-edit.empty", _ => "remote-edit.state." + state };
        var heading = Value(LocalizationText.Get(titleKey), AppTypography.Display);
        heading.Name = "RemoteEditStatusHeading"; heading.FontWeight = FontWeight.SemiBold; heading.TextWrapping = TextWrapping.Wrap;
        var summary = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        summary.Children.Add(heading);
        if (state != null)
        {
            var description = Value(LocalizationText.Get("remote-edit.description." + state), AppTypography.BodyLarge, muted: true);
            description.TextWrapping = TextWrapping.Wrap; summary.Children.Add(description);
        }
        var hero = new Grid { ColumnDefinitions = new("60,*"), ColumnSpacing = 16, Margin = new Thickness(2, 2, 2, 0) };
        hero.Children.Add(badge); Grid.SetColumn(summary, 1); hero.Children.Add(summary); _body.Children.Add(hero);

        Grid? errorRow = null, localRow = null;
        if (selected is { } current)
        {
            var name = Path.GetFileName(current.LocalPath);
            var entry = new FileSystemEntry { FullPath = current.LocalPath, Name = name, Extension = Path.GetExtension(name) };
            var identity = new Grid { ColumnDefinitions = new("44,*"), ColumnSpacing = 16 };
            identity.Children.Add(new Image { Width = 44, Height = 44, Stretch = Stretch.Uniform,
                Source = new FileEntryToIconConverter().Convert(entry.DetailsIconSource, typeof(IImage), 128, LocalizationService.Current?.Culture ?? System.Globalization.CultureInfo.CurrentCulture) as IImage });
            var fileName = Value(name, AppTypography.Heading); fileName.FontWeight = FontWeight.SemiBold;
            fileName.TextTrimming = TextTrimming.CharacterEllipsis; ToolTip.SetTip(fileName, name);
            var remote = Value(LocalizationText.Get("remote-edit.remote-location", current.RemotePath), muted: true);
            remote.TextTrimming = TextTrimming.CharacterEllipsis; ToolTip.SetTip(remote, current.RemotePath);
            var fileDetails = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { fileName, remote } };
            Grid.SetColumn(fileDetails, 1); identity.Children.Add(fileDetails);
            var details = new StackPanel { Spacing = 14, Children = { identity, Divider() } };
            if (state == RemoteEditState.Failed)
            {
                var reason = Value(LocalizationText.Get("remote-edit.error." + current.FailureReason), AppTypography.BodyLarge);
                reason.FontWeight = FontWeight.SemiBold; reason.TextWrapping = TextWrapping.Wrap;
                var reasonDetails = new StackPanel { Spacing = 4, Children = { reason } };
                if (!string.IsNullOrWhiteSpace(current.Error))
                {
                    var technical = new SelectableTextBlock { Name = "RemoteEditErrorDetail", Text = current.Error, TextWrapping = TextWrapping.Wrap };
                    technical.Bind(TextBlock.FontFamilyProperty, technical.GetResourceObservable("FontFamilyMono"));
                    technical.Bind(TextBlock.ForegroundProperty, technical.GetResourceObservable("TextSecondaryBrush"));
                    AppTypography.BindFontSize(technical, AppTypography.Body); reasonDetails.Children.Add(technical);
                }
                errorRow = DetailRow("remote-edit.failure-reason", reasonDetails);
                details.Children.Add(errorRow); details.Children.Add(Divider());
            }
            var path = Value(current.LocalPath, muted: true); path.TextTrimming = TextTrimming.PathSegmentEllipsis;
            var pathContent = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
            pathContent.Children.Add(path);
            var chevron = new PathIcon { Data = Icons.ChevronRightGeometry, Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
            chevron.Bind(PathIcon.ForegroundProperty, chevron.GetResourceObservable("TextMutedBrush"));
            Grid.SetColumn(chevron, 1); pathContent.Children.Add(chevron);
            var reveal = new AppButton { Name = "RevealRemoteEditCopy", Content = pathContent, Classes = { "ghost", "dialog-location" } };
            ToolTip.SetTip(reveal, current.LocalPath); AutomationProperties.SetName(reveal, LocalizationText.Get("remote-edit.reveal-copy", name));
            reveal.Click += async (_, _) =>
            {
                try { await _revealLocalCopy(current.LocalPath); }
                catch (Exception ex) { if (!_closed) AppToast.ShowError(this, ex.Message); }
            };
            localRow = DetailRow("remote-edit.local-copy", reveal);
            details.Children.Add(localRow);
            _body.Children.Add(new Border { Classes = { "settings-group" }, Padding = new Thickness(16), Child = details });
            if (state == RemoteEditState.Conflict)
            {
                var note = Value(LocalizationText.Get("remote-edit.replace-note"), muted: true); note.TextWrapping = TextWrapping.Wrap;
                _body.Children.Add(note);
                AddAction(current.LocalPath, "remote-edit.overwrite", "ReplaceRemoteEdit", async () =>
                {
                    if (await new ConfirmDialog("remote-edit.overwrite", "remote-edit.confirm-overwrite", "remote-edit.overwrite").ShowDialog<bool>(this))
                        await _edits.ResolveConflictAsync(current.LocalPath, true);
                });
                AddAction(current.LocalPath, "remote-edit.keep-local", "KeepRemoteEditLocal", () => StopAsync(current.LocalPath), primary: true);
            }
            else
            {
                if (state != RemoteEditState.Stopped)
                {
                    var stop = new AppButton { Name = "StopRemoteEdit", Content = LocalizationText.Get("remote-edit.stop"), Classes = { "ghost" } };
                    AutomationProperties.SetName(stop, LocalizationText.Get("remote-edit.stop"));
                    stop.Click += (_, _) => _edits.StopWatching(current.LocalPath);
                    _sessionActions.Children.Add(stop);
                }
                if (state == RemoteEditState.Failed) AddAction(current.LocalPath, "remote-edit.reconnect", "ReconnectRemoteEdit", () => _edits.ReconnectAsync(current.LocalPath));
                if (state is RemoteEditState.Failed or RemoteEditState.Pending or RemoteEditState.Stopped)
                    AddAction(current.LocalPath, "remote-edit.retry", "RetryRemoteEdit", () => _edits.RetryAsync(current.LocalPath), primary: true);
            }
        }
        _footer.IsVisible = _sessionActions.Children.Count > 0;
        _root.RowSpacing = _footer.IsVisible ? 18 : 0;
        UpdateActionState();
        bool? wasNarrow = null, wereDetailsStacked = null;
        _adaptLayout = () =>
        {
            var width = _root.Bounds.Width;
            if (width <= 0) return;
            foreach (var button in _sessionActions.Children) button.Measure(Size.Infinity);
            var needed = _sessionActions.Children.Sum(button => button.DesiredSize.Width) + Math.Max(0, _sessionActions.Children.Count - 1) * _sessionActions.Spacing;
            var narrow = needed > width;
            if (wasNarrow != narrow)
            {
                wasNarrow = narrow;
                _sessionActions.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
                _sessionActions.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
                foreach (var button in _sessionActions.Children) button.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            }
            var stacked = width < 480 || state == RemoteEditState.Conflict;
            if (wereDetailsStacked == stacked) return;
            wereDetailsStacked = stacked;
            foreach (var row in new[] { errorRow, localRow }.OfType<Grid>())
            {
                row.ColumnDefinitions = new(stacked ? "*" : "108,*"); row.RowDefinitions = new(stacked ? "Auto,Auto" : "Auto");
                Grid.SetColumn(row.Children[1], stacked ? 0 : 1); Grid.SetRow(row.Children[1], stacked ? 1 : 0);
            }
        };
        _adaptLayout();
    }

    private void AddAction(string path, string key, string name, Func<Task> action, bool primary = false)
    {
        var button = new AppButton { Name = name, Content = LocalizationText.Get(key),
            Classes = { primary ? "primary" : "secondary" }, IsDefault = primary };
        AutomationProperties.SetName(button, LocalizationText.Get(key));
        button.Click += (_, _) => _ = RunAsync(path, action);
        _sessionActions.Children.Add(button);
    }
    private Task StopAsync(string path) { _edits.StopWatching(path); return Task.CompletedTask; }
    private void UpdateActionState()
    {
        foreach (var button in _sessionActions.Children.OfType<AppButton>())
            button.IsEnabled = button.Name == "StopRemoteEdit" || _selectedPath == null || !_busyPaths.Contains(_selectedPath);
    }
    private async Task RunAsync(string path, Func<Task> action)
    {
        if (!_busyPaths.Add(path)) return;
        UpdateActionState();
        try { await action(); }
        catch (Exception ex) { if (!_closed) AppToast.ShowError(this, ex.Message); }
        finally { _busyPaths.Remove(path); if (!_closed) UpdateActionState(); }
    }
    private static TextBlock Value(string text, string size = AppTypography.Body, bool muted = false)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable(muted ? "TextSecondaryBrush" : "TextPrimaryBrush"));
        AppTypography.BindFontSize(label, size); return label;
    }
    private static Grid DetailRow(string key, Control value)
    {
        var row = new Grid { ColumnDefinitions = new("108,*"), ColumnSpacing = 12, RowSpacing = 4 };
        var label = Value(LocalizationText.Get(key), muted: true); label.TextWrapping = TextWrapping.Wrap;
        row.Children.Add(label); Grid.SetColumn(value, 1); row.Children.Add(value); return row;
    }
    private static Border Divider() => new() { Classes = { "dialog-divider" } };
}
