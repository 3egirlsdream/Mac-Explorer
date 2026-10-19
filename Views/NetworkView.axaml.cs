using Avalonia;
using MacExplorer.Controls;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public sealed record NetworkLocationRow(string Name, string Detail, RemoteServerInfo? Server = null, VolumeInfo? Volume = null)
{
    public bool IsMounted => Volume != null;
    public string Address => Server != null ? FormatHost(Server.Host, Server.Port) :
        Uri.TryCreate(Volume?.SourceUrl, UriKind.Absolute, out var uri) ? FormatHost(uri.Host, uri.IsDefaultPort ? 445 : uri.Port) : "—";
    public string Protocol => Uri.TryCreate(Volume?.SourceUrl, UriKind.Absolute, out var uri) ? uri.Scheme.ToUpperInvariant() : "SMB";
    public string Status => LocalizationText.Get(IsMounted ? "已连接" : "未连接");
    public string ActionText => LocalizationText.Get(IsMounted ? "network.open" : "network.connect-short");
    public string LastConnected => Server?.LastConnectedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "—";
    public string LastConnectedDetail => Server?.LastConnectedAt?.ToLocalTime().ToString("F", System.Globalization.CultureInfo.CurrentCulture) ?? "—";
    private static string FormatHost(string host, int port) => port == 445 ? host : $"{(host.Contains(':') ? $"[{host}]" : host)}:{port}";
    internal string Identity => Volume?.Path ?? Server?.Id ?? "";
}

public partial class NetworkView : UserControl
{
    private INetworkDiscoveryService? _discovery;
    private IVolumeMonitorService? _volumes;
    private IServerConnectionCoordinator? _coordinator;
    private IRemoteConnectionService? _connections;
    private IDisposable? _lease;
    private bool _opening;
    private bool _attached;
    private int _refreshQueued;
    private IReadOnlyList<NetworkLocationRow> _locations = [];
    private readonly Dictionary<string, NetworkLocationRow> _byPath = new();
    internal IReadOnlyList<NetworkLocationRow> Locations => _locations;
    internal NetworkLocationRow? SelectedLocation
    {
        get => ServersList.Rows.FirstOrDefault(r => r.IsSelected) is { } selected ? _byPath.GetValueOrDefault(selected.FullPath) : null;
        set
        {
            foreach (var entry in ServersList.Rows) entry.IsSelected = _byPath[entry.FullPath].Identity == value?.Identity;
            ServersList.KeyboardFocusPath = ServersList.Rows.FirstOrDefault(r => r.IsSelected)?.FullPath;
            ServersList.InvalidateVisual();
        }
    }
    private string? _lastDiscoveryError;
    internal ContextMenu? RowMenu { get; private set; }

