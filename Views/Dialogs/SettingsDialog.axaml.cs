using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views.Dialogs;

public partial class SettingsDialog : DialogWindow
{
    private readonly IDefaultAppService _defaultAppService;
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IOpenWithAppService _openWithService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly Dictionary<string, ToggleSwitch> _sidebarToggles = new(StringComparer.Ordinal);
    private List<OpenWithApp> _openWithApps = [];
    private List<AppListItem> _installedApps = [];
    private VersionInfo? _availableVersion;
    private readonly CancellationTokenSource _updateCancellation = new();
    private UpdateState _updateState = UpdateState.Idle;
    private bool _initializing = true;
    private bool _installedAppsLoaded;
    private int _installedAppsRenderVersion;

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    public SettingsDialog()
    {
        InitializeComponent();
        _defaultAppService = App.Services.GetRequiredService<IDefaultAppService>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _themeService = App.Services.GetRequiredService<IThemeService>();
        _openWithService = App.Services.GetRequiredService<IOpenWithAppService>();
        _appUpdateService = App.Services.GetRequiredService<IAppUpdateService>();
        Opened += OnOpened;
        Closed += (_, _) => _updateCancellation.Cancel();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        LoadSettings();
        await _openWithService.RemoveUnavailableAppsAsync();
        await LoadOpenWithAppsAsync();
        _initializing = false;
    }

    private void LoadSettings()
    {
        if (ViewModel == null) return;

        _initializing = true;
        DefaultManagerToggle.IsChecked = _defaultAppService.IsDefaultFolderHandler();
        AiAnalysisToggle.IsChecked = ViewModel.IsAiAnalysisEnabled;
        HideSystemFilesToggle.IsChecked = ViewModel.HideSystemFiles;
        HideDotFilesToggle.IsChecked = ViewModel.HideDotFiles;
        HideDotFoldersToggle.IsChecked = ViewModel.HideDotFolders;
        UsernameSettingLabel.Text = ViewModel.UserName;

        _sidebarToggles.Clear();
        foreach (var toggle in PanelSidebar.GetLogicalDescendants().OfType<ToggleSwitch>())
        {
            if (toggle.Tag is not string key) continue;
            _sidebarToggles[key] = toggle;
            toggle.IsChecked = _settingsService.Get(key, true);
        }

        var themeMode = _settingsService.Get("theme_mode", "system");
        ThemeModeCombo.SelectedIndex = themeMode switch { "light" => 1, "dark" => 2, _ => 0 };
        VibrancyToggle.IsChecked = _settingsService.Get("vibrancy_enabled", true);
        VibrancySlider.Value = _settingsService.Get("vibrancy_alpha", 0.85);
        VibrancySlider.IsEnabled = VibrancyToggle.IsChecked == true;
        UpdateVibrancyLabel();

        AboutVersion.Text = $"版本 {_appUpdateService.CurrentVersion}";
    }

    private void OnDefaultManagerChanged(object? sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var enabled = DefaultManagerToggle.IsChecked == true;
        var result = enabled
            ? _defaultAppService.SetAsDefaultFolderHandler()
            : _defaultAppService.ResetDefaultFolderHandler();

        GeneralStatusBorder.IsVisible = true;
        GeneralStatusBorder.Background = new SolidColorBrush(Color.Parse(result.Success ? "#1834C759" : "#18FF3B30"));
        GeneralStatusText.Foreground = new SolidColorBrush(Color.Parse(result.Success ? "#2D8A4E" : "#D63030"));
        GeneralStatusText.Text = result.Message;

        _initializing = true;
        DefaultManagerToggle.IsChecked = _defaultAppService.IsDefaultFolderHandler();
        _initializing = false;
    }

    private void OnAiAnalysisChanged(object? sender, RoutedEventArgs e)
    {
        if (!_initializing && ViewModel != null)
            ViewModel.IsAiAnalysisEnabled = AiAnalysisToggle.IsChecked == true;
    }

