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
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MacExplorer;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private static IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize()
    {
        // Runtime XAML loading keeps startup behavior consistent across desktop platforms.
        // This ensures Application.Current is available before theme services initialize.
        AvaloniaXamlLoader.Load(this);
        Services = ConfigureServices();
        Services.GetRequiredService<IThemeService>().Initialize();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        MainWindow? mainWindow = null;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var startupPath = desktop.Args?.FirstOrDefault(Directory.Exists);
            mainWindow = CreateWindow(
                string.IsNullOrEmpty(startupPath) ? null : Path.GetFullPath(startupPath));
            desktop.MainWindow = mainWindow;

            if (ApplicationLifetime is IActivatableLifetime activatable)
                activatable.Activated += OnApplicationActivated;

        }

        base.OnFrameworkInitializationCompleted();

        if (mainWindow is { IsVisible: false })
        {
            mainWindow.Show();
            mainWindow.Activate();
        }

        if (OperatingSystem.IsMacOS())
        {
            Platforms.MacCatalyst.Services.MacApplicationIconService.Apply();
            DispatcherTimer.RunOnce(
                () => Services.GetRequiredService<Platforms.MacCatalyst.Services.MacDockMenuService>().Register(),
                TimeSpan.FromSeconds(1));
        }
    }

    public static MainWindow OpenNewWindow(string? path = null)
    {
        var window = CreateWindow(path);
        window.Show();
        window.Activate();
        return window;
    }

    private static MainWindow CreateWindow(string? path)
    {
        var scope = Services.CreateScope();
        var window = new MainWindow
        {
            DataContext = scope.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
            InitialNavigationPath = path,
        };
        window.AttachScope(scope);
        return window;
    }

    private static async void OnApplicationActivated(object? sender, ActivatedEventArgs e)
    {
        if (_desktop == null) return;

        var path = GetActivatedPath(e);
        var window = _desktop.Windows.OfType<MainWindow>().LastOrDefault(w => w.IsActive)
                     ?? _desktop.Windows.OfType<MainWindow>().LastOrDefault();

        if (window == null)
        {
            OpenNewWindow(path);
            return;
        }

        window.Show();
        window.Activate();
        if (!string.IsNullOrWhiteSpace(path))
            await window.NavigateToPathAsync(path);
    }

    private static string? GetActivatedPath(ActivatedEventArgs e)
    {
        if (e is not FileActivatedEventArgs fileActivation)
            return null;

        var localPath = fileActivation.Files
            .Select(file => file.Path.LocalPath)
            .FirstOrDefault(path => Directory.Exists(path) || File.Exists(path));

        if (string.IsNullOrWhiteSpace(localPath)) return null;
        return Directory.Exists(localPath)
            ? Path.GetFullPath(localPath)
            : Path.GetDirectoryName(Path.GetFullPath(localPath));
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        var indexConfig = new IndexConfiguration();
        services.AddSingleton(indexConfig);
        services.AddSingleton(sp => new DatabaseConnectionFactory(indexConfig.DatabasePath));
        services.AddSingleton<SqliteFileIndex>(sp => new SqliteFileIndex(indexConfig.DatabasePath, sp.GetRequiredService<DatabaseConnectionFactory>()));
        services.AddSingleton<IFileIndex>(sp => sp.GetRequiredService<SqliteFileIndex>());
        services.AddSingleton<IFileIndexWriter>(sp => sp.GetRequiredService<SqliteFileIndex>());
        services.AddSingleton<Platforms.MacCatalyst.Services.MacFileService>(sp => new Platforms.MacCatalyst.Services.MacFileService(sp.GetRequiredService<SqliteFileIndex>()));
        services.AddSingleton<IRemoteConnectionService, Services.Impl.RemoteConnectionService>();
        services.AddSingleton<SftpFileService>();
        services.AddSingleton<IRemoteFileEditService, Services.Impl.RemoteFileEditService>();
        services.AddSingleton<IFileService>(sp => new CompositeFileService(
            sp.GetRequiredService<Platforms.MacCatalyst.Services.MacFileService>(),
            sp.GetRequiredService<SftpFileService>(),
            sp.GetService<IBackgroundTaskManager>()));
        services.AddSingleton<IApplicationLauncherService, Platforms.MacCatalyst.Services.MacApplicationLauncherService>();
        services.AddSingleton<IContextMenuService, Platforms.MacCatalyst.Services.MacContextMenuService>();
        services.AddSingleton<IOpenWithAppService>(sp => new OpenWithAppService(sp.GetRequiredService<DatabaseConnectionFactory>(), sp.GetService<ILogger<OpenWithAppService>>()));
        services.AddSingleton<IMetadataService, Platforms.MacCatalyst.Services.MacMetadataService>();
        services.AddSingleton<INativeContextMenuService, Platforms.MacCatalyst.Services.MacNativeContextMenuService>();
        services.AddSingleton<IQuickLookService, Platforms.MacCatalyst.Services.MacQuickLookService>();
        services.AddSingleton<IThumbnailService, Platforms.MacCatalyst.Services.MacThumbnailService>();
        services.AddSingleton<IClipboardService, Platforms.MacCatalyst.Services.MacClipboardService>();
        services.AddSingleton<IDragDropService, Platforms.MacCatalyst.Services.MacDragDropBridge>();
        services.AddSingleton<ISearchService, Platforms.MacCatalyst.Services.MacSearchService>();
        services.AddSingleton<ISettingsService, Services.Impl.SettingsService>();
        services.AddSingleton<IRatingService, Services.Impl.RatingService>();
        services.AddSingleton<ICollectionService>(sp => new Services.Impl.CollectionService(sp.GetRequiredService<DatabaseConnectionFactory>()));
        services.AddSingleton<IFrequentFolderService>(sp => new Services.Impl.FrequentFolderService(sp.GetRequiredService<DatabaseConnectionFactory>(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        services.AddSingleton<IGitStatusService>(sp => new Services.Impl.GitStatusService(sp.GetService<ILoggerFactory>()));
        services.AddSingleton<IPinnedFolderService>(sp => new Services.Impl.PinnedFolderService(sp.GetRequiredService<DatabaseConnectionFactory>()));
        services.AddSingleton<IArchiveService, Services.Impl.ArchiveService>();
        services.AddSingleton<IBackgroundTaskManager>(sp => new Services.Impl.BackgroundTaskManager(sp.GetService<ILoggerFactory>()));
        services.AddSingleton<NavigationBridge>();
        services.AddSingleton<IAiTagService>(sp => new Services.Impl.AiTagService(sp.GetRequiredService<DatabaseConnectionFactory>(), sp.GetService<ILoggerFactory>()));
        services.AddSingleton<IImageAnalysisService, Platforms.MacCatalyst.Services.MacImageAnalysisService>();
        services.AddSingleton<IDefaultAppService, Platforms.MacCatalyst.Services.MacDefaultAppService>();
        services.AddSingleton<IThemeService, Platforms.MacCatalyst.Services.MacThemeService>();
        services.AddSingleton<IDisplayNameService, Platforms.MacCatalyst.Services.MacDisplayNameService>();
        services.AddSingleton<HttpClient>(_ => new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30), PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(10) });
        services.AddSingleton<IAppUpdateService, Services.Impl.AppUpdateService>();
        services.AddSingleton<IDirectoryChangeNotifier, Services.Impl.DirectoryChangeNotifier>();
        services.AddSingleton<IFSEventsWatcher>(sp => new Platforms.MacCatalyst.Services.MacFSEventsWatcher(sp.GetRequiredService<IDirectoryChangeNotifier>()));
        services.AddSingleton<IDragDropBridge>(sp => new Platforms.MacCatalyst.Services.MacDragDropBridge(
            sp.GetRequiredService<IFileService>(),
            sp.GetRequiredService<IDirectoryChangeNotifier>(),
            sp.GetService<IRemoteFileEditService>(),
            sp.GetService<IRemoteConnectionService>()));
        services.AddSingleton<IVolumeMonitorService>(sp => new Platforms.MacCatalyst.Services.MacVolumeMonitorService(sp.GetRequiredService<IAiTagService>(), sp.GetService<ILoggerFactory>()?.CreateLogger<Platforms.MacCatalyst.Services.MacVolumeMonitorService>()));
        services.AddSingleton<Platforms.MacCatalyst.Services.MacDockMenuService>();
        services.AddScoped<NavigationViewModel>();
        services.AddScoped<FileOpsViewModel>();
        services.AddScoped<SearchViewModel>();
        services.AddScoped<ArchiveViewModel>();
        services.AddScoped<AiViewModel>(sp => new AiViewModel(sp.GetService<IAiTagService>(), sp.GetService<IThumbnailService>(), sp.GetService<IFileIndex>(), sp.GetService<IImageAnalysisService>(), sp.GetService<IBackgroundTaskManager>(), sp.GetService<ISettingsService>(), sp.GetService<ILogger<AiViewModel>>()));
        services.AddScoped<CollectionViewModel>();
        services.AddScoped<SortFilterViewModel>();
        services.AddScoped<FileListViewModel>(sp => new FileListViewModel(sp.GetRequiredService<NavigationViewModel>(), sp.GetRequiredService<FileOpsViewModel>(), sp.GetRequiredService<SearchViewModel>(), sp.GetRequiredService<ArchiveViewModel>(), sp.GetRequiredService<AiViewModel>(), sp.GetRequiredService<CollectionViewModel>(), sp.GetRequiredService<SortFilterViewModel>(), sp.GetRequiredService<IFileService>(), sp.GetRequiredService<IFileIndex>(), sp.GetRequiredService<IFileIndexWriter>(), sp.GetRequiredService<IndexConfiguration>(), sp.GetService<IContextMenuService>(), sp.GetService<IMetadataService>(), sp.GetService<IThumbnailService>(), sp.GetService<IQuickLookService>(), sp.GetService<INativeContextMenuService>(), sp.GetService<IClipboardService>(), sp.GetService<IApplicationLauncherService>(), sp.GetService<ISettingsService>(), sp.GetService<IArchiveService>(), sp.GetService<IDragDropBridge>(), sp.GetRequiredService<IDirectoryChangeNotifier>(), sp.GetService<ILoggerFactory>(), sp.GetService<IGitStatusService>(), sp.GetService<IDisplayNameService>(), sp.GetService<IVolumeMonitorService>(), sp.GetService<IRemoteConnectionService>(), sp.GetService<SftpFileService>(), sp.GetService<IRemoteFileEditService>(), sp.GetService<IOpenWithAppService>()));
        services.AddScoped<MainWindowViewModel>();
        services.AddLogging(builder => { builder.AddDebug(); builder.SetMinimumLevel(LogLevel.Debug); });
        return services.BuildServiceProvider();
    }
}
