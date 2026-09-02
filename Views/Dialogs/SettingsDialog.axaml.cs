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
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views.Dialogs;

public partial class SettingsDialog : DialogWindow
{
    private sealed record SearchLocationRow(string Path, string DisplayPath);

    private readonly IDefaultAppService _defaultAppService;
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly ITypographyService _typographyService;
    private readonly IInteractionStyleService _interactionStyleService;
    private readonly IOpenWithAppService _openWithService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IGlobalSearchScopeService _globalSearchScopeService;
    private readonly Dictionary<string, ToggleSwitch> _sidebarToggles = new(StringComparer.Ordinal);
    private List<OpenWithApp> _openWithApps = [];
    private List<AppListItem> _installedApps = [];
    private bool _initializing = true;
    private bool _updatingInteractionStyleSettings;
    private bool _installedAppsLoaded;
    private int _installedAppsRenderVersion;
    private InteractionThemeVariant _editingInteractionTheme;
    private InteractionStyleToken _selectedInteractionToken = InteractionStyleToken.Hover;

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    public SettingsDialog()
        : this(
            App.Services.GetRequiredService<IDefaultAppService>(),
            App.Services.GetRequiredService<ISettingsService>(),
            App.Services.GetRequiredService<IThemeService>(),
            App.Services.GetRequiredService<ITypographyService>(),
            App.Services.GetRequiredService<IOpenWithAppService>(),
            App.Services.GetRequiredService<IAppUpdateService>(),
            App.Services.GetRequiredService<IInteractionStyleService>(),
            App.Services.GetRequiredService<IGlobalSearchScopeService>())
    {
    }

    internal SettingsDialog(
        IDefaultAppService defaultAppService,
        ISettingsService settingsService,
        IThemeService themeService,
        ITypographyService typographyService,
        IOpenWithAppService openWithService,
        IAppUpdateService appUpdateService,
        IInteractionStyleService? interactionStyleService = null,
        IGlobalSearchScopeService? globalSearchScopeService = null)
    {
        InitializeComponent();
        _defaultAppService = defaultAppService;
        _settingsService = settingsService;
        _themeService = themeService;
        _typographyService = typographyService;
        _interactionStyleService = interactionStyleService ?? new Services.Impl.InteractionStyleService(settingsService);
        _openWithService = openWithService;
        _appUpdateService = appUpdateService;
        _globalSearchScopeService = globalSearchScopeService
            ?? new Services.Impl.GlobalSearchScopeService(settingsService);
        Opened += OnOpened;
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
        _initializing = true;
        _interactionStyleService.Initialize();
        LoadInteractionStyleSettings();
        LoadSearchLocations();

        if (ViewModel == null) return;

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
        TypographyPresetCombo.SelectedIndex = _typographyService.CurrentPreset switch
        {
            FontSizePreset.Small => 0,
            FontSizePreset.Large => 2,
            _ => 1
        };
        VibrancyToggle.IsChecked = _settingsService.Get("vibrancy_enabled", true);
        VibrancySlider.Value = _settingsService.Get("vibrancy_alpha", 0.30);
        VibrancySlider.IsEnabled = VibrancyToggle.IsChecked == true;
        UpdateVibrancyLabel();

        AboutVersion.Text = $"版本 {_appUpdateService.CurrentVersion}";
    }

    private void LoadSearchLocations()
    {
        if (SearchLocationsList == null || DefaultSearchLocationsList == null)
            return;

        var defaultPaths = Services.Impl.GlobalSearchScopeService.DefaultSearchFolders
            .Select(NormalizeSearchLocation)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = _globalSearchScopeService.CustomFolders
            .Select(path => new SearchLocationRow(path, FormatSearchLocation(path)))
            .ToList();

        var defaultRows = rows
            .Where(row => defaultPaths.Contains(row.Path))
            .ToList();
        DefaultSearchLocationsList.ItemsSource = defaultRows;
        DefaultSearchLocationsList.IsVisible = defaultRows.Count > 0;
        var extraRows = rows
            .Where(row => !defaultPaths.Contains(row.Path))
            .ToList();
        SearchLocationsList.ItemsSource = extraRows;
        SearchLocationsList.IsVisible = extraRows.Count > 0;
        DefaultSearchLocationsList.SelectedIndex = -1;
        SearchLocationsList.SelectedIndex = -1;
        UpdateSearchLocationButtons();
    }

