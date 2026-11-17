using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public partial class DiskManagementView : UserControl, IDisposable
{
    private readonly IVolumeMonitorService? _volumes;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private LocalizationService? _language;
    private bool _attached;
    private bool _compact;
    private bool _showDetail;
    private bool _listening;
    private bool _disposed;
    private Control[] _visibilityAncestors = [];
    private ContextMenu? _menu;
    public DiskManagementViewModel ViewModel { get; }
    internal Func<string, Task>? OpenDirectory { get; set; }

    public DiskManagementView() : this(App.Services?.GetService<IDiskInformationService>() ?? new DiskInformationService(),
        App.Services?.GetService<IVolumeMonitorService>()) { }

    internal DiskManagementView(IDiskInformationService information, IVolumeMonitorService? volumes)
    {
        _volumes = volumes;
        ViewModel = new DiskManagementViewModel(information, volumes);
        InitializeComponent();
        DataContext = ViewModel;
        DiskList.AddHandler(KeyDownEvent, OnDiskKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _timer.Tick += OnTick;
        SizeChanged += (_, _) => UpdateLayoutMode();
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            _visibilityAncestors = this.GetVisualAncestors().OfType<Control>().ToArray();
            foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged += OnAncestorPropertyChanged;
            UpdateActivity();
            Dispatcher.UIThread.Post(UpdateActivity);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            StopWatchingAncestors();
            UpdateActivity();
        };
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) UpdateActivity(); };
    }

    internal void UpdateActivity()
    {
        var active = !_disposed && _attached && IsEffectivelyVisible;
        if (active == _listening) return;
        _listening = active;
        if (active)
        {
            if (_volumes != null) _volumes.VolumesChanged += OnVolumesChanged;
            _language = LocalizationService.Current;
            if (_language != null) _language.LanguageChanged += OnLanguageChanged;
            ViewModel.RefreshLanguage();
            ViewModel.SetActive(true);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            if (_volumes != null) _volumes.VolumesChanged -= OnVolumesChanged;
            if (_language != null) _language.LanguageChanged -= OnLanguageChanged;
            _language = null;
            ViewModel.SetActive(false);
            _menu?.Close();
        }
    }

    private void OnTick(object? sender, EventArgs e) { if (!ViewModel.IsRefreshing && !ViewModel.IsEjecting) _ = RefreshAsync(showProgress: false); }
    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) Dispatcher.UIThread.Post(UpdateActivity);
    }
    private void StopWatchingAncestors()
    {
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        _visibilityAncestors = [];
    }
    private void OnVolumesChanged() => Dispatcher.UIThread.Post(() => { if (_listening) _ = RefreshAsync(showProgress: false); });
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ViewModel.RefreshLanguage);
    internal Task RefreshAsync(bool showProgress = true) => ViewModel.RefreshAsync(showProgress);

    private void OnDiskSelected(object? sender, SelectionChangedEventArgs e)
    {
        UpdateLayoutMode();
    }

    private void OnDiskReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || ViewModel.SelectedDisk == null
            || e.Source is not Visual visual || (visual is not ListBoxItem && !visual.GetVisualAncestors().OfType<ListBoxItem>().Any())) return;
        ShowDetails();
    }

    private void OnDiskKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel.SelectedDisk == null) return;
        ShowDetails();
        e.Handled = true;
    }

    private void ShowDetails()
    {
        _showDetail = true;
        UpdateLayoutMode();
        if (_compact) BackToDisksButton.Focus();
    }

    private void OnBackToList(object? sender, RoutedEventArgs e)
    {
        _showDetail = false;
        UpdateLayoutMode();
        DiskList.UpdateLayout();
        if (ViewModel.SelectedDisk is { } selected && DiskList.ContainerFromItem(selected) is Control row) row.Focus();
    }
    private void UpdateLayoutMode()
    {
        var compact = Bounds.Width < 720;
        if (compact != _compact) { _compact = compact; _showDetail = false; }
        DiskLayout.ColumnDefinitions[0].Width = new GridLength(compact ? 1 : 220, compact ? GridUnitType.Star : GridUnitType.Pixel);
        DiskLayout.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 1, compact ? GridUnitType.Pixel : GridUnitType.Star);
        Grid.SetColumn(DiskDetailPane, compact ? 0 : 1);
        DiskListPane.IsVisible = !compact || !_showDetail;
        DiskDetailPane.IsVisible = !compact || _showDetail;
        BackToDisksButton.IsVisible = compact;
    }

    private async void OnOpenDisk(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanOpen || ViewModel.SelectedDisk is not { Info: var disk } || OpenDirectory == null) return;
        try { await OpenDirectory(disk.Path); }
        catch (Exception ex) { ShowOperationError(ex.Message); }
    }
    private async void OnEjectDisk(object? sender, RoutedEventArgs e) => await ViewModel.EjectAsync();

    private void OnMore(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button) return;
        _menu?.Close();
        _menu = new ContextMenu();
        var copy = new MenuItem { Header = LocalizationText.Get("disk.copy") };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(ViewModel.CopyInformation());
            }
            catch (Exception ex) { ShowOperationError(ex.Message); }
        };
        var utility = new MenuItem { Header = LocalizationText.Get("disk.utility") };
        utility.Click += async (_, _) =>
        {
            try
            {
                var path = OperatingSystem.IsMacOS() ? MacSandboxNative.ApplicationPath("com.apple.DiskUtility") : null;
                if (path == null || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher
                    || !await launcher.LaunchUriAsync(new Uri(path)))
                    ShowOperationError(LocalizationText.Get("disk.utility-error"));
            }
            catch (Exception ex) { ShowOperationError(ex.Message); }
        };
        _menu.Items.Add(copy);
        _menu.Items.Add(utility);
        _menu.Open(button);
    }

    private void ShowOperationError(string message)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
            AppToast.ShowError(window, LocalizationText.Get("disk.operation-error", message));
    }

    public void Dispose()
    {
        _disposed = true;
        _attached = false;
        StopWatchingAncestors();
        UpdateActivity();
        _timer.Tick -= OnTick;
        ViewModel.Dispose();
    }
}
