using CommunityToolkit.Maui;
using FKFinder.Indexing;
using FKFinder.Services;
using FKFinder.ViewModels;
using Microsoft.Extensions.Logging;
using Masa.Blazor;
using Xe.AcrylicView;

#if MACCATALYST
using FKFinder.Platforms.MacCatalyst.Handlers;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.LifecycleEvents;
using UIKit;
#endif

namespace FKFinder;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .UseAcrylicView()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

#if MACCATALYST
        // 配置生命周期事件，使窗口透明
        builder.ConfigureLifecycleEvents(events =>
        {
            events.AddiOS(ios => ios.FinishedLaunching((app, options) =>
            {
                var window = app.KeyWindow;
                if (window != null)
                {
                    window.BackgroundColor = UIColor.Clear;
                    window.MakeKeyAndVisible();
                }
                return true;
            }));
        });

        // Enable transparent WebView
        builder.ConfigureMauiHandlers(handlers =>
        {
            handlers.AddHandler<BlazorWebView, TransparentWebViewHandler>();
        });

        // ── 从根源拦截 MAUI 设置背景色 ──
        // 用 ModifyMapping 替换 PageHandler 默认的 MapBackground，
        // 跳过 MAUI 默认逻辑，强制透明。这样 MAUI 无论何时尝试
        // 设置背景色都会被拦截。
        Microsoft.Maui.Handlers.PageHandler.Mapper.ModifyMapping(
            nameof(Microsoft.Maui.IContentView.Background),
            (handler, view, action) =>
            {
                // 不调用 action(handler, view) —— 跳过 MAUI 默认的 MapBackground
                // 强制 ViewController.View 和 PlatformView 为透明
                if (handler is Microsoft.Maui.IPlatformViewHandler pvh
                    && pvh.ViewController?.View is UIView vcView)
                {
                    vcView.BackgroundColor = UIColor.Clear;
                    vcView.Opaque = false;
                }
                if (handler.PlatformView is UIView nativeView)
                {
                    nativeView.BackgroundColor = UIColor.Clear;
                    nativeView.Opaque = false;
                }
            });

        // 同样拦截 ContentView 的 Background 映射
        Microsoft.Maui.Handlers.ContentViewHandler.Mapper.ModifyMapping(
            nameof(Microsoft.Maui.IContentView.Background),
            (handler, view, action) =>
            {
                if (handler.PlatformView is UIView nativeView)
                {
                    nativeView.BackgroundColor = UIColor.Clear;
                    nativeView.Opaque = false;
                }
            });

        // 拦截 Window 的 Background 映射
        Microsoft.Maui.Handlers.WindowHandler.Mapper.ModifyMapping(
            "Background",
            (handler, view, action) =>
            {
                // 跳过默认映射，不设置窗口背景色
            });
