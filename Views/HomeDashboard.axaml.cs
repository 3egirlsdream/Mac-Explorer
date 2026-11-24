using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Icons = MacExplorer.Assets.Icons;

namespace MacExplorer.Views;

public partial class HomeDashboard : UserControl
{
    private const string AllTagKey = "__all__";

    private FileListViewModel? _navigation;
    private IFileTagService? _tags;
    private HomeWorkspaceService? _workspace;
    private ISettingsService? _settings;
    private IDiskInformationService? _diskInfo;
    private IVolumeMonitorService? _volumes;
    private IPinnedFolderService? _pinned;
    private IGitStatusService? _git;
    private HomeItemActions? _actions;
    private ILocalizationService? _language;

    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _disksCancellation;
    private CancellationTokenSource? _workspacesCancellation;
    private CancellationTokenSource? _inboxCancellation;
    private CancellationTokenSource? _collectionsCancellation;

    private HomeSectionPreferences _prefs = new();
    private HomeInboxFilter _inboxFilter = HomeInboxFilter.All;
    private string _activeTagKey = AllTagKey;
    private bool _attached;

    private IReadOnlyList<DiskInfo> _currentDisks = [];
    private TabGroupPreset[] _currentPresets = [];
    private IReadOnlyList<FileTag> _loadedTags = [];
    private Dictionary<string, IReadOnlyList<string>> _tagPaths = new(StringComparer.Ordinal);
    private Dictionary<string, List<FileTag>> _pathTags = new(StringComparer.Ordinal);
    private Dictionary<string, FileSystemEntry> _entryCache = new(StringComparer.Ordinal);
    private int _lastStorageColumns = -1;
    private bool _lastStorageCompact;
    private int _lastPresetColumns = -1;
    private bool? _lastBentoTwoColumn;

    public HomeDashboard()
    {
        InitializeComponent();
        SetupCollectionsDrop();
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
        _settings = services.GetService<ISettingsService>();
        _diskInfo = services.GetService<IDiskInformationService>();
        _volumes = services.GetService<IVolumeMonitorService>();
        _pinned = services.GetService<IPinnedFolderService>();
        _git = services.GetService<IGitStatusService>();
        var files = services.GetService<IFileService>();
        var runner = services.GetService<HomeScriptRunner>();
        if (_tags == null || _workspace == null || files == null || runner == null)
        {
            StatusMessage.Text = LocalizationText.Get("工作台服务尚未初始化。");
            StatusMessage.IsVisible = true;
            return;
        }
        _actions = new(this, _navigation, files, _tags, _workspace, runner, SetStatus);
        _language = LocalizationService.Current;
        if (_language != null) _language.LanguageChanged += OnLanguageChanged;
        _navigation.PropertyChanged += OnNavigationChanged;
        _tags.TagsChanged += OnTagsChanged;
        _workspace.HistoryChanged += OnHistoryChanged;
        _workspace.PreferencesChanged += OnPreferencesChanged;
        if (_settings != null) _settings.SettingChanged += OnSettingChanged;
        if (_volumes != null) _volumes.VolumesChanged += OnVolumesChanged;
        if (_pinned != null) _pinned.Changed += OnPinnedChanged;

        _prefs = _workspace.GetSectionPreferences();
        ApplySectionPreferences();
        if (_navigation.IsHomePage) _ = RefreshAllAsync();
    }

    private void Disconnect()
    {
        if (_language != null) _language.LanguageChanged -= OnLanguageChanged;
        _language = null;
        if (_navigation != null) _navigation.PropertyChanged -= OnNavigationChanged;
        if (_tags != null) _tags.TagsChanged -= OnTagsChanged;
        if (_workspace != null)
        {
            _workspace.HistoryChanged -= OnHistoryChanged;
            _workspace.PreferencesChanged -= OnPreferencesChanged;
        }
        if (_settings != null) _settings.SettingChanged -= OnSettingChanged;
        if (_volumes != null) _volumes.VolumesChanged -= OnVolumesChanged;
        if (_pinned != null) _pinned.Changed -= OnPinnedChanged;

        CancelLoads();
        StorageGrid.Children.Clear();
        PresetStrip.Children.Clear();
        FrequentFoldersList.Children.Clear();
        InboxList.Children.Clear();
        TagPillsPanel.Children.Clear();
        CollectionsListPanel.Children.Clear();
        CollectionsGridPanel.Children.Clear();
        _currentDisks = [];
        _currentPresets = [];
        _loadedTags = [];
        _tagPaths.Clear();
        _pathTags.Clear();
        _entryCache.Clear();
        _actions?.Dispose();
        _navigation = null;
        _tags = null;
        _workspace = null;
        _settings = null;
        _diskInfo = null;
        _volumes = null;
        _pinned = null;
        _git = null;
        _actions = null;
    }

    private void CancelLoads()
    {
        Cancel(ref _refreshCancellation);
        Cancel(ref _disksCancellation);
        Cancel(ref _workspacesCancellation);
        Cancel(ref _inboxCancellation);
        Cancel(ref _collectionsCancellation);
    }