    private void OnSearchLocationSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateSearchLocationButtons();
    }

    private void UpdateSearchLocationButtons()
    {
        if (RemoveSearchLocationButton != null)
            RemoveSearchLocationButton.IsEnabled =
                (SearchLocationsList?.SelectedIndex ?? -1) >= 0
                || (DefaultSearchLocationsList?.SelectedIndex ?? -1) >= 0;
    }

    private async void OnAddSearchLocation(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "添加搜索位置",
            AllowMultiple = true
        });
        var selected = folders
            .Select(folder => folder.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (selected.Length == 0)
            return;

        _globalSearchScopeService.SetCustomFolders(
            _globalSearchScopeService.CustomFolders.Concat(selected));
        LoadSearchLocations();
    }

    private void OnRemoveSearchLocation(object? sender, RoutedEventArgs e)
    {
        var selectedPath = SearchLocationsList?.SelectedItem is SearchLocationRow extraRow
            ? extraRow.Path
            : DefaultSearchLocationsList?.SelectedItem is SearchLocationRow defaultRow
                ? defaultRow.Path
                : null;
        if (string.IsNullOrWhiteSpace(selectedPath))
            return;

        _globalSearchScopeService.SetCustomFolders(
            _globalSearchScopeService.CustomFolders
                .Where(path => !path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase)));
        LoadSearchLocations();
    }

    private static string NormalizeSearchLocation(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (ArgumentException)
        {
            return path.Trim();
        }
    }

    private static string FormatSearchLocation(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.Equals(home, StringComparison.OrdinalIgnoreCase))
            return "~";
        if (path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "~" + path[home.Length..];
        return path;
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
        _interactionStyleService.ApplyCurrentTheme();
    }

    private void OnTypographyPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || TypographyPresetCombo.SelectedItem is not ComboBoxItem { Tag: string preset })
            return;

        _typographyService.SetPreset(preset switch
        {
            "small" => FontSizePreset.Small,
            "large" => FontSizePreset.Large,
            _ => FontSizePreset.Standard
        });
    }

    private void LoadInteractionStyleSettings()
    {
        _editingInteractionTheme = _interactionStyleService.CurrentTheme;
        _updatingInteractionStyleSettings = true;
        InteractionThemeVariantCombo.SelectedIndex = _editingInteractionTheme == InteractionThemeVariant.Dark ? 1 : 0;
        _updatingInteractionStyleSettings = false;
        RefreshInteractionColorEditor();
        InteractionStyleStatusText.IsVisible = false;
    }

    private void OnInteractionThemeVariantChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _updatingInteractionStyleSettings
            || InteractionThemeVariantCombo.SelectedItem is not ComboBoxItem { Tag: string theme })
            return;

        _editingInteractionTheme = theme == "dark" ? InteractionThemeVariant.Dark : InteractionThemeVariant.Light;
        RefreshInteractionColorEditor();
    }

    private void OnInteractionTokenSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !TryGetInteractionToken(tag, out var token))
            return;

        _selectedInteractionToken = token;
        RefreshInteractionColorEditor();
    }

    private void OnInteractionPaletteColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color }
            || !_interactionStyleService.TrySetColor(_selectedInteractionToken, _editingInteractionTheme, color))
            return;

        InteractionStyleStatusText.IsVisible = false;
        RefreshInteractionColorEditor();
    }

    private void OnInteractionCustomColorChanged(object? sender, TextChangedEventArgs e)
    {
        if (_initializing || _updatingInteractionStyleSettings)
            return;

        if (_interactionStyleService.TrySetColor(
                _selectedInteractionToken,
                _editingInteractionTheme,
                InteractionCustomColorBox.Text ?? string.Empty))
        {
            InteractionStyleStatusText.IsVisible = false;
            RefreshInteractionColorEditor();
            return;
        }

        InteractionStyleStatusText.Text = "颜色格式无效，请使用 #RRGGBB 或 #AARRGGBB。";
        InteractionStyleStatusText.IsVisible = true;
    }

    private void OnResetInteractionColors(object? sender, RoutedEventArgs e)
    {
        _interactionStyleService.ResetColors(_editingInteractionTheme);
        InteractionStyleStatusText.IsVisible = false;
        RefreshInteractionColorEditor();
    }

    private void RefreshInteractionColorEditor()
    {
        _updatingInteractionStyleSettings = true;
        try
        {
            UpdateInteractionColorRow(InteractionStyleToken.Hover, InteractionHoverColorPreview, InteractionHoverColorValue, InteractionHoverColorButton);
            UpdateInteractionColorRow(InteractionStyleToken.Pressed, InteractionPressedColorPreview, InteractionPressedColorValue, InteractionPressedColorButton);
            UpdateInteractionColorRow(InteractionStyleToken.Selected, InteractionSelectedColorPreview, InteractionSelectedColorValue, InteractionSelectedColorButton);
            UpdateInteractionColorRow(InteractionStyleToken.SelectedHover, InteractionSelectedHoverColorPreview, InteractionSelectedHoverColorValue, InteractionSelectedHoverColorButton);
            UpdateInteractionColorRow(InteractionStyleToken.TextHighlight, InteractionTextHighlightColorPreview, InteractionTextHighlightColorValue, InteractionTextHighlightColorButton);
            InteractionSelectedTokenText.Text = $"正在设置：{GetInteractionTokenName(_selectedInteractionToken)}（{GetInteractionThemeName(_editingInteractionTheme)}）";
            InteractionCustomColorBox.Text = _interactionStyleService.GetColor(_selectedInteractionToken, _editingInteractionTheme);
        }
        finally
        {
            _updatingInteractionStyleSettings = false;
        }
    }

    private void UpdateInteractionColorRow(InteractionStyleToken token, Border preview, TextBlock value, Button button)
    {
        var color = _interactionStyleService.GetColor(token, _editingInteractionTheme);
        preview.Background = new SolidColorBrush(Color.Parse(color));
        value.Text = color;
        button.Classes.Set("selected", token == _selectedInteractionToken);
    }

    private static string GetInteractionTokenName(InteractionStyleToken token) => token switch
    {
        InteractionStyleToken.Hover => "悬停",
        InteractionStyleToken.Pressed => "按下",
        InteractionStyleToken.Selected => "选中、已勾选或焦点",
        InteractionStyleToken.SelectedHover => "选中时悬停",
        InteractionStyleToken.TextHighlight => "文本高亮",
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, null)
    };

    private static string GetInteractionThemeName(InteractionThemeVariant theme) => theme == InteractionThemeVariant.Dark
        ? "深色主题"
        : "浅色主题";

    private static bool TryGetInteractionToken(string value, out InteractionStyleToken token)
    {
        token = value switch
        {
            "hover" => InteractionStyleToken.Hover,
            "pressed" => InteractionStyleToken.Pressed,
            "selected" => InteractionStyleToken.Selected,
            "selected-hover" => InteractionStyleToken.SelectedHover,
            "text-highlight" => InteractionStyleToken.TextHighlight,
            _ => default
        };

        return value is "hover" or "pressed" or "selected" or "selected-hover" or "text-highlight";
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
            InstalledAppsPanel.Children.Add(AppTypography.BindFontSize(new TextBlock
            {
                Text = _installedApps.Count == 0 ? "未找到已安装的应用" : "没有匹配的应用",
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, 12),
                Foreground = new SolidColorBrush(Color.Parse("#8E8E93"))
            }, AppTypography.Label));
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
                Child = AppTypography.BindFontSize(new TextBlock
                {
                    Text = "尚未配置任何应用",
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.Parse("#8E8E93"))
                }, AppTypography.Label)
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
                    AppTypography.BindFontSize(new TextBlock { Text = "显示在根目录", Opacity = 0.6, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center }, AppTypography.Caption),
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
        panel.Children.Add(AppTypography.BindFontSize(new TextBlock { Text = label, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center }, AppTypography.Body));
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

}