#endif

        // Register indexing subsystem — always succeeds (auto-recreates on corruption)
        var indexConfig = new IndexConfiguration();
        builder.Services.AddSingleton(indexConfig);

        // Register shared database connection factory
        builder.Services.AddSingleton(sp =>
            new Services.Impl.DatabaseConnectionFactory(indexConfig.DatabasePath));

        builder.Services.AddSingleton<SqliteFileIndex>(sp =>
            new SqliteFileIndex(
                indexConfig.DatabasePath,
                sp.GetRequiredService<Services.Impl.DatabaseConnectionFactory>()));
        builder.Services.AddSingleton<IFileIndex>(sp => sp.GetRequiredService<SqliteFileIndex>());
        builder.Services.AddSingleton<IFileIndexWriter>(sp => sp.GetRequiredService<SqliteFileIndex>());

        // Register services (platform implementations resolved via MAUI multi-targeting)
        builder.Services.AddSingleton<IFileService>(sp =>
            new Platforms.MacCatalyst.Services.MacFileService(sp.GetRequiredService<SqliteFileIndex>()));
        builder.Services.AddSingleton<IApplicationLauncherService, Platforms.MacCatalyst.Services.MacApplicationLauncherService>();
        builder.Services.AddSingleton<IContextMenuService, Platforms.MacCatalyst.Services.MacContextMenuService>();
        // [Web ContextMenu] Mac原生菜单已屏蔽，使用Web实现
        // builder.Services.AddSingleton<INativeContextMenuService, Platforms.MacCatalyst.Services.MacNativeContextMenuService>();
        builder.Services.AddSingleton<IMetadataService, Platforms.MacCatalyst.Services.MacMetadataService>();
        builder.Services.AddSingleton<IClipboardService, Platforms.MacCatalyst.Services.MacClipboardService>();
        builder.Services.AddSingleton<ISearchService, Platforms.MacCatalyst.Services.MacSearchService>();
        builder.Services.AddSingleton<IQuickLookService, Platforms.MacCatalyst.Services.MacQuickLookService>();
        builder.Services.AddSingleton<IMouseNavigationService, Platforms.MacCatalyst.Services.MacMouseNavigationService>();
        builder.Services.AddSingleton<IThumbnailService, Platforms.MacCatalyst.Services.MacThumbnailService>();
        builder.Services.AddSingleton<ICollectionService>(sp =>
            new Services.Impl.CollectionService(
                sp.GetRequiredService<Services.Impl.DatabaseConnectionFactory>(),
                sp.GetService<ILoggerFactory>()));
        builder.Services.AddSingleton<IRatingService>(sp =>
            new Services.Impl.RatingService(sp.GetRequiredService<Services.Impl.DatabaseConnectionFactory>()));
        builder.Services.AddSingleton<ISettingsService>(sp =>
            new Services.Impl.SettingsService(
                sp.GetRequiredService<Services.Impl.DatabaseConnectionFactory>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<Services.Impl.SettingsService>()));
        builder.Services.AddSingleton<IFrequentFolderService>(sp =>
            new Services.Impl.FrequentFolderService(
                sp.GetRequiredService<Services.Impl.DatabaseConnectionFactory>(),
                sp.GetRequiredService<IFileService>().HomeDirectory,
                sp.GetService<ILoggerFactory>()));
        builder.Services.AddSingleton<IPinnedFolderService>(sp =>
            new Services.Impl.PinnedFolderService(sp.GetRequiredService<Services.Impl.DatabaseConnectionFactory>()));
        builder.Services.AddSingleton<IArchiveService, Services.Impl.ArchiveService>();
        builder.Services.AddSingleton<IBackgroundTaskManager>(sp =>
            new Services.Impl.BackgroundTaskManager(sp.GetService<ILoggerFactory>()));
        builder.Services.AddSingleton<NavigationBridge>();
        builder.Services.AddSingleton<IAiTagService>(sp =>
            new Services.Impl.AiTagService(
                sp.GetRequiredService<Services.Impl.DatabaseConnectionFactory>(),
                sp.GetService<ILoggerFactory>()));
        builder.Services.AddSingleton<IImageAnalysisService,
            Platforms.MacCatalyst.Services.MacImageAnalysisService>();
        builder.Services.AddSingleton<IDefaultAppService, Platforms.MacCatalyst.Services.MacDefaultAppService>();

        // Unified refresh pipeline
        builder.Services.AddSingleton<IDirectoryChangeNotifier, Services.Impl.DirectoryChangeNotifier>();
        builder.Services.AddSingleton<IFSEventsWatcher>(sp =>
            new Platforms.MacCatalyst.Services.MacFSEventsWatcher(
                sp.GetRequiredService<IDirectoryChangeNotifier>()));
        builder.Services.AddSingleton<IDragDropBridge>(sp =>
            new Platforms.MacCatalyst.Services.MacDragDropBridge(
                sp.GetRequiredService<IFileService>(),
                sp.GetRequiredService<IDirectoryChangeNotifier>()));

        // Register sub-viewmodels (Scoped so each window gets its own instance)
        builder.Services.AddScoped<NavigationViewModel>();
        builder.Services.AddScoped<FileOpsViewModel>();
        builder.Services.AddScoped<SearchViewModel>();
        builder.Services.AddScoped<ArchiveViewModel>();
        builder.Services.AddScoped<AiViewModel>(sp => new AiViewModel(
            sp.GetService<IAiTagService>(),
            sp.GetService<IThumbnailService>(),
            sp.GetService<IFileIndex>(),
            sp.GetService<IImageAnalysisService>(),
            sp.GetService<IBackgroundTaskManager>(),
            sp.GetService<ISettingsService>(),
            sp.GetService<ILogger<AiViewModel>>()
        ));
        builder.Services.AddScoped<CollectionViewModel>();
        builder.Services.AddScoped<SortFilterViewModel>();

        // FileListViewModel factory - receives all sub-viewmodels and core services
        builder.Services.AddScoped<FileListViewModel>(sp => new FileListViewModel(
            sp.GetRequiredService<NavigationViewModel>(),
            sp.GetRequiredService<FileOpsViewModel>(),
            sp.GetRequiredService<SearchViewModel>(),
            sp.GetRequiredService<ArchiveViewModel>(),
            sp.GetRequiredService<AiViewModel>(),
            sp.GetRequiredService<CollectionViewModel>(),
            sp.GetRequiredService<SortFilterViewModel>(),
            sp.GetRequiredService<IFileService>(),
            sp.GetRequiredService<IFileIndex>(),
            sp.GetRequiredService<IFileIndexWriter>(),
            sp.GetRequiredService<IndexConfiguration>(),
            sp.GetService<IContextMenuService>(),
            sp.GetService<IMetadataService>(),
            sp.GetService<IThumbnailService>(),
            sp.GetService<INativeContextMenuService>(),
            sp.GetService<IClipboardService>(),
            sp.GetService<IApplicationLauncherService>(),
            sp.GetService<ISettingsService>(),
            sp.GetService<IArchiveService>(),
            sp.GetService<IDragDropBridge>(),
            sp.GetRequiredService<IDirectoryChangeNotifier>(),
            sp.GetService<ILoggerFactory>()
        ));

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddMasaBlazor();

        builder.Logging.AddDebug();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        return builder.Build();
    }
}