    private static void Cancel(ref CancellationTokenSource? cts)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = null;
    }

    private void OnLanguageChanged()
        => Dispatcher.UIThread.Post(() => { if (_attached && _navigation?.IsHomePage == true) _ = RefreshAllAsync(); });

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileListViewModel.IsHomePage)) return;
        if (_navigation?.IsHomePage == true) _ = RefreshAllAsync();
        else CancelLoads();
    }

    private void OnTagsChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => { if (_attached && _navigation?.IsHomePage == true) _ = RefreshCollectionsAsync(); });

    private void OnHistoryChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (!_attached || _navigation?.IsHomePage != true) return;
            _ = RefreshWorkspacesAsync();
            _ = RefreshInboxAsync();
        });

    private void OnPreferencesChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (!_attached || _workspace == null) return;
            var previous = _prefs;
            _prefs = _workspace.GetSectionPreferences();
            ApplySectionPreferences();
            if (_navigation?.IsHomePage != true) return;
            if (_prefs.ShowStorage && !previous.ShowStorage) _ = RefreshStorageAsync();
            if (_prefs.ShowWorkspaces && !previous.ShowWorkspaces) _ = RefreshWorkspacesAsync();
            if (_prefs.ShowInbox && !previous.ShowInbox) _ = RefreshInboxAsync();
            if (_prefs.ShowCollections && (!previous.ShowCollections || _prefs.CollectionViewMode != previous.CollectionViewMode))
                _ = RefreshCollectionsAsync();
        });

    private void OnSettingChanged(string key)
    {
        if (key != TabGroupPresets.SettingKey) return;
        Dispatcher.UIThread.Post(() => { if (_attached && _navigation?.IsHomePage == true) _ = RefreshWorkspacesAsync(); });
    }

    private void OnVolumesChanged()
        => Dispatcher.UIThread.Post(() => { if (_attached && _navigation?.IsHomePage == true) _ = RefreshStorageAsync(); });

    private void OnPinnedChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => { if (_attached && _navigation?.IsHomePage == true) _ = RefreshWorkspacesAsync(); });

    private void SetStatus(string text)
    {
        StatusMessage.Text = text;
        StatusMessage.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void ApplySectionPreferences()
    {
        var isGrid = string.Equals(_prefs.CollectionViewMode, HomeCollectionViewModes.Grid, StringComparison.OrdinalIgnoreCase);
        SetActiveClass(CollectionsListButton, !isGrid);
        SetActiveClass(CollectionsGridButton, isGrid);
        CollectionsListPanel.IsVisible = !isGrid;
        CollectionsGridPanel.IsVisible = isGrid;

        StorageSection.IsVisible = _prefs.ShowStorage && _currentDisks.Count > 0;
        var showPresetsStrip = _prefs.ShowWorkspaces && _currentPresets.Length > 0;
        WorkgroupsStripSection.IsVisible = showPresetsStrip;
        WorkgroupsActionButton.IsVisible = !showPresetsStrip;
        WorkspacesSection.IsVisible = _prefs.ShowWorkspaces;
        InboxSection.IsVisible = _prefs.ShowInbox;
        MiddleBentoGrid.IsVisible = _prefs.ShowWorkspaces || _prefs.ShowInbox;
        CollectionsSection.IsVisible = _prefs.ShowCollections;
        AllHiddenHint.IsVisible = !_prefs.ShowStorage && !_prefs.ShowWorkspaces && !_prefs.ShowInbox && !_prefs.ShowCollections;

        UpdateResponsiveLayout(DashboardRoot.Bounds.Width);
    }

    private void OnDashboardSizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        if (width <= 0) width = 900;

        // Storage: single disk renders as a sleek horizontal row when wide; multiple disks use 3/2/1 grid
        if (_currentDisks.Count > 0)
        {
            var compactSingle = _currentDisks.Count == 1 && width >= 540;
            var targetStorageCols = width >= 640 ? 3 : width >= 430 ? 2 : 1;
            if (targetStorageCols != _lastStorageColumns || compactSingle != _lastStorageCompact)
            {
                _lastStorageColumns = targetStorageCols;
                _lastStorageCompact = compactSingle;
                RenderStorageCards(_currentDisks, targetStorageCols, compactSingle);
            }
        }

        // Workgroup strip: 3 presets -> 3 equal columns in 1 row (no orphan slot!)
        if (_currentPresets.Length > 0)
        {
            var maxCols = width >= 680 ? 3 : width >= 420 ? 2 : 1;
            var targetPresetCols = Math.Min(maxCols, _currentPresets.Length);
            if (targetPresetCols != _lastPresetColumns)
            {
                _lastPresetColumns = targetPresetCols;
                RenderPresets(_currentPresets, targetPresetCols);
            }
        }

        // Equal-height middle bento: 2 columns when both visible and width >= 660
        var twoCol = _prefs.ShowWorkspaces && _prefs.ShowInbox && width >= 660;
        if (twoCol != _lastBentoTwoColumn || MiddleBentoGrid.ColumnDefinitions.Count == 0)
        {
            _lastBentoTwoColumn = twoCol;
            MiddleBentoGrid.ColumnDefinitions.Clear();
            MiddleBentoGrid.RowDefinitions.Clear();
            if (twoCol)
            {
                MiddleBentoGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                MiddleBentoGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                MiddleBentoGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetRow(WorkspacesSection, 0);
                Grid.SetColumn(WorkspacesSection, 0);
                Grid.SetRow(InboxSection, 0);
                Grid.SetColumn(InboxSection, 1);
            }
            else
            {
                MiddleBentoGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                MiddleBentoGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                MiddleBentoGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetRow(WorkspacesSection, 0);
                Grid.SetColumn(WorkspacesSection, 0);
                Grid.SetRow(InboxSection, _prefs.ShowWorkspaces ? 1 : 0);
                Grid.SetColumn(InboxSection, 0);
            }
        }
    }

    private async Task RefreshAllAsync()
    {
        if (!_attached || _navigation?.IsHomePage != true) return;
        Cancel(ref _refreshCancellation);
        _refreshCancellation = new CancellationTokenSource();
        var token = _refreshCancellation.Token;
        SetStatus("");
        try
        {
            await Task.Delay(50, token);
            var tasks = new List<Task>(4);
            if (_prefs.ShowStorage) tasks.Add(RefreshStorageAsync());
            if (_prefs.ShowWorkspaces) tasks.Add(RefreshWorkspacesAsync());
            if (_prefs.ShowInbox) tasks.Add(RefreshInboxAsync());
            if (_prefs.ShowCollections) tasks.Add(RefreshCollectionsAsync());
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
    }

    /* ============================================================
       MODULE 1: Storage & Mounted Volumes
       ============================================================ */
    private async Task RefreshStorageAsync()
    {
        if (!_attached || _navigation?.IsHomePage != true || _diskInfo == null || !_prefs.ShowStorage)
        {
            StorageSection.IsVisible = false;
            return;
        }
        Cancel(ref _disksCancellation);
        _disksCancellation = new CancellationTokenSource();
        var token = _disksCancellation.Token;
        try
        {
            var disks = await Task.Run(() => _diskInfo.GetMountedDisksAsync(token), token);
            token.ThrowIfCancellationRequested();
            if (!_attached || _navigation?.IsHomePage != true) return;
            _currentDisks = disks.Where(d => !string.IsNullOrWhiteSpace(d.Path)).Take(6).ToArray();
            StorageSection.IsVisible = _prefs.ShowStorage && _currentDisks.Count > 0;
            if (StorageSection.IsVisible)
            {
                var width = DashboardRoot.Bounds.Width > 0 ? DashboardRoot.Bounds.Width : 900;
                _lastStorageCompact = _currentDisks.Count == 1 && width >= 540;
                _lastStorageColumns = width >= 640 ? 3 : width >= 430 ? 2 : 1;
                RenderStorageCards(_currentDisks, _lastStorageColumns, _lastStorageCompact);
            }
        }
        catch (OperationCanceledException) { }
        catch { StorageSection.IsVisible = false; }
    }

    private void RenderStorageCards(IReadOnlyList<DiskInfo> disks, int columns, bool compactSingle)
    {
        StorageGrid.Children.Clear();
        StorageGrid.ColumnDefinitions.Clear();
        StorageGrid.RowDefinitions.Clear();

        if (compactSingle && disks.Count == 1)
        {
            StorageGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            StorageGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            StorageGrid.Children.Add(CreateSingleRowDiskCard(disks[0]));
            return;
        }

        var actualCols = Math.Max(1, Math.Min(columns, disks.Count));
        for (var c = 0; c < actualCols; c++)
            StorageGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        var rows = (int)Math.Ceiling(disks.Count / (double)actualCols);
        for (var r = 0; r < rows; r++)
            StorageGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var i = 0; i < disks.Count; i++)
        {
            var card = CreateDiskCard(disks[i]);
            Grid.SetRow(card, i / actualCols);
            Grid.SetColumn(card, i % actualCols);
            StorageGrid.Children.Add(card);
        }
    }

    private Control CreateSingleRowDiskCard(DiskInfo disk)
    {
        var button = new AppButton { Classes = { "ghost", "home-disk-card" } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 14 };

        var iconBox = CreateDiskIconBox(disk);
        row.Children.Add(iconBox);

        var titleStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, MinWidth = 110 };
        titleStack.Children.Add(AppTypography.BindFontSize(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(disk.Name) ? disk.Path : disk.Name,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        }, AppTypography.Body));
        titleStack.Children.Add(new TextBlock
        {
            Text = FormatDiskSubtitle(disk),
            Classes = { "home-muted" },
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(titleStack, 1);
        row.Children.Add(titleStack);

        if (disk.TotalBytes is > 0)
        {
            var meterStack = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            meterStack.Children.Add(CreateDiskMeter(disk.UsedPercent));
            var freeBytes = disk.AvailableBytes ?? disk.FreeBytes;
            var usedBytes = disk.FreeBytes.HasValue ? Math.Max(0, disk.TotalBytes.Value - disk.FreeBytes.Value) : (long?)null;
            var metaRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            metaRow.Children.Add(new TextBlock
            {
                Text = LocalizationText.Get("home.disk.free", DiskManagementViewModel.FormatBytes(freeBytes)),
                Classes = { "home-muted" }
            });
            var usedSummary = new TextBlock
            {
                Text = usedBytes.HasValue
                    ? LocalizationText.Get("home.disk.used_of", DiskManagementViewModel.FormatBytes(usedBytes), DiskManagementViewModel.FormatBytes(disk.TotalBytes))
                    : DiskManagementViewModel.FormatBytes(disk.TotalBytes),
                Classes = { "home-muted" },
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(usedSummary, 1);
            metaRow.Children.Add(usedSummary);
            meterStack.Children.Add(metaRow);
            Grid.SetColumn(meterStack, 2);
            row.Children.Add(meterStack);
        }

        if (disk.CanEject && _volumes != null)
        {
            var ejectBtn = CreateEjectButton(disk);
            Grid.SetColumn(ejectBtn, 3);
            row.Children.Add(ejectBtn);
        }

        button.Content = row;
        ToolTip.SetTip(button, disk.Path);
        AutomationProperties.SetName(button, disk.Name);
        button.Click += async (_, _) =>
        {
            if (_navigation != null && _actions != null)
                await _actions.GuardAsync(() => _navigation.NavigateToAsync(disk.Path));
        };
        return button;
    }

    private Control CreateDiskCard(DiskInfo disk)
    {
        var button = new AppButton { Classes = { "ghost", "home-disk-card" } };
        var body = new StackPanel { Spacing = 8 };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 9 };
        top.Children.Add(CreateDiskIconBox(disk));

        var titleStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(AppTypography.BindFontSize(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(disk.Name) ? disk.Path : disk.Name,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        }, AppTypography.Body));
        titleStack.Children.Add(new TextBlock
        {
            Text = FormatDiskSubtitle(disk),
            Classes = { "home-muted" },
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(titleStack, 1);
        top.Children.Add(titleStack);

        if (disk.CanEject && _volumes != null)
        {
            var ejectBtn = CreateEjectButton(disk);
            Grid.SetColumn(ejectBtn, 2);
            top.Children.Add(ejectBtn);
        }
        body.Children.Add(top);

        if (disk.TotalBytes is > 0)
        {
            body.Children.Add(CreateDiskMeter(disk.UsedPercent));
            var freeBytes = disk.AvailableBytes ?? disk.FreeBytes;
            var usedBytes = disk.FreeBytes.HasValue ? Math.Max(0, disk.TotalBytes.Value - disk.FreeBytes.Value) : (long?)null;
            var metaRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            metaRow.Children.Add(new TextBlock
            {
                Text = LocalizationText.Get("home.disk.free", DiskManagementViewModel.FormatBytes(freeBytes)),
                Classes = { "home-muted" }
            });
            var usedSummary = new TextBlock
            {
                Text = usedBytes.HasValue
                    ? LocalizationText.Get("home.disk.used_of", DiskManagementViewModel.FormatBytes(usedBytes), DiskManagementViewModel.FormatBytes(disk.TotalBytes))
                    : DiskManagementViewModel.FormatBytes(disk.TotalBytes),
                Classes = { "home-muted" },
                HorizontalAlignment = HorizontalAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(usedSummary, 1);
            metaRow.Children.Add(usedSummary);
            body.Children.Add(metaRow);
        }

        button.Content = body;
        ToolTip.SetTip(button, disk.Path);
        AutomationProperties.SetName(button, disk.Name);
        button.Click += async (_, _) =>
        {
            if (_navigation != null && _actions != null)
                await _actions.GuardAsync(() => _navigation.NavigateToAsync(disk.Path));
        };
        return button;
    }

    private static Border CreateDiskIconBox(DiskInfo disk) => new()
    {
        Classes = { "home-disk-icon-box" },
        Child = new PathIcon
        {
            Data = Geometry.Parse(IsNetworkDisk(disk) ? Icons.Network : Icons.ExternalDrive),
            Width = 15,
            Height = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Border CreateDiskMeter(double usedPercent)
    {
        var track = new Border { Classes = { "home-disk-meter-track" } };
        var barGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{Math.Max(0.5, usedPercent):F1}*,{Math.Max(0.1, 100 - usedPercent):F1}*")
        };
        var fill = new Border { Classes = { "home-disk-meter-fill" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (usedPercent >= 75) fill.Classes.Add("warn");
        barGrid.Children.Add(fill);
        track.Child = barGrid;
        return track;
    }

    private AppButton CreateEjectButton(DiskInfo disk)
    {
        var ejectBtn = new AppButton
        {
            Classes = { "ghost", "home-disk-eject" },
            Content = new PathIcon { Data = Geometry.Parse(Icons.Eject), Width = 13, Height = 13 }
        };
        ToolTip.SetTip(ejectBtn, LocalizationText.Get("home.disk.eject_tooltip", disk.Name));
        AutomationProperties.SetName(ejectBtn, LocalizationText.Get("home.disk.eject_tooltip", disk.Name));
        ejectBtn.Click += async (_, e) =>
        {
            e.Handled = true;
            await EjectDiskAsync(disk);
        };
        return ejectBtn;
    }

    private static bool IsNetworkDisk(DiskInfo disk)
    {
        var fmt = disk.Format?.ToLowerInvariant() ?? "";
        var conn = disk.Connection?.ToLowerInvariant() ?? "";
        return fmt.Contains("smb") || fmt.Contains("webdav") || fmt.Contains("nfs") || fmt.Contains("afp") || fmt.Contains("cifs")
            || conn.Contains("network") || conn.Contains("smb") || conn.Contains("webdav");
    }

    private static string FormatDiskSubtitle(DiskInfo disk)
    {
        var parts = new List<string>(2);
        if (disk.IsDiskImage) parts.Add(LocalizationText.Get("home.disk.kind_image"));
        else if (disk.IsExternal) parts.Add(LocalizationText.Get("home.disk.kind_external"));
        else parts.Add(LocalizationText.Get("home.disk.kind_internal"));
        if (!string.IsNullOrWhiteSpace(disk.Format)) parts.Add(disk.Format.ToUpperInvariant());
        return string.Join(" · ", parts);
    }

    private async Task EjectDiskAsync(DiskInfo disk)
    {
        if (_volumes == null) return;
        try
        {
            var result = await _volumes.EjectVolumeWithResultAsync(disk.Path);
            if (!result.Success)
                SetStatus(result.ErrorMessage ?? LocalizationText.Get("home.disk.eject_failed", disk.Name));
            else
                _ = RefreshStorageAsync();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    /* ============================================================
       MODULE 2: Workgroup Strip & Frequent Project Folders
       ============================================================ */
    private async Task RefreshWorkspacesAsync()
    {
        if (!_attached || _navigation?.IsHomePage != true || _workspace == null || _actions == null || !_prefs.ShowWorkspaces)
            return;
        Cancel(ref _workspacesCancellation);
        _workspacesCancellation = new CancellationTokenSource();
        var token = _workspacesCancellation.Token;
        var actions = _actions;
        var homeDir = _navigation.HomeDirectory;
        try
        {
            var presets = _settings != null ? TabGroupPresets.Read(_settings) : [];
            HashSet<string>? pinnedPaths = null;
            if (_pinned != null)
            {
                var pins = await _pinned.GetAllAsync();
                pinnedPaths = new HashSet<string>(pins.Select(p => p.FolderPath), StringComparer.Ordinal);
            }
            var frequent = await _workspace.GetFrequentFoldersAsync(homeDir, pinnedPaths, count: 5, token);
            var entries = frequent.Count > 0
                ? await actions.LoadEntriesAsync(frequent.Select(f => f.Path).ToArray(), token)
                : [];
            token.ThrowIfCancellationRequested();
            if (!_attached || _navigation?.IsHomePage != true) return;

            _currentPresets = presets.Take(6).ToArray();
            var showStrip = _prefs.ShowWorkspaces && _currentPresets.Length > 0;
            WorkgroupsStripSection.IsVisible = showStrip;
            WorkgroupsActionButton.IsVisible = !showStrip;
            if (showStrip)
            {
                var width = DashboardRoot.Bounds.Width > 0 ? DashboardRoot.Bounds.Width : 900;
                var maxCols = width >= 680 ? 3 : width >= 420 ? 2 : 1;
                _lastPresetColumns = Math.Min(maxCols, _currentPresets.Length);
                RenderPresets(_currentPresets, _lastPresetColumns);
            }
            else
            {
                PresetStrip.Children.Clear();
            }

            FrequentFoldersList.Children.Clear();
            var timestampByPath = frequent.ToDictionary(x => x.Path, x => x.LastUsedUtc, StringComparer.Ordinal);
            var gitTargets = new List<(string Path, Border Badge, TextBlock Label)>();
            foreach (var entry in entries)
            {
                timestampByPath.TryGetValue(entry.FullPath, out var lastUsed);
                var (row, gitBadge, gitLabel) = CreateFolderRow(entry, homeDir, lastUsed, actions);
                FrequentFoldersList.Children.Add(row);
                if (gitBadge != null && gitLabel != null)
                    gitTargets.Add((entry.FullPath, gitBadge, gitLabel));
            }

            FrequentFoldersList.IsVisible = entries.Count > 0;
            WorkspacesEmpty.IsVisible = entries.Count == 0;

            if (gitTargets.Count > 0 && _git != null)
                _ = PopulateGitBranchesAsync(gitTargets, token);
        }
        catch (OperationCanceledException) { }
    }

    private void RenderPresets(TabGroupPreset[] presets, int columns)
    {
        PresetStrip.Children.Clear();
        PresetStrip.ColumnDefinitions.Clear();
        PresetStrip.RowDefinitions.Clear();
        if (presets.Length == 0) return;

        var cols = Math.Max(1, Math.Min(columns, presets.Length));
        for (var c = 0; c < cols; c++)
            PresetStrip.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        var rows = (int)Math.Ceiling(presets.Length / (double)cols);
        for (var r = 0; r < rows; r++)
            PresetStrip.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var i = 0; i < presets.Length; i++)
        {
            var preset = presets[i];
            var btn = new AppButton { Classes = { "ghost", "home-preset-card" } };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
            grid.Children.Add(new Border
            {
                Classes = { "home-preset-icon-box" },
                Child = new PathIcon
                {
                    Data = Geometry.Parse(Icons.SquareMultiple),
                    Width = 14,
                    Height = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            var textStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(AppTypography.BindFontSize(new TextBlock
            {
                Text = preset.Name,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            }, AppTypography.Label));
            textStack.Children.Add(new TextBlock
            {
                Text = LocalizationText.Get("home.preset.tab_count", preset.Session.Paths.Length),
                Classes = { "home-muted" },
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);
            btn.Content = grid;
            ToolTip.SetTip(btn, string.Join("\n", preset.Session.Paths.Take(6)));
            btn.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this) is MainWindow window)
                    await window.OpenGroupPresetAsync(preset);
            };
            Grid.SetRow(btn, i / cols);
            Grid.SetColumn(btn, i % cols);
            PresetStrip.Children.Add(btn);
        }
    }

    private (Control Row, Border? GitBadge, TextBlock? GitLabel) CreateFolderRow(
        FileSystemEntry entry, string homeDir, DateTime lastUsedUtc, HomeItemActions actions)
    {
        var row = new Border { Classes = { "home-row-container" }, ContextMenu = actions.CreateItemContextMenu(entry) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };

        var hit = new AppButton { Classes = { "ghost", "home-row-hit" } };
        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*"), ColumnSpacing = 10 };
        main.Children.Add(actions.CreateFileIcon(entry, 24));

        var labels = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var name = AppTypography.BindFontSize(new FileNameText
        {
            Text = InterfaceLocationNames.Get(entry.FullPath, homeDir) ?? entry.DisplayName,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center
        }, AppTypography.Body);
        titleRow.Children.Add(name);

        var gitLabel = AppTypography.BindFontSize(new TextBlock
        {
            FontFamily = Application.Current?.TryFindResource("FontFamilyMono", out var mono) == true && mono is FontFamily ff ? ff : FontFamily.Default,
            Classes = { "home-muted" }
        }, AppTypography.Meta);
        var gitBadge = new Border { Classes = { "home-git-pill" }, IsVisible = false, Child = gitLabel };
        titleRow.Children.Add(gitBadge);
        labels.Children.Add(titleRow);

        labels.Children.Add(new TextBlock
        {
            Text = HomePathFormatting.FormatParentPath(entry.FullPath, homeDir),
            Classes = { "home-row-path" }
        });
        Grid.SetColumn(labels, 1);
        main.Children.Add(labels);
        hit.Content = main;
        ToolTip.SetTip(hit, entry.FullPath);
        AutomationProperties.SetName(hit, entry.DisplayName);
        hit.Click += async (_, _) => await actions.GuardAsync(() => actions.OpenEntryAsync(entry));
        grid.Children.Add(hit);

        // Flush right-aligned timestamp that swaps cleanly into quick action buttons on hover
        var rightOverlay = CreateMetaHoverSlot(
            lastUsedUtc != default ? HomeItemActions.FormatAddedTime(lastUsedUtc, DateTime.UtcNow) : null,
            CreateRowIconAction(Icons.Plus, LocalizationText.Get("home.action.open_in_new_tab"),
                () => actions.GuardAsync(() => actions.OpenInNewTabAsync(entry.FullPath))),
            CreateRowIconAction(Icons.Location, LocalizationText.Get("home.action.locate"),
                () => actions.GuardAsync(() => actions.RevealEntryAsync(entry))));
        Grid.SetColumn(rightOverlay, 1);
        grid.Children.Add(rightOverlay);
        row.Child = grid;
        return (row, gitBadge, gitLabel);
    }

    private async Task PopulateGitBranchesAsync(List<(string Path, Border Badge, TextBlock Label)> targets, CancellationToken token)
    {
        var git = _git;
        if (git == null) return;
        foreach (var (path, badge, label) in targets)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                var status = await git.GetRepoStatusAsync(path);
                if (token.IsCancellationRequested || !_attached) return;
                var branch = status != null && !string.IsNullOrWhiteSpace(status.RepoRoot)
                    ? await Task.Run(() => ReadGitBranchName(status.RepoRoot), token)
                    : null;
                if (!string.IsNullOrWhiteSpace(branch))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested || !_attached) return;
                        label.Text = branch;
                        badge.IsVisible = true;
                    });
                }
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }

    private static string? ReadGitBranchName(string repoRoot)
    {
        try
        {
            var dotGit = System.IO.Path.Combine(repoRoot, ".git");
            string? gitDir = null;
            if (Directory.Exists(dotGit))
                gitDir = dotGit;
            else if (File.Exists(dotGit))
            {
                var pointer = File.ReadAllText(dotGit).Trim();
                const string prefix = "gitdir:";
                if (pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var raw = pointer[prefix.Length..].Trim();
                    gitDir = System.IO.Path.IsPathRooted(raw) ? raw : System.IO.Path.GetFullPath(System.IO.Path.Combine(repoRoot, raw));
                }
            }
            if (string.IsNullOrEmpty(gitDir)) return null;
            var headPath = System.IO.Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath)) return null;
            var head = File.ReadAllText(headPath).Trim();
            const string refPrefix = "ref: refs/heads/";
            if (head.StartsWith(refPrefix, StringComparison.Ordinal))
                return head[refPrefix.Length..];
            return head.Length >= 7 ? head[..7] : null;
        }
        catch
        {
            return null;
        }
    }

    /* ============================================================
       MODULE 3: Recent / Inbox Files
       ============================================================ */
    private async Task RefreshInboxAsync()
    {
        if (!_attached || _navigation?.IsHomePage != true || _workspace == null || _actions == null || !_prefs.ShowInbox)
            return;
        Cancel(ref _inboxCancellation);
        _inboxCancellation = new CancellationTokenSource();
        var token = _inboxCancellation.Token;
        var actions = _actions;
        var homeDir = _navigation.HomeDirectory;
        try
        {
            var items = await _workspace.GetInboxFilesAsync(homeDir, _inboxFilter, count: 5, token);
            var entries = items.Count > 0
                ? await actions.LoadEntriesAsync(items.Select(i => i.Path).ToArray(), token)
                : [];
            token.ThrowIfCancellationRequested();
            if (!_attached || _navigation?.IsHomePage != true) return;

            InboxList.Children.Clear();
            var timeByPath = items.ToDictionary(i => i.Path, i => i.TimestampUtc, StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                timeByPath.TryGetValue(entry.FullPath, out var ts);
                InboxList.Children.Add(CreateInboxRow(entry, homeDir, ts, actions));
            }
            InboxList.IsVisible = entries.Count > 0;
            InboxEmpty.IsVisible = entries.Count == 0;
        }
        catch (OperationCanceledException) { }
    }

    private Control CreateInboxRow(FileSystemEntry entry, string homeDir, DateTime timestampUtc, HomeItemActions actions)
    {
        var row = new Border { Classes = { "home-row-container" }, ContextMenu = actions.CreateItemContextMenu(entry) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };

        var hit = new AppButton { Classes = { "ghost", "home-row-hit" } };
        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*"), ColumnSpacing = 10 };
        main.Children.Add(actions.CreateFileIcon(entry, 24));

        var labels = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(AppTypography.BindFontSize(new FileNameText
        {
            Text = entry.DisplayName,
            FontWeight = FontWeight.Medium
        }, AppTypography.Body));
        labels.Children.Add(new TextBlock
        {
            Text = HomePathFormatting.FormatParentPath(entry.FullPath, homeDir),
            Classes = { "home-row-path" }
        });
        Grid.SetColumn(labels, 1);
        main.Children.Add(labels);
        hit.Content = main;
        ToolTip.SetTip(hit, entry.FullPath);
        AutomationProperties.SetName(hit, entry.DisplayName);
        hit.Click += async (_, _) => await actions.GuardAsync(() => actions.OpenEntryAsync(entry));
        grid.Children.Add(hit);

        var rightOverlay = CreateMetaHoverSlot(
            timestampUtc != default ? HomeItemActions.FormatAddedTime(timestampUtc, DateTime.UtcNow) : null,
            CreateRowIconAction(Icons.Eye, LocalizationText.Get("home.action.preview"),
                () => actions.GuardAsync(() => actions.PreviewEntryAsync(entry))),
            CreateRowIconAction(Icons.Location, LocalizationText.Get("home.action.locate"),
                () => actions.GuardAsync(() => actions.RevealEntryAsync(entry))));
        Grid.SetColumn(rightOverlay, 1);
        grid.Children.Add(rightOverlay);
        row.Child = grid;
        return row;
    }

    /* ============================================================
       MODULE 4: Unified Collections & Tags
       ============================================================ */
    private async Task RefreshCollectionsAsync()
    {
        if (!_attached || _navigation?.IsHomePage != true || _tags == null || _actions == null || !_prefs.ShowCollections)
            return;
        Cancel(ref _collectionsCancellation);
        _collectionsCancellation = new CancellationTokenSource();
        var token = _collectionsCancellation.Token;
        var tagsService = _tags;
        var actions = _actions;
        try
        {
            await Task.Delay(80, token);
            var tags = await Task.Run(() => tagsService.GetSidebarTagsAsync(token), token);
            token.ThrowIfCancellationRequested();

            var tagPaths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var pathTags = new Dictionary<string, List<FileTag>>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                var paths = await Task.Run(async () => (await tagsService.FindFilePathsAsync(tag, token))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(System.IO.Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(p => p, StringComparer.Ordinal)
                    .ToArray(), token);
                token.ThrowIfCancellationRequested();
                tagPaths[TagKey(tag)] = paths;
                foreach (var path in paths)
                {
                    if (!pathTags.TryGetValue(path, out var list))
                        pathTags[path] = list = [];
                    list.Add(tag);
                }
            }

            var allUniquePaths = pathTags.Keys
                .OrderBy(System.IO.Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(p => p, StringComparer.Ordinal)
                .ToArray();
            var loadedEntries = allUniquePaths.Length > 0
                ? await actions.LoadEntriesAsync(allUniquePaths, token)
                : [];
            token.ThrowIfCancellationRequested();
            if (!_attached || _navigation?.IsHomePage != true) return;

            _loadedTags = tags;
            _tagPaths = tagPaths;
            _pathTags = pathTags;
            _entryCache = loadedEntries.ToDictionary(e => e.FullPath, StringComparer.Ordinal);

            if (_activeTagKey != AllTagKey && !_tagPaths.ContainsKey(_activeTagKey))
                _activeTagKey = AllTagKey;

            RenderTagPills();
            RenderCollectionsBody();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                SetStatus(LocalizationText.Get("读取收藏夹失败：") + ex.Message);
        }
    }

    private void RenderTagPills()
    {
        TagPillsPanel.Children.Clear();
        if (_actions == null) return;

        TagPillsPanel.Children.Add(CreateTagPill(
            key: AllTagKey,
            label: LocalizationText.Get("home.collections.all"),
            count: _pathTags.Count,
            colorHex: null,
            tag: null));

        foreach (var tag in _loadedTags)
        {
            var key = TagKey(tag);
            var count = _tagPaths.TryGetValue(key, out var list) ? list.Count : 0;
            if (count == 0 && !tag.IsCustom) continue;
            TagPillsPanel.Children.Add(CreateTagPill(key, tag.DisplayName, count, tag.ColorHex, tag));
        }

        var addBtn = new AppButton
        {
            Classes = { "ghost", "home-tag-pill", "add-pill" },
            Margin = new Thickness(0, 0, 6, 6),
            Content = "+ " + LocalizationText.Get("home.tag.new")
        };
        addBtn.Click += async (_, _) =>
        {
            if (_actions != null) await _actions.GuardAsync(_actions.CreateTagAsync);
        };
        TagPillsPanel.Children.Add(addBtn);
    }

    private Control CreateTagPill(string key, string label, int count, string? colorHex, FileTag? tag)
    {
        var btn = new AppButton
        {
            Classes = { "ghost", "home-tag-pill" },
            Margin = new Thickness(0, 0, 6, 6)
        };
        if (key == _activeTagKey) btn.Classes.Add("active");

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrWhiteSpace(colorHex) && Color.TryParse(colorHex, out var color))
        {
            content.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock
        {
            Text = count.ToString(),
            Classes = { "home-muted" },
            VerticalAlignment = VerticalAlignment.Center
        });
        btn.Content = content;
        btn.Click += (_, _) =>
        {
            _activeTagKey = key;
            RenderTagPills();
            RenderCollectionsBody();
        };

        if (tag != null && _actions != null)
        {
            btn.ContextMenu = _actions.CreateFolderMenu(tag);
            EnableTagDrop(btn, tag);
        }
        return btn;
    }

    private void RenderCollectionsBody()
    {
        CollectionsListPanel.Children.Clear();
        CollectionsGridPanel.Children.Clear();
        if (_actions == null || _navigation == null || _workspace == null) return;

        var activeTag = _loadedTags.FirstOrDefault(t => TagKey(t) == _activeTagKey);
        var paths = _activeTagKey == AllTagKey
            ? _pathTags.Keys.OrderBy(System.IO.Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(p => p, StringComparer.Ordinal).ToArray()
            : (_tagPaths.TryGetValue(_activeTagKey, out var list) ? list : []);

        var entries = paths
            .Select(p => _entryCache.TryGetValue(p, out var entry) ? entry : null)
            .Where(e => e != null)
            .Cast<FileSystemEntry>()
            .ToArray();

        var isGrid = string.Equals(_prefs.CollectionViewMode, HomeCollectionViewModes.Grid, StringComparison.OrdinalIgnoreCase);
        CollectionsListPanel.IsVisible = !isGrid && entries.Length > 0;
        CollectionsGridPanel.IsVisible = isGrid && entries.Length > 0;
        CollectionsEmpty.IsVisible = entries.Length == 0;

        if (entries.Length == 0)
        {
            CollectionsEmpty.Text = activeTag != null
                ? LocalizationText.Get("home.empty.tag_folder", activeTag.DisplayName)
                : LocalizationText.Get("home.empty.collections");
            return;
        }

        var homeDir = _navigation.HomeDirectory;
        if (isGrid)
        {
            foreach (var entry in entries)
                CollectionsGridPanel.Children.Add(CreateCollectionGridTile(entry, activeTag, homeDir, _actions));
        }
        else
        {
            foreach (var entry in entries)
                CollectionsListPanel.Children.Add(CreateCollectionListRow(entry, activeTag, homeDir, _actions, _workspace));
        }
    }

    private Control CreateCollectionListRow(
        FileSystemEntry entry, FileTag? activeTag, string homeDir, HomeItemActions actions, HomeWorkspaceService workspace)
    {
        var row = new Border
        {
            Classes = { "home-row-container" },
            Opacity = entry.IsReadable ? 1 : 0.55,
            ContextMenu = actions.CreateItemContextMenu(entry, activeTag)
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 10 };

        // Col 0: icon + full name + parent path
        var hit = new AppButton { Classes = { "ghost", "home-row-hit" } };
        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*"), ColumnSpacing = 10 };
        main.Children.Add(actions.CreateFileIcon(entry, 24));
        var labels = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(AppTypography.BindFontSize(new FileNameText
        {
            Text = entry.DisplayName,
            FontWeight = FontWeight.Medium
        }, AppTypography.Body));
        labels.Children.Add(new TextBlock
        {
            Text = HomePathFormatting.FormatParentPath(entry.FullPath, homeDir),
            Classes = { "home-row-path" }
        });
        Grid.SetColumn(labels, 1);
        main.Children.Add(labels);
        hit.Content = main;
        ToolTip.SetTip(hit, entry.FullPath);
        AutomationProperties.SetName(hit, entry.DisplayName);
        hit.Click += async (_, _) => await actions.GuardAsync(() => actions.OpenEntryAsync(entry));
        grid.Children.Add(hit);

        // Col 1: inline tag chips when viewing "All"
        if (_activeTagKey == AllTagKey && _pathTags.TryGetValue(entry.FullPath, out var itemTags) && itemTags.Count > 0)
        {
            var tagChips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            foreach (var t in itemTags.Take(2))
            {
                var chipContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                if (Color.TryParse(t.ColorHex, out var c))
                    chipContent.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center });
                chipContent.Children.Add(AppTypography.BindFontSize(new TextBlock { Text = t.DisplayName, Classes = { "home-muted" } }, AppTypography.Meta));
                tagChips.Children.Add(new Border { Classes = { "home-item-tag-chip" }, Child = chipContent });
            }
            Grid.SetColumn(tagChips, 1);
            grid.Children.Add(tagChips);
        }

        // Col 2: first-class script command pill
        if (!entry.IsDirectory && DistributionChannel.SupportsSystemIntegration && System.IO.Path.IsPathFullyQualified(entry.FullPath))
        {
            var commands = workspace.GetCommands(entry.FullPath);
            if (commands.Count > 0)
            {
                var primary = commands[0];
                var runBtn = new AppButton
                {
                    Classes = { "ghost", "home-script-run" },
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 4,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new PathIcon { Data = Geometry.Parse(Icons.Play), Width = 10, Height = 10 },
                            new TextBlock { Text = primary.Name, VerticalAlignment = VerticalAlignment.Center }
                        }
                    }
                };
                ToolTip.SetTip(runBtn, primary.Command);
                runBtn.Click += async (_, e) =>
                {
                    e.Handled = true;
                    await actions.GuardAsync(() => actions.RunScriptCommandAsync(entry.FullPath, primary));
                };
                Grid.SetColumn(runBtn, 2);
                grid.Children.Add(runBtn);
            }
            else if (HomeScriptCommand.IsScript(entry.FullPath))
            {
                var setupBtn = new AppButton
                {
                    Classes = { "ghost", "home-script-run", "setup" },
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = LocalizationText.Get("home.script.configure")
                };
                ToolTip.SetTip(setupBtn, LocalizationText.Get("编辑脚本命令…"));
                setupBtn.Click += async (_, e) =>
                {
                    e.Handled = true;
                    await actions.GuardAsync(() => actions.EditCommandsAsync(entry.FullPath));
                };
                Grid.SetColumn(setupBtn, 2);
                grid.Children.Add(setupBtn);
            }
        }

        // Col 3: flush right-aligned size/date that swaps cleanly into Preview + Locate on hover
        var metaParts = new List<string>(2);
        if (!entry.IsDirectory && entry.Size > 0) metaParts.Add(entry.FormattedSize);
        if (entry.LastModified != default) metaParts.Add(HomeItemActions.FormatAddedTime(entry.LastModified.ToUniversalTime(), DateTime.UtcNow));
        var rightOverlay = CreateMetaHoverSlot(
            metaParts.Count > 0 ? string.Join(" · ", metaParts) : null,
            CreateRowIconAction(Icons.Eye, LocalizationText.Get("home.action.preview"),
                () => actions.GuardAsync(() => actions.PreviewEntryAsync(entry))),
            CreateRowIconAction(Icons.Location, LocalizationText.Get("home.action.locate"),
                () => actions.GuardAsync(() => actions.RevealEntryAsync(entry))));
        Grid.SetColumn(rightOverlay, 3);
        grid.Children.Add(rightOverlay);

        row.Child = grid;
        return row;
    }

    private Control CreateCollectionGridTile(FileSystemEntry entry, FileTag? activeTag, string homeDir, HomeItemActions actions)
    {
        var btn = new AppButton
        {
            Classes = { "ghost", "home-grid-tile" },
            Width = 206,
            Margin = new Thickness(0, 0, 8, 8),
            Opacity = entry.IsReadable ? 1 : 0.55,
            ContextMenu = actions.CreateItemContextMenu(entry, activeTag)
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*"), ColumnSpacing = 10 };
        grid.Children.Add(actions.CreateFileIcon(entry, 26));
        var labels = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(AppTypography.BindFontSize(new FileNameText
        {
            Text = entry.DisplayName,
            FontWeight = FontWeight.Medium
        }, AppTypography.Label));
        labels.Children.Add(new TextBlock
        {
            Text = HomePathFormatting.FormatParentPath(entry.FullPath, homeDir),
            Classes = { "home-row-path" }
        });
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);
        btn.Content = grid;
        ToolTip.SetTip(btn, entry.FullPath);
        AutomationProperties.SetName(btn, entry.DisplayName);
        btn.Click += async (_, _) => await actions.GuardAsync(() => actions.OpenEntryAsync(entry));
        return btn;
    }

    private static Panel CreateMetaHoverSlot(string? metaText, params Control[] hoverButtons)
    {
        var slot = new Panel { VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(metaText))
        {
            slot.Children.Add(new TextBlock
            {
                Text = metaText,
                Classes = { "home-muted", "home-row-meta" },
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0)
            });
        }
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Classes = { "home-row-actions" },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var button in hoverButtons)
            actions.Children.Add(button);
        slot.Children.Add(actions);
        return slot;
    }

    private static AppButton CreateRowIconAction(string iconData, string tooltip, Func<Task> action)
    {
        var btn = new AppButton
        {
            Classes = { "ghost", "icon-action", "small" },
            Content = new PathIcon { Data = Geometry.Parse(iconData), Width = 14, Height = 14 }
        };
        ToolTip.SetTip(btn, tooltip);
        AutomationProperties.SetName(btn, tooltip);
        btn.Click += async (_, e) =>
        {
            e.Handled = true;
            await action();
        };
        return btn;
    }

    /* ============================================================
       Drag & Drop into Collections Card / Tag Pills
       ============================================================ */
    private void SetupCollectionsDrop()
    {
        DragDrop.SetAllowDrop(CollectionsCard, true);
        CollectionsCard.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var targetTag = _loadedTags.FirstOrDefault(t => TagKey(t) == _activeTagKey);
            var canDrop = targetTag != null && LocalDropPaths(e).Length > 0;
            e.DragEffects = canDrop ? DragDropEffects.Link : DragDropEffects.None;
            CollectionsCard.Classes.Set("drop-target", canDrop);
            e.Handled = true;
        });
        CollectionsCard.AddHandler(DragDrop.DragLeaveEvent, (_, _) => CollectionsCard.Classes.Remove("drop-target"));
        CollectionsCard.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            CollectionsCard.Classes.Remove("drop-target");
            var targetTag = _loadedTags.FirstOrDefault(t => TagKey(t) == _activeTagKey);
            var paths = LocalDropPaths(e);
            if (targetTag != null && paths.Length > 0 && _actions != null)
                await _actions.GuardAsync(() => _actions.AddPathsAsync(targetTag, paths));
            e.Handled = true;
        });
    }

    private void EnableTagDrop(AppButton pill, FileTag tag)
    {
        DragDrop.SetAllowDrop(pill, true);
        pill.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var canDrop = LocalDropPaths(e).Length > 0;
            e.DragEffects = canDrop ? DragDropEffects.Link : DragDropEffects.None;
            pill.Classes.Set("drop-target", canDrop);
            e.Handled = true;
        });
        pill.AddHandler(DragDrop.DragLeaveEvent, (_, _) => pill.Classes.Remove("drop-target"));
        pill.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            pill.Classes.Remove("drop-target");
            var paths = LocalDropPaths(e);
            if (paths.Length > 0 && _actions != null)
                await _actions.GuardAsync(() => _actions.AddPathsAsync(tag, paths));
            e.Handled = true;
        });
    }

    private static string[] LocalDropPaths(DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles();
        if (items == null) return [];
        return items.Where(i => i.Path.IsFile).Select(i => i.Path.LocalPath)
            .Where(System.IO.Path.IsPathFullyQualified).Distinct(StringComparer.Ordinal).ToArray();
    }

    /* ============================================================
       Header & Segmented Event Handlers
       ============================================================ */
    private async void OnOpenDiskManagement(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
            await window.OpenDiskManagementAsync();
    }

    private void OnOpenWorkgroupsMenu(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window && sender is Control anchor)
            window.OpenGroupPresetsMenu(anchor);
    }

    private void OnInboxFilterAll(object? sender, RoutedEventArgs e) => SetInboxFilter(HomeInboxFilter.All);
    private void OnInboxFilterDownloads(object? sender, RoutedEventArgs e) => SetInboxFilter(HomeInboxFilter.Downloads);
    private void OnInboxFilterEdited(object? sender, RoutedEventArgs e) => SetInboxFilter(HomeInboxFilter.Edited);

    private void SetInboxFilter(HomeInboxFilter filter)
    {
        _inboxFilter = filter;
        SetActiveClass(InboxAllButton, filter == HomeInboxFilter.All);
        SetActiveClass(InboxDownloadsButton, filter == HomeInboxFilter.Downloads);
        SetActiveClass(InboxEditedButton, filter == HomeInboxFilter.Edited);
        _ = RefreshInboxAsync();
    }

    private void OnCollectionsListView(object? sender, RoutedEventArgs e) => SetCollectionViewMode(HomeCollectionViewModes.List);
    private void OnCollectionsGridView(object? sender, RoutedEventArgs e) => SetCollectionViewMode(HomeCollectionViewModes.Grid);

    private void SetCollectionViewMode(string mode)
    {
        if (_workspace != null)
            _workspace.SaveSectionPreferences(_prefs with { CollectionViewMode = mode });
        else
        {
            _prefs = _prefs with { CollectionViewMode = mode };
            ApplySectionPreferences();
            RenderCollectionsBody();
        }
    }

    private static void SetActiveClass(Control control, bool active)
        => control.Classes.Set("active", active);

    private static string TagKey(FileTag tag)
        => $"{tag.Kind}:{FileTagCatalog.NormalizeName(tag.Name).ToUpperInvariant()}";
}