    public NetworkView()
    {
        InitializeComponent();
        ServersList.DetailIcon = Geometry.Parse(Assets.Icons.Desktop);
        ServersList.DetailTextProvider = entry =>
        {
            var row = _byPath[entry.FullPath];
            return [row.Name, row.Address, row.Protocol, "", row.LastConnected];
        };
        ServersList.AddHandler(KeyDownEvent, OnServerKeyDown, RoutingStrategies.Tunnel);
        ServersList.PointerPressed += OnRowPointerPressed;
        ServersList.ViewportChanged += UpdateRowActions;
        ServersList.SizeChanged += (_, _) => UpdateColumns();
        UpdateColumns();
        AttachedToVisualTree += (_, _) => { _attached = true; UpdateLease(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; Stop(); };
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) UpdateLease(); };
    }

    private void UpdateLease()
    {
        if (!_attached || !IsVisible) { Stop(); return; }
        if (_lease != null || App.Services == null) return;
        _discovery = App.Services.GetService<INetworkDiscoveryService>();
        _volumes = App.Services.GetService<IVolumeMonitorService>();
        _coordinator = App.Services.GetService<IServerConnectionCoordinator>();
        _connections = App.Services.GetService<IRemoteConnectionService>();
        if (_discovery == null) return;
        _discovery.Changed += OnChanged;
        if (_volumes != null) _volumes.VolumesChanged += OnChanged;
        if (_coordinator != null) _coordinator.Changed += OnChanged;
        _lease = _discovery.Acquire();
        if (LocalizationService.Current != null) LocalizationService.Current.LanguageChanged += OnChanged;
        RefreshRows();
    }
    private void Stop()
    {
        if (_discovery != null) _discovery.Changed -= OnChanged;
        if (_volumes != null) _volumes.VolumesChanged -= OnChanged;
        if (_coordinator != null) _coordinator.Changed -= OnChanged;
        if (LocalizationService.Current != null) LocalizationService.Current.LanguageChanged -= OnChanged;
        RowMenu?.Close(); RowMenu = null;
        _lease?.Dispose(); _lease = null;
    }
    private void OnChanged()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_lease != null) RefreshRows();
        });
    }
    private void RefreshRows()
    {
        var selected = SelectedLocation;
        var rows = new List<NetworkLocationRow>();
        var volumes = _volumes?.ExternalVolumes.Where(v => v.IsNetwork).ToArray() ?? [];
        var saved = _connections?.GetSavedServers().Where(s => s.ServiceType == RemoteServiceType.Smb).ToArray() ?? [];
        foreach (var volume in volumes)
        {
            var connection = saved.FirstOrDefault(s => _coordinator?.GetMountedVolume(s)?.Path == volume.Path);
            rows.Add(new(volume.DisplayName, LocalizationText.Get("network.mounted"), connection, volume));
        }
        foreach (var server in saved)
        {
            if (_coordinator?.GetMountedVolume(server) is { } mounted && volumes.Any(v => v.Path == mounted.Path)) continue;
            rows.Add(new(server.DisplayName, server.Host, server));
        }
        foreach (var server in _discovery?.Servers ?? [])
        {
            if (saved.Any(s => string.Equals(s.Host.TrimEnd('.'), server.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))) continue;
            rows.Add(new(server.Name, server.Host, new RemoteServerInfo
                { Id = "discovered:" + server.Id, Name = server.Name, Host = server.Host, Port = server.Port, ServiceType = RemoteServiceType.Smb }));
        }
        SetLocations(rows);
        SelectedLocation = rows.FirstOrDefault(r => r.Identity == selected?.Identity);
        UpdateColumns();
        DiscoveryStatus.Text = LocalizationText.Get(rows.Count == 0 ? "network.empty" : "network.discovery-hint");
        var error = _discovery?.Error;
        if (error != null && error != _lastDiscoveryError && TopLevel.GetTopLevel(this) is Window window)
            AppToast.ShowError(window, error);
        _lastDiscoveryError = error;
    }
    internal Task ConnectServerAsync() => OpenAsync(null);
    internal void SetLocations(IReadOnlyList<NetworkLocationRow> rows)
    {
        if (_locations.Count == rows.Count && _locations.Zip(rows).All(pair =>
            pair.First.Identity == pair.Second.Identity && pair.First.Name == pair.Second.Name
            && pair.First.Address == pair.Second.Address && pair.First.Protocol == pair.Second.Protocol
            && pair.First.LastConnected == pair.Second.LastConnected && pair.First.IsMounted == pair.Second.IsMounted
            && (ReferenceEquals(pair.First.Server, pair.Second.Server)
                || pair.First.Server?.Id.StartsWith("discovered:", StringComparison.Ordinal) == true))) return;
        _locations = rows; _byPath.Clear();
        var entries = rows.Select(row =>
        {
            var path = "network-entry:" + row.Identity;
            _byPath[path] = row;
            return new FileSystemEntry { FullPath = path, Name = row.Name, IsVirtual = true, IsWritable = false };
        }).ToArray();
        ServersList.SetRows(entries);
        UpdateRowActions();
    }
    private void UpdateColumns()
    {
        var nameWidth = Math.Max(180, ServersList.Bounds.Width - 707);
        double[] widths = [nameWidth, 200, 70, 124, 155, 110];
        string[] keys = ["name", "address", "protocol", "status", "last-connected", "actions"];
        var labels = keys.Select(key => LocalizationText.Get("network.column." + key)).ToArray();
        if (ServersList.DetailColumnWidths?.SequenceEqual(widths) == true
            && ColumnHeaders.Children.OfType<TextBlock>().Select(t => t.Text).SequenceEqual(labels)) return;
        ServersList.DetailColumnWidths = widths;
        ColumnHeaders.ColumnDefinitions = new ColumnDefinitions("22," + string.Join(",", widths.Select(w => w.ToString(System.Globalization.CultureInfo.InvariantCulture))) + ",*");
        ColumnHeaders.Children.Clear();
        for (var i = 0; i < keys.Length; i++)
        {
            var header = new TextBlock { Text = labels[i],
                Margin = new Thickness(i == 0 ? 8 : 0, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis };
            header.Classes.Add("file-list-column-caption");
            Grid.SetColumn(header, i + 1); ColumnHeaders.Children.Add(header);
        }
        ServersList.InvalidateVisual(); UpdateRowActions();
    }
    private void UpdateRowActions()
    {
        RowActions.Children.Clear();
        if (ServersList.DetailColumnWidths is not { } widths) return;
        var x = 35 + widths.Take(5).Sum();
        for (var i = 0; i < ServersList.Rows.Count; i++)
        {
            var bounds = ServersList.RowBounds(i);
            if (bounds.Bottom <= 0 || bounds.Top >= ServersList.Bounds.Height) continue;
            var row = _byPath[ServersList.Rows[i].FullPath];
            var statusText = new TextBlock { Text = row.Status, Classes = { "tag-label" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            dot.Bind(Border.BackgroundProperty, ServersList.GetResourceObservable(row.IsMounted ? "SuccessBrush" : "TextMutedBrush"));
            var status = new Border { Classes = { "status-tag" }, IsHitTestVisible = false,
                Child = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Children = { dot, statusText } } };
            Canvas.SetLeft(status, 35 + widths.Take(3).Sum()); Canvas.SetTop(status, bounds.Top + 3);
            RowActions.Children.Add(status);
            var open = new Button { Content = row.ActionText, Tag = row, Height = 24, MinHeight = 24, Width = 70, Padding = new Thickness(8, 0), Classes = { "secondary", "compact" } };
            open.Bind(Button.ForegroundProperty, open.GetResourceObservable(row.IsMounted ? "AccentBrush" : "TextPrimaryBrush"));
            open.Click += OnOpen;
            Avalonia.Automation.AutomationProperties.SetName(open, $"{row.ActionText} — {row.Name}");
            open.PointerEntered += (_, e) => ServersList.UpdatePointerPosition(e.GetPosition(ServersList));
            var more = new Button { Tag = row, Width = 24, MinWidth = 24, Height = 24, MinHeight = 24, Padding = new Thickness(0), Classes = { "secondary", "compact" },
                Content = new PathIcon { Data = Geometry.Parse(Assets.Icons.MoreHorizontal), Width = 12, Height = 12 } };
            more.Click += OnMore;
            ToolTip.SetTip(more, LocalizationText.Get("network.more"));
            Avalonia.Automation.AutomationProperties.SetName(more, $"{LocalizationText.Get("network.more")} — {row.Name}");
            more.PointerEntered += (_, e) => ServersList.UpdatePointerPosition(e.GetPosition(ServersList));
            Canvas.SetLeft(open, x); Canvas.SetTop(open, bounds.Top + 3);
            Canvas.SetLeft(more, x + 76); Canvas.SetTop(more, bounds.Top + 3);
            RowActions.Children.Add(open); RowActions.Children.Add(more);
        }
    }
    private async void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ServersList.EntryAt(e.GetPosition(ServersList)) is not { } entry) return;
        SelectedLocation = _byPath[entry.FullPath];
        ServersList.Focus();
        if (e.GetCurrentPoint(ServersList).Properties.IsRightButtonPressed)
            ShowRowMenu(ServersList, SelectedLocation!);
        else if (e.ClickCount == 2) await OpenAsync(SelectedLocation);
        e.Handled = true;
    }
    private async void OnServerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SelectedLocation is { } selected) { e.Handled = true; await OpenAsync(selected); }
        else if (e.Key is Key.Up or Key.Down or Key.Home or Key.End && ServersList.Rows.Count > 0)
        {
            var current = ServersList.Rows.ToList().FindIndex(r => r.IsSelected);
            var index = e.Key == Key.Home ? 0 : e.Key == Key.End ? ServersList.Rows.Count - 1
                : Math.Clamp(current + (e.Key == Key.Up ? -1 : 1), 0, ServersList.Rows.Count - 1);
            SelectedLocation = _byPath[ServersList.Rows[index].FullPath];
            ServersList.ScrollToEntry(ServersList.Rows[index]); e.Handled = true;
        }
        else if (e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && SelectedLocation is { } row)
        { ShowRowMenu(ServersList, row); e.Handled = true; }
    }
    private async Task OpenAsync(NetworkLocationRow? row)
    {
        if (_opening || DataContext is not FileListViewModel vm || TopLevel.GetTopLevel(this) is not Window window) return;
        _opening = true;
        try
        {
            if (row?.Volume != null) { await vm.NavigateToAsync(row.Volume.Path); return; }
            using var modalBlock = window is MainWindow main ? main.BlockModalParentInteraction() : null;
            var dialog = new RemoteConnectionDialog(row?.Server ?? new RemoteServerInfo { ServiceType = RemoteServiceType.Smb, Port = 445 });
            var result = await dialog.ShowDialog<RemoteServerInfo?>(window);
            if (result != null && dialog.Connected)
            {
                if (dialog.NavigationPath != null) await vm.NavigateToAsync(dialog.NavigationPath);
                else await vm.ConnectToServerAsync(result);
            }
            RefreshRows();
        }
        catch (Exception ex) { AppToast.ShowError(window, LocalizationText.Get("network.connect-error", ex.Message)); }
        finally { _opening = false; }
    }
    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: NetworkLocationRow row }) await OpenAsync(row);
    }
    private void OnMore(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: NetworkLocationRow row } button) ShowRowMenu(button, row);
    }
    private void ShowRowMenu(Control anchor, NetworkLocationRow row)
    {
        RowMenu?.Close();
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight, MinWidth = 160 };
        RowMenu = menu;
        var open = new MenuItem { Header = row.IsMounted ? row.ActionText : LocalizationText.Get("network.edit") };
        open.Click += async (_, _) => await OpenAsync(row);
        menu.Items.Add(open);
        if (row.Server != null && row.IsMounted)
        {
            var edit = new MenuItem { Header = LocalizationText.Get("network.edit") };
            edit.Click += async (_, _) => await OpenAsync(row with { Volume = null });
            menu.Items.Add(edit);
        }
        if (row.Volume is { } volume && DataContext is FileListViewModel vm)
        {
            var eject = new MenuItem { Header = LocalizationText.Get("network.disconnect") };
            eject.Click += async (_, _) => await vm.EjectVolumeAsync(volume);
            menu.Items.Add(eject);
        }
        menu.Open(anchor);
    }
}