    private void OnFileDisplayChanged(object? sender, RoutedEventArgs e)
    {
        if (_initializing || ViewModel == null || sender is not ToggleSwitch { Tag: string tag } toggle) return;
        var value = toggle.IsChecked == true;
        switch (tag)
        {
            case "system": ViewModel.HideSystemFiles = value; break;
            case "files": ViewModel.HideDotFiles = value; break;
            case "folders": ViewModel.HideDotFolders = value; break;
        }
    }

    private void OnSidebarSettingChanged(object? sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not ToggleSwitch { Tag: string key } toggle) return;
        _settingsService.Set(key, toggle.IsChecked == true);
        ViewModel?.LoadSidebarVisibility();
        ViewModel?.NotifySidebarVisibilityChanged();
    }

    private void OnThemeModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeModeCombo.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        _settingsService.Set("theme_mode", mode);
        _themeService.SetThemeMode(mode);
    }

    private void OnVibrancyChanged(object? sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var enabled = VibrancyToggle.IsChecked == true;
        VibrancySlider.IsEnabled = enabled;
        _settingsService.Set("vibrancy_enabled", enabled);
        (Owner as MainWindow)?.ApplyAppearanceSettings();
    }

    private void OnVibrancyAlphaChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateVibrancyLabel();
        if (!_initializing)
        {
            _settingsService.Set("vibrancy_alpha", VibrancySlider.Value);
            (Owner as MainWindow)?.ApplyAppearanceSettings();
        }
    }

    private void UpdateVibrancyLabel()
    {
        if (VibrancyValueText != null)
            VibrancyValueText.Text = $"{(int)Math.Round(VibrancySlider.Value * 100)}%";
    }

    private async void ToggleAddApplicationPopup(object? sender, RoutedEventArgs e)
    {
        AddApplicationPopup.IsOpen = !AddApplicationPopup.IsOpen;
        if (!AddApplicationPopup.IsOpen || _installedAppsLoaded) return;

        InstalledAppsLoadingText.IsVisible = true;
        _installedApps = await _openWithService.GetInstalledAppsAsync();
        _installedAppsLoaded = true;
        InstalledAppsLoadingText.IsVisible = false;
        RebuildInstalledApps();
    }

    private void OnApplicationSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_installedAppsLoaded) RebuildInstalledApps();
    }

    private void RebuildInstalledApps()
    {
        var renderVersion = ++_installedAppsRenderVersion;
        InstalledAppsPanel.Children.Clear();
        var existing = _openWithApps.Select(app => app.BundleId).ToHashSet(StringComparer.Ordinal);
        var query = _installedApps.Where(app => !existing.Contains(app.BundleId));
        if (!string.IsNullOrWhiteSpace(ApplicationSearchBox.Text))
            query = query.Where(app => app.Name.Contains(ApplicationSearchBox.Text, StringComparison.OrdinalIgnoreCase));

        var apps = query.Take(5).ToList();
        foreach (var app in apps)
        {
            var row = new Button
            {
                Tag = app,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3),
                Content = CreateAppIdentity(app.Name, app.IconBase64, 18)
            };
            row.Classes.Add("ghost");
            row.Classes.Add("toolbar-popup-item");
            row.Click += AddApplication;
            InstalledAppsPanel.Children.Add(row);
            _ = LoadInstalledAppIconAsync(app, row, renderVersion);
        }

        if (apps.Count == 0)
            InstalledAppsPanel.Children.Add(new TextBlock
            {
                Text = _installedApps.Count == 0 ? "未找到已安装的应用" : "没有匹配的应用",
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, 12),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#8E8E93"))
            });
    }

    private async Task LoadInstalledAppIconAsync(AppListItem app, Button row, int renderVersion)
    {
        if (!string.IsNullOrWhiteSpace(app.IconBase64) || string.IsNullOrWhiteSpace(app.AppPath))
            return;

        try
        {
            var icon = await _openWithService.GetAppIconBase64ByPathAsync(app.AppPath);
            if (string.IsNullOrWhiteSpace(icon)) return;

            app.IconBase64 = icon;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (renderVersion != _installedAppsRenderVersion) return;
                if (!InstalledAppsPanel.Children.Contains(row)) return;
                if (!ReferenceEquals(row.Tag, app)) return;
                row.Content = CreateAppIdentity(app.Name, app.IconBase64, 18);
            });
        }
        catch
        {
        }
    }

    private async void AddApplication(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppListItem app }) return;
        await _openWithService.AddAsync(app.BundleId, app.Name, isTopLevel: true, app.IconBase64);
        AddApplicationPopup.IsOpen = false;
        ApplicationSearchBox.Text = string.Empty;
        await LoadOpenWithAppsAsync();
        RebuildInstalledApps();
    }

    private async System.Threading.Tasks.Task LoadOpenWithAppsAsync()
    {
        _openWithApps = await _openWithService.GetAllAsync();
        RebuildConfiguredApps();
    }

    private void RebuildConfiguredApps()
    {
        ConfiguredAppsPanel.Children.Clear();
        if (_openWithApps.Count == 0)
        {
            ConfiguredAppsPanel.Children.Add(new Border
            {
                Classes = { "settings-row" },
                Child = new TextBlock
                {
                    Text = "尚未配置任何应用",
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#8E8E93"))
                }
            });
            return;
        }

        for (var i = 0; i < _openWithApps.Count; i++)
        {
            var app = _openWithApps[i];
            var toggle = new ToggleSwitch
            {
                Tag = app,
                IsChecked = app.IsTopLevel,
                OnContent = string.Empty,
                OffContent = string.Empty,
                MinWidth = 38,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
            };
            toggle.IsCheckedChanged += OnTopLevelChanged;

            var delete = new Button
            {
                Tag = app,
                Classes = { "ghost", "openwith-delete" },
                Content = new PathIcon { Data = Geometry.Parse(Assets.Icons.Delete), Width = 14, Height = 14 }
            };
            ToolTip.SetTip(delete, "删除");
            delete.Click += RemoveApplication;

            var actions = new StackPanel
            {
                Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "显示在根目录", FontSize = 11, Opacity = 0.6, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center },
                    toggle,
                    delete
                }
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
            grid.Children.Add(CreateAppIdentity(app.Label, app.IconBase64, 20));
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            var row = new Border { Classes = { "settings-compact-row" }, Child = grid };
            if (i < _openWithApps.Count - 1)
                row.Classes.Add("settings-divider");
            ConfiguredAppsPanel.Children.Add(row);
        }
    }

    private async void OnTopLevelChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: OpenWithApp app } toggle) return;
        toggle.IsCheckedChanged -= OnTopLevelChanged;
        await _openWithService.UpdateAsync(app.Id, null, toggle.IsChecked == true, null);
        await LoadOpenWithAppsAsync();
    }

    private async void RemoveApplication(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OpenWithApp app }) return;
        await _openWithService.RemoveAsync(app.Id);
        await LoadOpenWithAppsAsync();
        if (_installedAppsLoaded) RebuildInstalledApps();
    }

    private static StackPanel CreateAppIdentity(string label, string? iconBase64, double iconSize)
    {
        var panel = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
        };
        var bitmap = DecodeBitmap(iconBase64);
        if (bitmap != null)
            panel.Children.Add(new Image { Source = bitmap, Width = iconSize, Height = iconSize, Stretch = Stretch.Uniform });
        else
            panel.Children.Add(new PathIcon { Data = Geometry.Parse(Assets.Icons.CodeEditor), Width = iconSize, Height = iconSize });
        panel.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center });
        return panel;
    }

    private static Bitmap? DecodeBitmap(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var comma = base64.IndexOf(',');
            var payload = base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0 ? base64[(comma + 1)..] : base64;
            using var stream = new MemoryStream(Convert.FromBase64String(payload));
            return new Bitmap(stream);
        }
        catch { return null; }
    }

    private async void OnUpdateButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_updateState is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing)
            return;

        if (_availableVersion != null
            && _updateState is UpdateState.UpdateAvailable or UpdateState.Error)
        {
            SetUpdateState(UpdateState.Downloading, "准备下载...");
            try
            {
                var progress = new Progress<(double Progress, string Status)>(report =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var installing = report.Status.Contains("解压", StringComparison.Ordinal)
                                         || report.Status.Contains("校验", StringComparison.Ordinal)
                                         || report.Status.Contains("安装", StringComparison.Ordinal)
                                         || report.Status.Contains("重启", StringComparison.Ordinal);
                        _updateState = installing ? UpdateState.Installing : UpdateState.Downloading;
                        UpdateProgress.IsIndeterminate = report.Progress < 0 || installing;
                        if (report.Progress >= 0)
                            UpdateProgress.Value = Math.Clamp(report.Progress, 0, 100);
                        UpdateStatus.Text = report.Status;
                    });
                });
                await _appUpdateService.DownloadAndInstallAsync(
                    _availableVersion,
                    progress,
                    _updateCancellation.Token);
            }
            catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                SetUpdateState(UpdateState.Error, $"更新失败: {ex.Message}");
            }
            return;
        }

        _availableVersion = null;
        ChangelogBorder.IsVisible = false;
        SetUpdateState(UpdateState.Checking, "正在连接更新服务器...");
        try
        {
            _availableVersion = await _appUpdateService.CheckVersionAsync(_updateCancellation.Token);
            if (_availableVersion == null)
            {
                SetUpdateState(UpdateState.NoUpdate, "当前已是最新版本");
            }
            else
            {
                SetUpdateState(UpdateState.UpdateAvailable, $"发现新版本 {_availableVersion.Version}");
                var releaseDate = DateTime.TryParse(_availableVersion.DateTime, out var parsedDate)
                    ? parsedDate.ToString("yyyy-MM-dd")
                    : _availableVersion.DateTime;
                ChangelogTitle.Text = string.IsNullOrWhiteSpace(releaseDate)
                    ? $"版本 {_availableVersion.Version} 更新内容"
                    : $"版本 {_availableVersion.Version} · {releaseDate}";
                ChangelogText.Text = _availableVersion.Memo;
                ChangelogBorder.IsVisible = !string.IsNullOrWhiteSpace(_availableVersion.Memo);
            }
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            SetUpdateState(UpdateState.Error, $"检查失败: {ex.Message}");
        }
    }

    private void SetUpdateState(UpdateState state, string status)
    {
        _updateState = state;
        UpdateStatus.Text = status;
        UpdateProgress.IsIndeterminate = false;

        switch (state)
        {
            case UpdateState.Checking:
                UpdateButton.IsVisible = true;
                UpdateButton.IsEnabled = false;
                UpdateButton.Content = "检查中...";
                UpdateProgress.IsVisible = false;
                break;
            case UpdateState.UpdateAvailable:
                UpdateButton.IsVisible = true;
                UpdateButton.IsEnabled = true;
                UpdateButton.Content = "立即更新";
                UpdateProgress.IsVisible = false;
                break;
            case UpdateState.Downloading:
                UpdateButton.IsVisible = false;
                UpdateButton.IsEnabled = false;
                UpdateProgress.IsVisible = true;
                UpdateProgress.Value = 0;
                break;
            case UpdateState.Installing:
                UpdateButton.IsVisible = false;
                UpdateButton.IsEnabled = false;
                UpdateProgress.IsVisible = true;
                UpdateProgress.IsIndeterminate = true;
                break;
            case UpdateState.Error:
                UpdateButton.IsVisible = true;
                UpdateButton.IsEnabled = true;
                UpdateButton.Content = _availableVersion == null ? "重新检查" : "重试更新";
                UpdateProgress.IsVisible = false;
                break;
            case UpdateState.Idle:
            case UpdateState.NoUpdate:
            default:
                UpdateButton.IsVisible = true;
                UpdateButton.IsEnabled = true;
                UpdateButton.Content = "检查更新";
                UpdateProgress.IsVisible = false;
                break;
        }
    }

    private enum UpdateState
    {
        Idle,
        Checking,
        NoUpdate,
        UpdateAvailable,
        Downloading,
        Installing,
        Error,
    }
}
