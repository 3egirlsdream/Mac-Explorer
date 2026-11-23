using System.Collections.Concurrent;
using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacContextMenuService : IContextMenuService
{
    private readonly IApplicationLauncherService _launcher;
    private readonly IOpenWithAppService _openWithService;
    private readonly Func<string, IReadOnlyList<RegisteredApp>> _queryRegisteredApplications;
    private readonly Func<string, string?> _queryDefaultApplicationPath;
    private readonly ConcurrentDictionary<string, bool> _installedApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _installChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Extension, bool IsDirectory), IReadOnlyList<RegisteredApp>> _applicationsByType = new();

    public MacContextMenuService(
        IApplicationLauncherService launcher,
        IOpenWithAppService openWithService)
        : this(launcher, openWithService, QueryLaunchServicesApplications)
    {
    }

    internal MacContextMenuService(
        IApplicationLauncherService launcher,
        IOpenWithAppService openWithService,
        Func<string, IReadOnlyList<RegisteredApp>> queryRegisteredApplications,
        Func<string, string?>? queryDefaultApplicationPath = null)
    {
        _launcher = launcher;
        _openWithService = openWithService;
        _queryRegisteredApplications = queryRegisteredApplications;
        _queryDefaultApplicationPath = queryDefaultApplicationPath ?? Platforms.MacOS.MacSandboxNative.DefaultApplication;

        foreach (var bundleId in new[]
        {
            "com.apple.TextEdit",
            "com.apple.Preview",
            "com.apple.QuickTimePlayerX",
            "com.apple.dt.Xcode",
            "com.microsoft.VSCode",
            "com.todesktop.230313mzl4w4u92",
            "org.videolan.vlc"
        })
        {
            _ = _openWithService.GetAppIconBase64Async(bundleId);
            StartAppInstalledCheck(bundleId);
        }

        _ = Task.Run(async () =>
        {
            foreach (var app in await _openWithService.GetAllAsync())
                StartAppInstalledCheck(app.BundleId);
        });
    }

    public Task<IReadOnlyList<ContextMenuAction>> GetFileContextMenuActionsAsync(FileSystemEntry entry)
        => Task.FromResult<IReadOnlyList<ContextMenuAction>>([]);

    public Task<IReadOnlyList<ContextMenuAction>> GetBackgroundContextMenuActionsAsync(string currentDirectory)
        => Task.FromResult<IReadOnlyList<ContextMenuAction>>([]);

    public Task<IReadOnlyList<ContextMenuAction>> GetTrashFileContextMenuActionsAsync(FileSystemEntry entry)
        => Task.FromResult<IReadOnlyList<ContextMenuAction>>([]);

    public Task<IReadOnlyList<ContextMenuAction>> GetTrashBackgroundContextMenuActionsAsync()
        => Task.FromResult<IReadOnlyList<ContextMenuAction>>([]);

    public Task<IReadOnlyList<ContextMenuAction>> GetOpenWithActionsAsync(string filePath)
        => GetOpenWithActionsAsync(filePath, null);

    public async Task<IReadOnlyList<ContextMenuAction>> GetOpenWithActionsAsync(string filePath, IReadOnlyList<string>? allPaths)
    {
        var targetPaths = allPaths is { Count: > 0 } ? allPaths : [filePath];
        var actions = new List<ContextMenuAction>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var topLevelBundleIds = (await _openWithService.GetTopLevelAppsAsync())
            .Select(app => app.BundleId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recommendations = await Task.Run(() => GetRecommendedApplications(targetPaths));
        foreach (var (app, paths) in recommendations.Values)
        {
            if (topLevelBundleIds.Contains(app.BundleIdentifier))
                continue;

            seen.Add(app.BundleIdentifier);
            actions.Add(new ContextMenuAction
            {
                Label = app.Name,
                IconSvg = Icons.Open,
                LoadIconBase64Async = CreateSystemAppIconLoader(app),
                Execute = () => _launcher.OpenFilesWithAppAsync(paths, app.BundleIdentifier)
            });
        }

        foreach (var app in await _openWithService.GetSubmenuAppsAsync())
        {
            if (BuiltInOpenWithActions.IsBuiltIn(app.BundleId))
                continue;
            if (!seen.Add(app.BundleId) || !IsAppInstalledForMenu(app.BundleId))
                continue;

            var configuredApp = app;
            actions.Add(new ContextMenuAction
            {
                Label = configuredApp.Label,
                IconSvg = Icons.Open,
                LoadIconBase64Async = () => _openWithService.GetAppIconBase64Async(configuredApp.BundleId),
                Execute = () => _launcher.OpenFilesWithAppAsync(targetPaths, configuredApp.BundleId)
            });
        }

        return actions;
    }

    private Dictionary<string, (RegisteredApp App, List<string> Paths)> GetRecommendedApplications(
        IReadOnlyList<string> targetPaths)
    {
        var recommendations = new Dictionary<string, (RegisteredApp App, List<string> Paths)>(
            StringComparer.OrdinalIgnoreCase);
        var groups = targetPaths.Distinct(StringComparer.Ordinal).GroupBy(path =>
        {
            var type = GetApplicationType(path);
            return (Type: type, UntypedFile: type is { Extension.Length: 0, IsDirectory: false } ? path : null);
        });
        foreach (var group in groups)
        {
            foreach (var app in GetApplicationsForFile(group.First(), group.Key.Type))
            {
                if (!recommendations.TryGetValue(app.BundleIdentifier, out var match))
                {
                    match = (app, []);
                    recommendations.Add(app.BundleIdentifier, match);
                }
                match.Paths.AddRange(group);
            }
        }
        return recommendations;
    }

    private Func<Task<string?>> CreateSystemAppIconLoader(RegisteredApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.IconPath))
        {
            var appPath = app.IconPath;
            return () => _openWithService.GetAppIconBase64ByPathAsync(appPath);
        }

        var bundleIdentifier = app.BundleIdentifier;
        return () => _openWithService.GetAppIconBase64Async(bundleIdentifier);
    }

    public Task<IReadOnlyList<ContextMenuAction>> GetTopLevelOpenWithActionsAsync(string path)
        => GetTopLevelOpenWithActionsAsync(path, null);

    public async Task<IReadOnlyList<ContextMenuAction>> GetTopLevelOpenWithActionsAsync(string path, IReadOnlyList<string>? allPaths)
    {
        var targetPaths = allPaths is { Count: > 0 } ? allPaths : [path];
        var actions = new List<ContextMenuAction>();
        foreach (var app in await _openWithService.GetTopLevelAppsAsync())
        {
            if (BuiltInOpenWithActions.IsBuiltIn(app.BundleId) || !IsAppInstalledForMenu(app.BundleId))
                continue;

            var configuredApp = app;
            actions.Add(new ContextMenuAction
            {
                Label = MacExplorer.Services.Impl.LocalizationText.Get("在 {0} 中打开", configuredApp.DisplayLabel),
                IconSvg = Icons.Open,
                LoadIconBase64Async = () => _openWithService.GetAppIconBase64Async(configuredApp.BundleId),
                Execute = () => _launcher.OpenFilesWithAppAsync(targetPaths, configuredApp.BundleId)
            });
        }
        return actions;
    }

    public Task<IReadOnlyList<RegisteredApp>> GetApplicationsForFileAsync(string filePath)
        => Task.Run(() => GetApplicationsForFile(filePath, GetApplicationType(filePath)));

    private static (string Extension, bool IsDirectory) GetApplicationType(string filePath)
        => (Path.GetExtension(filePath).ToLowerInvariant(), Directory.Exists(filePath));

    private IReadOnlyList<RegisteredApp> GetApplicationsForFile(string filePath, (string Extension, bool IsDirectory) type)
    {
        var cacheable = type.Extension.Length > 0 || type.IsDirectory;
        if (cacheable && _applicationsByType.TryGetValue(type, out var cachedApps))
            return cachedApps;

        var apps = _queryRegisteredApplications(filePath);
        if (apps.Count > 0)
            return cacheable ? _applicationsByType.GetOrAdd(type, apps) : apps;

        return type.IsDirectory ? [] : GetKnownAppsForExtension(type.Extension)
            .Where(app => IsAppInstalledForMenu(app.BundleIdentifier))
            .ToList();
    }

    public async Task<ContextMenuIconGroup> GetDefaultApplicationIconsAsync(IReadOnlyList<string> filePaths)
    {
        const int iconLimit = 3;
        var appPaths = await Task.Run(() => filePaths.Select(_queryDefaultApplicationPath)
            .OfType<string>()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Take(iconLimit + 1)
            .ToArray()).ConfigureAwait(false);
        var icons = await Task.WhenAll(appPaths.Take(iconLimit).Select(_openWithService.GetAppIconBase64ByPathAsync))
            .ConfigureAwait(false);
        return new ContextMenuIconGroup(icons.OfType<string>().Where(icon => icon.Length > 0).ToArray(),
            appPaths.Length > iconLimit);
    }

    private static IReadOnlyList<RegisteredApp> QueryLaunchServicesApplications(string filePath)
    {
        try
        {
            var results = JsonSerializer.Deserialize<List<LaunchServicesApp>>(
                Platforms.MacOS.MacSandboxNative.RegisteredApplications(filePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            return results
                .Where(app => !string.IsNullOrWhiteSpace(app.BundleId))
                .DistinctBy(app => app.BundleId, StringComparer.OrdinalIgnoreCase)
                .Select((app, index) => new RegisteredApp
                {
                    Name = app.Name,
                    BundleIdentifier = app.BundleId,
                    IconPath = app.AppPath,
                    IsDefault = index == 0
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private sealed class LaunchServicesApp
    {
        public string Name { get; set; } = string.Empty;
        public string BundleId { get; set; } = string.Empty;
        public string AppPath { get; set; } = string.Empty;
    }

    public bool IsAppInstalled(string bundleIdentifier)
    {
        return _installedApps.GetOrAdd(bundleIdentifier, DetectAppInstalled);
    }

    private bool IsAppInstalledForMenu(string bundleIdentifier)
    {
        if (_installedApps.TryGetValue(bundleIdentifier, out var installed))
            return installed;

        StartAppInstalledCheck(bundleIdentifier);
        return true;
    }

    private void StartAppInstalledCheck(string bundleIdentifier)
    {
        if (!_installChecks.TryAdd(bundleIdentifier, 0)) return;

        _ = Task.Run(() =>
        {
            try
            {
                _installedApps[bundleIdentifier] = DetectAppInstalled(bundleIdentifier);
            }
            finally
            {
                _installChecks.TryRemove(bundleIdentifier, out _);
            }
        });
    }

    private static bool DetectAppInstalled(string bundleIdentifier)
    {
        try
        {
            return Platforms.MacOS.MacSandboxNative.ApplicationPath(bundleIdentifier) != null;
        }
        catch
        {
            return false;
        }
    }





    private static IReadOnlyList<RegisteredApp> GetKnownAppsForExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".txt" or ".md" or ".log" or ".csv" =>
            [
                new() { Name = "文本编辑", BundleIdentifier = "com.apple.TextEdit", IsDefault = true },
                new() { Name = "Visual Studio Code", BundleIdentifier = "com.microsoft.VSCode" }
            ],
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".tiff" or ".webp" or ".svg" or ".heic" =>
            [
                new() { Name = "预览", BundleIdentifier = "com.apple.Preview", IsDefault = true },
                new() { Name = "Visual Studio Code", BundleIdentifier = "com.microsoft.VSCode" }
            ],
            ".pdf" => [new() { Name = "预览", BundleIdentifier = "com.apple.Preview", IsDefault = true }],
            ".mp4" or ".mov" or ".avi" or ".mkv" =>
            [
                new() { Name = "QuickTime Player", BundleIdentifier = "com.apple.QuickTimePlayerX", IsDefault = true },
                new() { Name = "VLC", BundleIdentifier = "org.videolan.vlc" }
            ],
            ".html" or ".css" or ".js" or ".ts" or ".py" or ".java" or ".cs" or ".go" or ".rs" or ".swift" or ".json" or ".xml" =>
            [
                new() { Name = "Visual Studio Code", BundleIdentifier = "com.microsoft.VSCode", IsDefault = true },
                new() { Name = "Cursor", BundleIdentifier = "com.todesktop.230313mzl4w4u92" },
                new() { Name = "Xcode", BundleIdentifier = "com.apple.dt.Xcode" },
                new() { Name = "文本编辑", BundleIdentifier = "com.apple.TextEdit" }
            ],
            _ => []
        };
    }
}
