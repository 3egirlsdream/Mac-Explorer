using CommunityToolkit.Maui;
using FKFinder.Indexing;
using FKFinder.Services;
using FKFinder.ViewModels;
using Microsoft.Extensions.Logging;

#if MACCATALYST
using FKFinder.Platforms.MacCatalyst.Handlers;
using Microsoft.AspNetCore.Components.WebView.Maui;
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
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

#if MACCATALYST
        // TransparentWebViewHandler disabled - vibrancy is achieved via CSS backdrop-filter
        // which works reliably without needing native transparency
        // builder.ConfigureMauiHandlers(handlers =>
        // {
        //     handlers.AddHandler<BlazorWebView, TransparentWebViewHandler>();
        // });
#endif

        // Register indexing subsystem (with fallback if SQLite init fails)
        var indexConfig = new IndexConfiguration();
        builder.Services.AddSingleton(indexConfig);

        SqliteFileIndex? sqliteIndex = null;
        try
        {
            sqliteIndex = new SqliteFileIndex(indexConfig.DatabasePath);
            builder.Services.AddSingleton(sqliteIndex);
            builder.Services.AddSingleton<IFileIndex>(sqliteIndex);
            builder.Services.AddSingleton<IFileIndexWriter>(sqliteIndex);
        }
        catch (Exception ex)
        {
            // SQLite init failed - register null fallbacks so DI doesn't crash
            System.Diagnostics.Debug.WriteLine($"SQLite init failed: {ex.Message}");
            builder.Services.AddSingleton<IFileIndex>(_ => null!);
            builder.Services.AddSingleton<IFileIndexWriter>(_ => null!);
        }

        // Register services (platform implementations resolved via MAUI multi-targeting)
        builder.Services.AddSingleton<IFileService, Platforms.MacCatalyst.Services.MacFileService>();
        builder.Services.AddSingleton<IApplicationLauncherService, Platforms.MacCatalyst.Services.MacApplicationLauncherService>();
        builder.Services.AddSingleton<IContextMenuService, Platforms.MacCatalyst.Services.MacContextMenuService>();
        builder.Services.AddSingleton<IMetadataService, Platforms.MacCatalyst.Services.MacMetadataService>();
        builder.Services.AddSingleton<IClipboardService, Platforms.MacCatalyst.Services.MacClipboardService>();
        builder.Services.AddSingleton<ISearchService, Platforms.MacCatalyst.Services.MacSearchService>();
        builder.Services.AddSingleton<IQuickLookService, Platforms.MacCatalyst.Services.MacQuickLookService>();

        // Register ViewModels
        builder.Services.AddSingleton<FileListViewModel>();

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
