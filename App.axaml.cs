using MacExplorer.Services.Subscriptions;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MacExplorer.Views;
using MacExplorer.Indexing;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Search;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MacExplorer.Views.Dialogs;

namespace MacExplorer;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private static IClassicDesktopStyleApplicationLifetime? _desktop;
    internal static bool IsShuttingDown { get; private set; }
    private static WeakReference<MainWindow>? _lastActiveMainWindow;
    private readonly System.Threading.CancellationTokenSource _startupUpdateCancellation = new();
    private Task _startupServicesTask = Task.CompletedTask;

    public override void Initialize()
    {
        Platforms.MacOS.MacFontTableLifetime.Prepare();
        // Runtime XAML loading keeps startup behavior consistent across desktop platforms.
        // This ensures Application.Current is available before theme services initialize.
        AvaloniaXamlLoader.Load(this);
        Platforms.MacOS.MacFontLifetime.Prepare();
        MacExplorer.Services.Markdown.MarkdownLocalization.Register();
        Services = ConfigureServices();
        FeatureAccess.Current = Services.GetRequiredService<FeatureAccessService>();
        FeatureAccess.UpgradeRequested += ShowFeatureUpgrade;
        var language = Services.GetRequiredService<ILocalizationService>();
        Name = AppBranding.Name;
        if (OperatingSystem.IsMacOS())
            NativeMenu.SetMenu(this, Platforms.MacOS.ApplicationMenu.Create(language, ShowAbout, () => ShowMainWindow()));
        Services.GetRequiredService<IThemeService>().Initialize();
        Services.GetRequiredService<IInteractionStyleService>().Initialize();
        Services.GetRequiredService<ITypographyService>().Initialize();
        RegisterOmniboxProviders();

        // Catch UI-thread exceptions so a single handler error never crashes the app.
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is FeatureAccessDeniedException denied)
        {
            e.Handled = true;
            if (FeatureAccess.Current?.Snapshot.State != FeatureAccessState.Initializing) ShowFeatureUpgrade(denied.Feature);
            return;
        }
        var logPath = Path.Combine(RuntimePaths.LogDirectory, "macexplorer_crash.log");
        try
        {
            Directory.CreateDirectory(RuntimePaths.LogDirectory);
            File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] UI Thread Exception: {e.Exception}\n\n");
        }
        catch { }
        e.Handled = true;
    }

    private static void RegisterOmniboxProviders()
    {
        var frequentFolderService = Services.GetService<IFrequentFolderService>();
        OmniboxService.RegisterProvider(new PathOmniboxProvider());
        OmniboxService.RegisterProvider(new CommandOmniboxProvider(
            () => Services.GetRequiredService<Copilot.IAppCapabilityRegistry>()));
        // Search suggestions are backed by the app-wide FTS index, so the omnibox
        // reuses the same pipeline as in-window search, including OCR/AI tags.
        OmniboxService.RegisterProvider(new SearchOmniboxProvider(
            Services.GetService<IFileIndex>(), Services.GetService<IGlobalSearchScopeService>(),
            Services.GetRequiredService<ISearchService>()));
        OmniboxService.RegisterProvider(new PinnedFolderOmniboxProvider(Services.GetService<IPinnedFolderService>()));
        OmniboxService.RegisterProvider(new RecentDirectoryOmniboxProvider(frequentFolderService));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        MainWindow? mainWindow = null;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var startupPath = RuntimePaths.TestRoot
                ?? desktop.Args?.FirstOrDefault(path => Directory.Exists(path) || File.Exists(path));
#if DEBUG
            Tools.Testing.FolderTabRestoreSmoke.Prepare();
            if (Tools.Testing.FolderTabRestoreSmoke.UseSavedSession) startupPath = null;
#endif
            mainWindow = CreateWindow(
                string.IsNullOrEmpty(startupPath) ? null : Path.GetFullPath(startupPath),
                isPrimary: true);
            desktop.MainWindow = mainWindow;
            mainWindow.Opened += OnStartupWindowOpened;
            var stoppingPlugins = false;
            var pluginsStopped = false;
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (pluginsStopped) return;
                e.Cancel = true;
                if (stoppingPlugins) return;
                stoppingPlugins = true;
                if (Services.GetRequiredService<IRemoteFileEditService>().HasPendingChanges)
                {
                    var owner = desktop.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive)
                        ?? desktop.Windows.OfType<MainWindow>().FirstOrDefault();
                    if (owner != null && !await new ConfirmDialog("remote-edit.exit-title", "remote-edit.exit-message", "remote-edit.exit").ShowDialog<bool>(owner))
                    { stoppingPlugins = false; return; }
                }
                IsShuttingDown = true;
                SaveFolderTabSession();
                _startupUpdateCancellation.Cancel();
                await _startupServicesTask;
                await Services.GetRequiredService<IRemoteFileEditService>().StopAsync();
                await Services.GetRequiredService<IBackgroundTaskManager>().ShutdownAsync();
                await Services.GetRequiredService<IFileOperationHistoryService>().ClearHistoryAsync();
                try
                {
                    try { await ((IAsyncDisposable)Services.GetRequiredService<ILocalSendService>()).DisposeAsync(); }
                    finally
                    {
                        try { await Services.GetRequiredService<SearchIndexer>().DisposeAsync(); }
                        finally
                        {
                            try { await Services.GetRequiredService<Services.Plugins.PluginManager>().DisposeAsync(); }
                            finally { await Services.GetRequiredService<ApplicationStorageMaintenance>().DisposeAsync(); }
                        }
                    }
                }
                finally { pluginsStopped = true; desktop.Shutdown(); }
            };
            desktop.Exit += (_, _) =>
            {
                FeatureAccess.UpgradeRequested -= ShowFeatureUpgrade;
                FeatureAccess.Current?.Dispose();
                FeatureAccess.Current = null;
                Services.GetService<Services.Subscriptions.SubscriptionService>()?.Dispose();
                _startupUpdateCancellation.Cancel();
                Services.GetRequiredService<FileDeliveryController>().Dispose();
                Services.GetRequiredService<BackgroundThemeService>().Dispose();
                DirectoryAccess.Current.Dispose();
            };

            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += OnApplicationActivated;

        }

        base.OnFrameworkInitializationCompleted();
        if (!Design.IsDesignMode && mainWindow != null)
        {
            var localSend = Services.GetRequiredService<ILocalSendService>();
            localSend.ConfirmReceiveAsync = ShowLocalSendReceiveAsync;
            localSend.RequestPinAsync = ShowLocalSendPinAsync;
        }

        if (mainWindow is { IsVisible: false })
        {
            mainWindow.ShowAndActivate();
        }

