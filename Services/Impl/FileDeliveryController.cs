using Avalonia.Input;
using Avalonia.Threading;
using MacExplorer.Indexing;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

internal sealed class FileDeliveryController(IServiceProvider services, FileDeliveryService delivery) : IDisposable
{
    private MacFileDeliveryStatusItem? _statusItem;
    private FileDeliveryWindow? _window;
    private FileListViewModel? _files;
    private MacQuickLookService? _preview;
    private IServiceScope? _scope;
    private bool _dragging;
    private bool _disposed;

    public void Initialize()
    {
        if (!OperatingSystem.IsMacOS()) return;
        delivery.EnabledChanged += OnEnabledChanged;
        UpdateEnabled();
    }

    private void OnEnabledChanged(object? sender, EventArgs e) => UpdateEnabled();

    private void UpdateEnabled()
    {
        if (_disposed || _dragging) return;
        if (delivery.Enabled)
        {
            if (_statusItem != null) return;
            _statusItem = new MacFileDeliveryStatusItem();
            _statusItem.Clicked += Toggle;
            _statusItem.OutsideClicked += OnOutsideClicked;
        }
        else ReleasePanel();
    }

    private void EnsureWindow()
    {
        if (_window != null) return;
        _scope = services.CreateScope();
        var sp = _scope.ServiceProvider;
        var settings = new FileDeliverySettings(sp.GetRequiredService<ISettingsService>());
        var navigation = new NavigationViewModel(sp.GetRequiredService<IFileService>(),
            fsEventsWatcher: sp.GetService<IFSEventsWatcher>(), displayNameService: sp.GetService<IDisplayNameService>());
        _preview = new MacQuickLookService();
        _files = new FileListViewModel(navigation, sp.GetRequiredService<FileOpsViewModel>(),
            sp.GetRequiredService<SearchViewModel>(), sp.GetRequiredService<ArchiveViewModel>(),
            sp.GetRequiredService<AiViewModel>(), sp.GetRequiredService<PinnedFoldersViewModel>(),
            new SortFilterViewModel(settings), sp.GetRequiredService<IFileService>(),
            sp.GetRequiredService<IFileIndex>(), sp.GetRequiredService<IFileIndexWriter>(),
            sp.GetRequiredService<IndexConfiguration>(),
            thumbnailService: sp.GetService<IThumbnailService>(), quickLookService: _preview,
            settingsService: settings, directoryChangeNotifier: sp.GetService<IDirectoryChangeNotifier>(),
            loggerFactory: sp.GetService<ILoggerFactory>(), gitStatusService: sp.GetService<IGitStatusService>(),
            displayNameService: sp.GetService<IDisplayNameService>(), fileTagService: sp.GetRequiredService<IFileTagService>())
        { IsBrowseOnly = true };
        _files.UseColumnLayoutService(new FileListColumnLayoutService(settings));
        _window = new FileDeliveryWindow(delivery, sp.GetRequiredService<IFileTagService>(), _files);
        _window.FileList.DragSessionChanged += OnDragSession;
        _window.DismissRequested += Hide;
    }

    private void Toggle()
    {
        if (_disposed || _dragging) return;
        if (_window?.IsVisible == true) Hide();
        else ShowPanel();
    }

    public async void ShowPanel()
    {
        if (_disposed || _dragging || !delivery.Enabled) return;
        try
        {
            EnsureWindow();
            var window = _window!;
            Show();
            await window.ResumeAsync();
        }
        catch (Exception ex) { services.GetService<ILogger<FileDeliveryController>>()?.LogError(ex, "Unable to show file delivery"); }
    }

    private void Show()
    {
        _window!.Width = 560;
        _window.Height = _window.Width / Math.Sqrt(2);
        _window!.Show();
        _statusItem?.Place(_window);
        _window.FileList.Focus();
    }

    private void OnOutsideClicked()
    {
        if (_dragging || _window?.IsChoosingFolder == true || _window?.HasOpenPopup == true || _preview?.IsOpen == true) return;
        Hide();
    }

    private void Hide()
    {
        if (_dragging || _window == null) return;
        _window.Suspend();
        _window.Hide();
    }

    private void OnDragSession(FileDragSessionEvent e)
    {
        if (_disposed || _window == null) return;
        switch (e.Phase)
        {
            case FileDragPhase.Started: _dragging = true; break;
            case FileDragPhase.Moved:
                if (_dragging && _window.IsVisible && _statusItem?.ContainsPoint(e.ScreenPoint) == false)
                {
                    _window.Suspend();
                    _window.Hide();
                }
                break;
            case FileDragPhase.Ended:
                _dragging = false;
                // Run after AppKit has finished returning to its event loop.
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || _window == null) return;
                    if (!delivery.Enabled) { UpdateEnabled(); return; }
                    if (e.Effect == DragDropEffects.None)
                    {
                        Show();
                        _files!.SetDirectoryNotificationsPaused(false);
                    }
                    else Hide();
                });
                break;
        }
    }

    private void ReleasePanel()
    {
        _window?.Suspend();
        _preview?.Dispose();
        _window?.Close();
        _files?.Dispose();
        _scope?.Dispose();
        _statusItem?.Dispose();
        _window = null; _files = null; _preview = null; _scope = null; _statusItem = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        delivery.EnabledChanged -= OnEnabledChanged;
        ReleasePanel();
    }
}