#if DEBUG
        if (RuntimePaths.TestRoot != null && Environment.GetEnvironmentVariable("MACEXPLORER_WINDOW_RESIZE_SMOKE") == "1" && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.WindowResizeSmoke.RunAsync(smokeWindow), TimeSpan.FromSeconds(1));
        }
        if (RuntimePaths.TestRoot != null && Environment.GetEnvironmentVariable("MACEXPLORER_FILE_CONFLICT_SMOKE") == "1" && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.FileConflictSmoke.RunAsync(smokeWindow), TimeSpan.FromSeconds(1));
        }
        if (RuntimePaths.TestRoot != null && Environment.GetEnvironmentVariable("MACEXPLORER_FEATURE_UPGRADE_SMOKE") == "1" && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.FeatureUpgradeSmoke.RunAsync(smokeWindow), TimeSpan.FromSeconds(1));
        }
        if (RuntimePaths.TestRoot != null && Environment.GetEnvironmentVariable("MACEXPLORER_VIDEO_PREVIEW_SMOKE") == "1" && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.VideoPreviewSmoke.RunAsync(smokeWindow), TimeSpan.FromSeconds(1));
        }
        if (RuntimePaths.TestRoot != null && Environment.GetEnvironmentVariable("MACEXPLORER_GROUP_PRESETS_SMOKE") == "1" && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.GroupPresetsSmoke.RunAsync(smokeWindow), TimeSpan.FromSeconds(1));
        }
        if (Tools.Testing.FolderTabRestoreSmoke.Phase != null && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.FolderTabRestoreSmoke.RunAsync(smokeWindow), TimeSpan.FromSeconds(1));
        }
        if (RuntimePaths.TestRoot != null && Environment.GetEnvironmentVariable("MACEXPLORER_TAB_GROUPS_SMOKE") != null && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.TabGroupsSmoke.RunAsync(smokeWindow), TimeSpan.FromSeconds(1));
        }
        if (Tools.Testing.FeatureAccessSmoke.Enabled && mainWindow != null)
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.FeatureAccessSmoke.ManualExpired
                ? Tools.Testing.FeatureAccessSmoke.PrepareManualExpiredAsync(mainWindow)
                : Tools.Testing.FeatureAccessSmoke.RunAsync(mainWindow), TimeSpan.FromSeconds(1));
        if (RuntimePaths.TestRoot != null && Environment.GetEnvironmentVariable("MACEXPLORER_BACKGROUND_THEME_SMOKE") == "1" && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.BackgroundThemeSmoke.RunAsync(smokeWindow), TimeSpan.FromSeconds(1));
        }
        if (RuntimePaths.TestRoot != null && Environment.GetEnvironmentVariable("MACEXPLORER_TAG_CLIPBOARD_SMOKE") == "1" && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.TagClipboardSmoke.RunAsync(smokeWindow), TimeSpan.FromSeconds(1));
        }
        if (RuntimePaths.TestRoot != null && Environment.GetEnvironmentVariable("MACEXPLORER_DELIVERY_SMOKE") != null && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.FileDeliverySmoke.RunAsync(smokeWindow), TimeSpan.FromSeconds(1));
        }
        if (RuntimePaths.TestRoot != null && Environment.GetEnvironmentVariable("MACEXPLORER_WINDOW_RENDER_SMOKE") == "1" && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.WindowRenderSmoke.RunAsync(smokeWindow), TimeSpan.FromSeconds(1));
        }
        if (RuntimePaths.TestRoot != null && Environment.GetEnvironmentVariable("MACEXPLORER_TASK_RING_SMOKE") == "1" && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.TaskProgressRingSmoke.RunAsync(smokeWindow), TimeSpan.FromSeconds(1));
        }
        if (RuntimePaths.TestRoot != null && Environment.GetEnvironmentVariable("MACEXPLORER_WEBDAV_SMOKE") == "1" && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.WebDavMountSmoke.RunAsync(smokeWindow), TimeSpan.FromSeconds(1));
        }
        if (RuntimePaths.TestRoot != null && Environment.GetEnvironmentVariable("MACEXPLORER_WINDOW_OPEN_SMOKE") == "1" && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(() => _ = Tools.Testing.WindowOpenSmoke.RunAsync(smokeWindow), TimeSpan.FromSeconds(1));
        }
        if (RuntimePaths.TestRoot is { } smokeRoot && Environment.GetEnvironmentVariable("MACEXPLORER_NETWORK_SMOKE") == "1" && mainWindow != null)
        {
            var smokeWindow = mainWindow;
            DispatcherTimer.RunOnce(async () =>
            {
                try
                {
                    if (smokeWindow.DataContext is MainWindowViewModel model)
                        await model.FileList.NavigateToAsync(VirtualPath.Network);
                    await Task.Delay(8000);
                    var discovery = Services.GetRequiredService<INetworkDiscoveryService>();
                    Console.Error.WriteLine($"Network smoke: {discovery.Servers.Count} servers, error={discovery.Error}");
                    File.WriteAllText(Path.Combine(smokeRoot, "network-smoke.json"),
                        System.Text.Json.JsonSerializer.Serialize(new { discovery.Servers, discovery.Error }));
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
            }, TimeSpan.FromSeconds(1));
        }
#endif

        if (OperatingSystem.IsMacOS())
        {
            Services.GetRequiredService<FileDeliveryController>().Initialize();
            DispatcherTimer.RunOnce(
                () => Services.GetRequiredService<Platforms.MacCatalyst.Services.MacDockMenuService>().Register(),
                TimeSpan.FromSeconds(1));
        }
#if DEMO_AUTOMATION
        if (mainWindow != null) Tools.DemoRecord.DemoAutomation.Start(mainWindow);
#endif
    }

    private static async Task StartLocalSendAsync(ILocalSendService localSend)
    {
        try { await localSend.StartAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("LocalSend startup failed: " + ex); }
    }

    private static Window? LocalSendOwner()
        => _desktop?.Windows.OfType<MainWindow>().LastOrDefault(window => window.IsActive && window.IsVisible)
           ?? _desktop?.Windows.OfType<MainWindow>().LastOrDefault(window => window.IsVisible);

    private static void ShowAbout()
    {
        var owner = LocalSendOwner();
        if (owner is MainWindow mainWindow) mainWindow.ShowAbout();
        else
        {
            var dialog = new SettingsDialog();
            dialog.ShowAbout();
            dialog.Show();
        }
    }

    private static Task<LocalSendReceiveDecision> ShowLocalSendReceiveAsync(
        LocalSendIncomingRequest request, System.Threading.CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<LocalSendReceiveDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var dialog = new LocalSendReceiveDialog(request);
                completion.TrySetResult(await dialog.ShowRequestAsync(LocalSendOwner(), cancellationToken));
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    private static Task<string?> ShowLocalSendPinAsync(System.Threading.CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var dialog = new LocalSendPinDialog();
                completion.TrySetResult(await dialog.ShowRequestAsync(LocalSendOwner(), cancellationToken));
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    private void OnStartupWindowOpened(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Opened -= OnStartupWindowOpened;
            _startupServicesTask = StartDeferredServicesAsync(window);
        }
        Dispatcher.UIThread.Post(Views.FileListView.PrepareFileDrag, DispatcherPriority.ApplicationIdle);
        if (DistributionChannel.IsAppStore)
            Dispatcher.UIThread.Post(async () =>
            {
                await Services.GetRequiredService<FeatureAccessService>().StartAsync();
                Services.GetRequiredService<SubscriptionService>().Start();
            }, DispatcherPriority.ApplicationIdle);
        _ = Task.Run(CheckStartupUpdateAsync);
        Services.GetRequiredService<ApplicationStorageMaintenance>().Start();
        _ = StartSearchIndexAsync();
    }

    private async Task StartDeferredServicesAsync(MainWindow window)
    {
        var token = _startupUpdateCancellation.Token;
        try
        {
            var compositor = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(window)?.Compositor;
            if (compositor != null) await compositor.RequestCommitAsync().WaitAsync(token);
            token.ThrowIfCancellationRequested();
            var localSend = Services.GetRequiredService<ILocalSendService>();
            var plugins = Services.GetRequiredService<Services.Plugins.PluginManager>();
            var tags = Services.GetRequiredService<IFileTagService>();
            await Task.WhenAll(Task.Run(() => StartLocalSendAsync(localSend), token), Task.Run(async () =>
            {
                try { token.ThrowIfCancellationRequested(); await plugins.InitializeAsync(); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex) { Services.GetService<ILogger<App>>()?.LogError(ex, "Plugin startup failed"); }
                token.ThrowIfCancellationRequested();
                await tags.RetryPendingAsync(token);
            }, token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Services.GetService<ILogger<App>>()?.LogError(ex, "Background service startup failed"); }
    }

    private async Task StartSearchIndexAsync()
    {
        var ct = _startupUpdateCancellation.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            var indexer = Services.GetRequiredService<SearchIndexer>();
            foreach (var root in RuntimePaths.StartupIndexRoots)
            {
                ct.ThrowIfCancellationRequested();
                indexer.EnsureRoot(root);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Services.GetService<ILogger<App>>()?.LogError(ex, "Search index startup failed"); }
    }

    private async Task CheckStartupUpdateAsync()
    {
#if DEMO_AUTOMATION
        if (RuntimePaths.TestRoot != null) return;
#endif
        var ct = _startupUpdateCancellation.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            var version = DistributionChannel.IsAppStore
                ? await Services.GetRequiredService<IAppUpdateService>().CheckVersionAsync(ct).ConfigureAwait(false)
                : await StartupUpdateChecker.CheckAsync(ct).ConfigureAwait(false);
            if (version == null || ct.IsCancellationRequested)
                return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                var window = _desktop?.Windows.OfType<MainWindow>().LastOrDefault(w => w.IsActive)
                    ?? _desktop?.Windows.OfType<MainWindow>().LastOrDefault(w => w.IsVisible);
                window?.ShowUpdateAvailableToast(version);
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Services.GetRequiredService<ILogger<App>>().LogDebug(ex, "Startup update check failed");
        }
    }

    public static MainWindow OpenNewWindow(string? path = null)
        => OpenNewWindow(path, restoreFolderTabsOnOpen: false);

    private static MainWindow OpenNewWindow(string? path, bool restoreFolderTabsOnOpen)
    {
        var source = _desktop?.Windows.OfType<MainWindow>().LastOrDefault(w => w.IsActive)
                     ?? _desktop?.Windows.OfType<MainWindow>().LastOrDefault();
        var window = CreateWindow(path, isPrimary: restoreFolderTabsOnOpen, source);
        if (_desktop != null && !_desktop.Windows.Contains(_desktop.MainWindow!))
            _desktop.MainWindow = window;
        window.ShowAndActivate();
        return window;
    }

    internal static Window ShowMainWindow()
        => ShowOrCreateWindow(_desktop?.Windows.OfType<MainWindow>() ?? [],
            () => OpenNewWindow(null, restoreFolderTabsOnOpen: true));

    internal static Window ShowOrCreateWindow(System.Collections.Generic.IEnumerable<Window> windows, Func<Window> createWindow)
    {
        var candidates = windows.ToArray();
        var window = candidates.LastOrDefault(w => w.IsActive)
                     ?? candidates.LastOrDefault(w => w.IsVisible)
                     ?? candidates.LastOrDefault()
                     ?? createWindow();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        ShowAndActivateWindow(window);
        return window;
    }

    private static void ShowAndActivateWindow(Window window)
    {
        if (window is MainWindow mainWindow) mainWindow.ShowAndActivate();
        else
        {
            window.Show();
            window.Activate();
        }
    }

    internal static void RecordActiveMainWindow(MainWindow window)
        => _lastActiveMainWindow = new WeakReference<MainWindow>(window);

    internal static void SaveFolderTabSession()
    {
        if (_desktop == null) return;
        var windows = _desktop.Windows.OfType<MainWindow>().Where(window => !window.IsClosingForSession).ToArray();
        if (windows.Length == 0) return; // Keep the last individually closed window when quitting without windows.
        var active = Array.FindLastIndex(windows, window => window.IsActive);
        if (active < 0 && _lastActiveMainWindow?.TryGetTarget(out var lastActive) == true)
            active = Array.IndexOf(windows, lastActive);
        SaveFolderWindowSession(FolderWindowSession.Capture(windows.Select(window => window.CaptureFolderTabs()),
            active >= 0 ? active : windows.Length - 1));
    }

    internal static void SaveLastWindowFolderTabs(MainWindow window)
    {
        if (!IsShuttingDown && _desktop?.Windows.OfType<MainWindow>()
            .All(candidate => ReferenceEquals(candidate, window) || candidate.IsClosingForSession) == true)
            SaveFolderWindowSession(FolderWindowSession.Capture([window.CaptureFolderTabs()], 0));
    }

    private static void SaveFolderWindowSession(FolderWindowSession session)
    {
        try { session.Save(Services.GetRequiredService<ISettingsService>()); }
        catch (Exception ex)
        {
            Services.GetService<ILogger<App>>()?.LogWarning(ex, "Could not save folder tabs before closing");
        }
    }

    internal static Task<FolderWindowSession?> ReadFolderWindowsAsync()
    {
        var settings = Services.GetRequiredService<ISettingsService>();
        return Task.Run(() => FolderWindowSession.Read(settings, MainWindow.CanRestoreFolder));
    }

    internal static void ShowRestoredFolderWindows(FolderWindowSession session)
    {
        for (var i = 0; i < session.Windows.Length; i++)
        {
            if (i == session.SelectedWindowIndex) continue;
            var window = CreateWindow(null, isPrimary: false, folderTabs: session.Windows[i]);
            window.ShowActivated = false;
            window.Show();
        }
    }

    private static MainWindow CreateWindow(string? path, bool isPrimary, MainWindow? source = null, FolderTabSession? folderTabs = null)
    {
        var scope = Services.CreateScope();
        var window = new MainWindow { InitialNavigationPath = path, RestoreFolderTabsOnOpen = isPrimary, InitialFolderTabSession = folderTabs };
        var model = scope.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        var navigation = Services.GetRequiredService<NavigationBridge>();
        var initialPath = folderTabs?.Paths.FirstOrDefault() ?? path
            ?? (navigation.PendingQuickAccessFocus ? null : model.FileList.GetRestorableDirectoryPath());
        if (!string.IsNullOrEmpty(initialPath) && Path.IsPathFullyQualified(initialPath)
            && (!DistributionChannel.IsAppStore || DirectoryAccess.Current.CanAccess(initialPath)))
            model.FileList.PrepareInitialDirectoryView();
        window.DataContext = model;
        window.AttachScope(scope);
        Services.GetRequiredService<WindowPlacementService>().Prepare(window, isPrimary, source);
        return window;
    }

    private static void ShowFeatureUpgrade(PremiumFeature feature)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var window = _desktop?.Windows.OfType<Controls.AppWindow>().FirstOrDefault(w => w.IsActive)
                ?? ShowMainWindow() as Controls.AppWindow;
            if (window != null) ShowAndActivateWindow(window);
            window?.ShowUpgrade(feature);
        });
    }

    private static async void OnApplicationActivated(object? sender, ActivatedEventArgs e)
    {
        if (_desktop == null) return;
        FeatureAccess.Current?.Refresh();
        if (DistributionChannel.IsAppStore)
            _ = Services.GetRequiredService<Services.Subscriptions.SubscriptionService>().RefreshAsync();

        if (e.Kind == ActivationKind.Reopen)
        {
            ShowMainWindow();
            return;
        }

        var path = GetActivatedPath(e);
        if (path == null) return;
        var window = _desktop.Windows.OfType<MainWindow>().LastOrDefault(w => w.IsActive)
                     ?? _desktop.Windows.OfType<MainWindow>().LastOrDefault();

        if (window == null)
        {
            OpenNewWindow(path);
            return;
        }

        window.ShowAndActivate();
        if (!string.IsNullOrWhiteSpace(path))
            await window.NavigateToPathAsync(path);
    }

    internal static string? GetActivatedPath(ActivatedEventArgs e)
    {
        if (e is not FileActivatedEventArgs fileActivation)
            return null;

        var localPath = fileActivation.Files
            .Select(file => file.Path.LocalPath)
            .FirstOrDefault(path => Directory.Exists(path) || File.Exists(path));

        if (string.IsNullOrWhiteSpace(localPath)) return null;
        return Path.GetFullPath(localPath);
    }

    internal static void CloseDatabaseConnections()
    {
        (Services?.GetService(typeof(SqliteFileIndex)) as IDisposable)?.Dispose();
    }

    private static IServiceProvider ConfigureServices()
    {
        DirectoryAccess.PrepareSandboxTestProfile();
        RuntimePaths.PrepareTestRoot();
        ApplicationDataMigration.Prepare();
        _ = DirectoryAccess.Current;
        var services = new ServiceCollection();
        if (DistributionChannel.IsAppStore)
            services.AddSingleton(sp => new Services.Subscriptions.SubscriptionService(new Platforms.MacOS.StoreKitBridge(), true,
                logger: sp.GetService<ILogger<Services.Subscriptions.SubscriptionService>>()));
#if DEBUG
        if (Tools.Testing.FeatureAccessSmoke.Enabled)
            services.AddSingleton(_ => new SubscriptionService(Tools.Testing.FeatureAccessSmoke.Store, true, Tools.Testing.FeatureAccessSmoke.Clock));
#endif
        var indexConfig = new IndexConfiguration();
        services.AddSingleton(indexConfig);
        services.AddSingleton(_ => new ApplicationStorageMaintenance());
        services.AddSingleton(sp => new DatabaseConnectionFactory(indexConfig.DatabasePath));
        services.AddSingleton<SqliteFileIndex>(sp => new SqliteFileIndex(indexConfig.DatabasePath, sp.GetRequiredService<DatabaseConnectionFactory>()));
        services.AddSingleton<IFileIndex>(sp => sp.GetRequiredService<SqliteFileIndex>());
        services.AddSingleton<IFileIndexWriter>(sp => sp.GetRequiredService<SqliteFileIndex>());
        services.AddSingleton<Platforms.MacCatalyst.Services.MacFileService>(sp => new Platforms.MacCatalyst.Services.MacFileService(sp.GetRequiredService<SqliteFileIndex>()));
        services.AddSingleton<IRemoteConnectionService>(sp => new Services.Impl.RemoteConnectionService(
            sp.GetService<ILogger<Services.Impl.RemoteConnectionService>>())
            { ConfirmHostKeyAsync = Views.Dialogs.SftpHostKeyConsent.ConfirmAsync,
                RequestPrivateKeyPassphraseAsync = Views.Dialogs.SftpPrivateKeyConsent.RequestAsync });
        services.AddSingleton<INetworkMountService, NetworkMountService>();
        services.AddSingleton<IServerConnectionCoordinator, ServerConnectionCoordinator>();
        services.AddSingleton<INetworkDiscoveryService, NetworkDiscoveryService>();
        services.AddSingleton<INetworkLocationService, NetworkLocationService>();
        services.AddSingleton<SftpFileService>();
        services.AddSingleton<IRemoteFileService>(sp => sp.GetRequiredService<SftpFileService>());
        services.AddSingleton<IRemoteFileEditService, Services.Impl.RemoteFileEditService>();
        services.AddSingleton<IFileConflictResolver, UiFileConflictResolver>();
        services.AddSingleton<IFileService>(sp => new CompositeFileService(
            sp.GetRequiredService<Platforms.MacCatalyst.Services.MacFileService>(),
            sp.GetRequiredService<IRemoteFileService>(),
            sp.GetService<IBackgroundTaskManager>(), sp.GetRequiredService<IRemoteConnectionService>(),
            () => sp.GetRequiredService<IFileOperationHistoryService>(), sp.GetRequiredService<IRatingService>(),
            sp.GetRequiredService<IFileConflictResolver>(), sp.GetService<IFileTagService>(), sp.GetService<IAiTagService>()));
        services.AddSingleton<IApplicationLauncherService, Platforms.MacCatalyst.Services.MacApplicationLauncherService>();
        services.AddSingleton<IContextMenuService, Platforms.MacCatalyst.Services.MacContextMenuService>();
        services.AddSingleton<IOpenWithAppService>(sp => new OpenWithAppService(sp.GetRequiredService<DatabaseConnectionFactory>(), sp.GetService<ILogger<OpenWithAppService>>()));
        services.AddSingleton<ApplicationOpenCommandService>();
        services.AddSingleton<IMetadataService, Platforms.MacCatalyst.Services.MacMetadataService>();
        services.AddSingleton<INativeContextMenuService, Platforms.MacCatalyst.Services.MacNativeContextMenuService>();
        services.AddSingleton<IQuickLookService, Platforms.MacCatalyst.Services.MacQuickLookService>();
        services.AddSingleton<Services.Plugins.PluginManager>();
        services.AddSingleton<Services.Plugins.PluginMarketClient>();
        services.AddSingleton<IThumbnailService, Platforms.MacCatalyst.Services.MacThumbnailService>();
        services.AddSingleton<IClipboardService, Platforms.MacCatalyst.Services.MacClipboardService>();
        services.AddSingleton<IDragDropService, Platforms.MacCatalyst.Services.MacDragDropBridge>();
        services.AddSingleton<SearchCatalog>();
        services.AddSingleton<IPinyinInitials, Platforms.MacCatalyst.Services.MacPinyinInitials>();
        services.AddSingleton<ISearchChangeSource, Platforms.MacCatalyst.Services.MacSearchChangeSource>();
        services.AddSingleton<SearchIndexer>();
        services.AddSingleton<Platforms.MacCatalyst.Services.MacSearchService>();
        services.AddSingleton<ISearchService>(sp => sp.GetRequiredService<Platforms.MacCatalyst.Services.MacSearchService>());
        services.AddSingleton<IGlobalSearchService>(sp => sp.GetRequiredService<Platforms.MacCatalyst.Services.MacSearchService>());
        services.AddSingleton<ISearchSessionService>(sp => sp.GetRequiredService<Platforms.MacCatalyst.Services.MacSearchService>());
        services.AddSingleton<ISettingsService, Services.Impl.SettingsService>();
        services.AddSingleton<BackgroundThemeService>();
        services.AddSingleton<ILocalizationService, Services.Impl.LocalizationService>();
        services.AddSingleton<IShortcutService, ShortcutService>();
        services.AddSingleton<ILocalSendService, Services.Impl.LocalSendService>();
        services.AddSingleton<IAirDropService, Platforms.MacOS.MacAirDropService>();
        services.AddSingleton<Copilot.CopilotSettings>();
        services.AddSingleton<Copilot.CopilotCredentialStore>();
        services.AddSingleton<Copilot.CopilotStore>();
        services.AddSingleton<Copilot.CopilotSkillCatalog>();
        services.AddSingleton<Copilot.CopilotContentExtractor>();
        services.AddSingleton<Copilot.IAppCapabilityRegistry, Copilot.AppCapabilityRegistry>();
        services.AddSingleton<HomeWorkspaceService>();
        services.AddSingleton<FileDeliveryService>();
        services.AddSingleton<FileDeliveryController>();
        services.AddSingleton<IAppTrialStore, AppTrialStore>();
        services.AddSingleton(sp => new FeatureAccessService(sp.GetService<SubscriptionService>(),
            sp.GetRequiredService<IAppTrialStore>(),
#if DEBUG
            DistributionChannel.IsAppStore || Tools.Testing.FeatureAccessSmoke.Enabled,
            time: Tools.Testing.FeatureAccessSmoke.Enabled ? Tools.Testing.FeatureAccessSmoke.Clock : null,
#else
            DistributionChannel.IsAppStore,
#endif
            logger: sp.GetService<ILogger<FeatureAccessService>>()));
        services.AddSingleton<HomeScriptRunner>();
        services.AddSingleton<IGlobalSearchScopeService, Services.Impl.GlobalSearchScopeService>();
        services.AddTransient<FileListColumnLayoutService>();
        services.AddSingleton<IDirectoryPreferencesService, DirectoryPreferencesService>();
        services.AddSingleton<WindowPlacementService>();
        services.AddSingleton<IRatingService, Services.Impl.RatingService>();
        services.AddSingleton<IFinderTagQueryService, Platforms.MacCatalyst.Services.MacFinderTagQueryService>();
        services.AddSingleton<IFileTagStore, Platforms.MacCatalyst.Services.MacFileTagStore>();
        services.AddSingleton<IFileTagService, Services.Impl.FileTagService>();
        services.AddSingleton<IFrequentFolderService>(sp => new Services.Impl.FrequentFolderService(sp.GetRequiredService<DatabaseConnectionFactory>(), RuntimePaths.HomeDirectory));
        services.AddSingleton<IGitStatusService>(sp => new Services.Impl.GitStatusService(sp.GetService<ILoggerFactory>()));
        services.AddSingleton<IPinnedFolderService>(sp => new Services.Impl.PinnedFolderService(sp.GetRequiredService<DatabaseConnectionFactory>()));
        services.AddSingleton<IArchiveService, Services.Impl.ArchiveService>();
        services.AddSingleton<IBackgroundTaskManager>(sp => new Services.Impl.BackgroundTaskManager(sp.GetService<ILoggerFactory>(), sp.GetRequiredService<ISettingsService>()));
        services.AddSingleton<IBatchRenameService>(sp => new Services.Impl.BatchRenameService(
            sp.GetRequiredService<IFileService>(),
            sp.GetService<IFileIndexWriter>(),
            sp.GetService<IAiTagService>(),
            sp.GetService<IPinnedFolderService>(),
            sp.GetService<IDirectoryChangeNotifier>(),
            sp.GetService<IFileTagService>(),
            sp.GetService<ILogger<Services.Impl.BatchRenameService>>(), sp.GetService<IMetadataService>(), sp.GetRequiredService<IRatingService>()));
        services.AddSingleton<IFileOperationHistoryService>(sp => new Services.Impl.FileOperationHistoryService(
            sp.GetRequiredService<IFileService>(),
            sp.GetService<IDirectoryChangeNotifier>(),
            sp.GetService<IFileTagService>(),
            sp.GetService<ILogger<Services.Impl.FileOperationHistoryService>>(), sp.GetRequiredService<IBatchRenameService>(), sp.GetRequiredService<IBackgroundTaskManager>(),
            sp.GetService<IAiTagService>(), sp.GetService<IPinnedFolderService>(), sp.GetRequiredService<IRatingService>(),
            sp.GetService<IFileIndexWriter>()));
        services.AddSingleton<BatchRenameOperationService>();
        services.AddSingleton<NavigationBridge>();
        services.AddSingleton<IAiTagService>(sp => new Services.Impl.AiTagService(sp.GetRequiredService<DatabaseConnectionFactory>(), sp.GetService<ILoggerFactory>()));
        services.AddSingleton<Services.Impl.PhotoLocationConsent>();
        services.AddSingleton<IImageAnalysisService, Platforms.MacCatalyst.Services.MacImageAnalysisService>();
        services.AddSingleton<IPdfTextExtractionService, Platforms.MacCatalyst.Services.MacPdfTextExtractionService>();
        services.AddSingleton<PdfAnalysisService>();
        services.AddSingleton<DocumentContentExtractor>();
        services.AddSingleton<DocumentIndexService>();
        services.AddSingleton<IDefaultAppService, Platforms.MacCatalyst.Services.MacDefaultAppService>();
        services.AddSingleton<IThemeService, Platforms.MacCatalyst.Services.MacThemeService>();
        services.AddSingleton<IInteractionStyleService, Services.Impl.InteractionStyleService>();
        services.AddSingleton<ITypographyService, Services.Impl.TypographyService>();
        services.AddSingleton<IDisplayNameService, Platforms.MacCatalyst.Services.MacDisplayNameService>();
        services.AddSingleton<HttpClient>(_ => new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30), PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(10) });
        services.AddSingleton<IAppUpdateService>(sp => AppUpdateService.Create(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<IDirectoryChangeNotifier, Services.Impl.DirectoryChangeNotifier>();
        services.AddSingleton<IFSEventsWatcher>(sp => new Platforms.MacCatalyst.Services.MacFSEventsWatcher(sp.GetRequiredService<IDirectoryChangeNotifier>()));
        services.AddSingleton<IDragDropBridge>(sp => new Platforms.MacCatalyst.Services.MacDragDropBridge(
            sp.GetRequiredService<IFileService>(),
            sp.GetRequiredService<IDirectoryChangeNotifier>(),
            sp.GetService<IRemoteFileEditService>(),
            sp.GetService<IRemoteConnectionService>(),
            sp.GetRequiredService<IFileTagService>()));
        services.AddSingleton<IVolumeMonitorService>(sp => new Platforms.MacCatalyst.Services.MacVolumeMonitorService(sp.GetRequiredService<IAiTagService>(), sp.GetService<ILoggerFactory>()?.CreateLogger<Platforms.MacCatalyst.Services.MacVolumeMonitorService>()));
        services.AddSingleton<Platforms.MacCatalyst.Services.MacDockMenuService>();
        services.AddScoped<NavigationViewModel>(sp =>
        {
            var navigation = ActivatorUtilities.CreateInstance<NavigationViewModel>(sp);
            navigation.TrackHomeUsageWith(sp.GetRequiredService<HomeWorkspaceService>());
            return navigation;
        });
        services.AddScoped<FileOpsViewModel>();
        services.AddScoped<SearchViewModel>();
        services.AddScoped<ArchiveViewModel>();
        services.AddScoped<AiViewModel>(sp => new AiViewModel(sp.GetService<IAiTagService>(), sp.GetService<IThumbnailService>(), sp.GetService<IFileIndex>(), sp.GetService<IImageAnalysisService>(), sp.GetService<IBackgroundTaskManager>(), sp.GetService<ISettingsService>(), sp.GetService<ILogger<AiViewModel>>(), sp.GetService<PdfAnalysisService>()));
        services.AddScoped<PinnedFoldersViewModel>();
        services.AddScoped<SortFilterViewModel>();
        services.AddScoped<FileListViewModel>(sp =>
        {
            var viewModel = new FileListViewModel(
                sp.GetRequiredService<NavigationViewModel>(), sp.GetRequiredService<FileOpsViewModel>(),
                sp.GetRequiredService<SearchViewModel>(), sp.GetRequiredService<ArchiveViewModel>(),
                sp.GetRequiredService<AiViewModel>(), sp.GetRequiredService<PinnedFoldersViewModel>(),
                sp.GetRequiredService<SortFilterViewModel>(), sp.GetRequiredService<IFileService>(),
                sp.GetRequiredService<IFileIndex>(), sp.GetRequiredService<IFileIndexWriter>(),
                sp.GetRequiredService<IndexConfiguration>(), sp.GetService<IContextMenuService>(),
                sp.GetService<IMetadataService>(), sp.GetService<IThumbnailService>(),
                sp.GetService<IQuickLookService>(), sp.GetService<INativeContextMenuService>(),
                sp.GetService<IClipboardService>(), sp.GetService<IApplicationLauncherService>(),
                sp.GetService<ISettingsService>(), sp.GetService<IArchiveService>(),
                sp.GetService<IDragDropBridge>(), sp.GetRequiredService<IDirectoryChangeNotifier>(),
                sp.GetService<ILoggerFactory>(), sp.GetService<IGitStatusService>(),
                sp.GetService<IDisplayNameService>(), sp.GetService<IVolumeMonitorService>(),
                sp.GetService<IRemoteConnectionService>(), sp.GetService<IRemoteFileService>(),
                sp.GetService<IRemoteFileEditService>(), sp.GetService<IOpenWithAppService>(),
                sp.GetService<IFileTagService>(), sp.GetService<Services.Plugins.PluginManager>(),
                sp.GetService<IBackgroundTaskManager>(), null,
                sp.GetService<ILocalSendService>(), airDropService: sp.GetRequiredService<IAirDropService>(),
                appCapabilitiesFactory: () => sp.GetRequiredService<Copilot.IAppCapabilityRegistry>());
            viewModel.UseColumnLayoutService(sp.GetRequiredService<FileListColumnLayoutService>());
            viewModel.UseDirectoryPreferences(sp.GetRequiredService<IDirectoryPreferencesService>());
            viewModel.UseDocumentIndex(sp.GetRequiredService<DocumentIndexService>());
            return viewModel;
        });
        services.AddScoped<MainWindowViewModel>();
        services.AddLogging(builder => { builder.AddDebug(); builder.SetMinimumLevel(LogLevel.Debug); });
        return services.BuildServiceProvider();
    }
}
