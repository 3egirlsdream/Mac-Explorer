using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacContextMenuService : IContextMenuService
{
    private readonly IApplicationLauncherService _launcher;
    private readonly IOpenWithAppService _openWithService;
    private readonly ConcurrentDictionary<string, bool> _installedApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _installChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<RegisteredApp>> _applicationsByType = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _applicationLoads = new(StringComparer.OrdinalIgnoreCase);

    public MacContextMenuService(
        IApplicationLauncherService launcher,
        IOpenWithAppService openWithService)
    {
        _launcher = launcher;
        _openWithService = openWithService;

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

    public async Task<IReadOnlyList<ContextMenuAction>> GetOpenWithActionsAsync(string filePath)
    {
        var actions = new List<ContextMenuAction>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var topLevelBundleIds = (await _openWithService.GetTopLevelAppsAsync())
            .Select(app => app.BundleId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Launch Services is authoritative. Configured apps only fill gaps.
        foreach (var app in await GetApplicationsForFileAsync(filePath))
        {
            if (topLevelBundleIds.Contains(app.BundleIdentifier) || !seen.Add(app.BundleIdentifier))
                continue;

            var systemApp = app;
            actions.Add(new ContextMenuAction
            {
                Label = systemApp.Name,
                IconSvg = Icons.Open,
                LoadIconBase64Async = CreateSystemAppIconLoader(systemApp),
                Execute = () => _launcher.OpenFileWithAppAsync(filePath, systemApp.BundleIdentifier)
            });
        }

        foreach (var app in await _openWithService.GetSubmenuAppsAsync())
        {
            if (!seen.Add(app.BundleId) || !IsAppInstalledForMenu(app.BundleId))
                continue;

            var configuredApp = app;
            actions.Add(new ContextMenuAction
            {
                Label = configuredApp.Label,
                IconSvg = Icons.Open,
                LoadIconBase64Async = () => _openWithService.GetAppIconBase64Async(configuredApp.BundleId),
                Execute = () => _launcher.OpenFileWithAppAsync(filePath, configuredApp.BundleId)
            });
        }

        return actions;
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

    public async Task<IReadOnlyList<ContextMenuAction>> GetTopLevelOpenWithActionsAsync(string path)
    {
        var actions = new List<ContextMenuAction>();
        foreach (var app in await _openWithService.GetTopLevelAppsAsync())
        {
            if (!IsAppInstalledForMenu(app.BundleId))
                continue;

            var configuredApp = app;
            actions.Add(new ContextMenuAction
            {
                Label = $"在 {configuredApp.Label} 中打开",
                IconSvg = Icons.Open,
                LoadIconBase64Async = () => _openWithService.GetAppIconBase64Async(configuredApp.BundleId),
                Execute = () => _launcher.OpenFileWithAppAsync(path, configuredApp.BundleId)
            });
        }
        return actions;
    }

    public Task<IReadOnlyList<RegisteredApp>> GetApplicationsForFileAsync(string filePath)
    {
        var cacheKey = Path.GetExtension(filePath).ToLowerInvariant();
        if (_applicationsByType.TryGetValue(cacheKey, out var cachedApps))
            return Task.FromResult(cachedApps);

        StartApplicationsLoad(cacheKey, filePath);

        IReadOnlyList<RegisteredApp> apps = GetKnownAppsForExtension(cacheKey)
            .Where(app => IsAppInstalledForMenu(app.BundleIdentifier))
            .ToList();
        return Task.FromResult(apps);
    }

    private void StartApplicationsLoad(string cacheKey, string filePath)
    {
        if (!_applicationLoads.TryAdd(cacheKey, 0)) return;

        _ = Task.Run(() =>
        {
            try
            {
                var apps = QueryLaunchServicesApplications(filePath);
                if (apps.Count > 0)
                    _applicationsByType[cacheKey] = apps;
            }
            finally
            {
                _applicationLoads.TryRemove(cacheKey, out _);
            }
        });
    }

    private static IReadOnlyList<RegisteredApp> QueryLaunchServicesApplications(string filePath)
    {
        try
        {
            var pathLiteral = JsonSerializer.Serialize(filePath);
            var script = $$"""
                ObjC.import('AppKit');
                ObjC.import('Foundation');
                var url = $.NSURL.fileURLWithPath({{pathLiteral}});
                var urls = $.NSWorkspace.sharedWorkspace.URLsForApplicationsToOpenURL(url);
                var result = [];
                for (var i = 0; i < urls.count; i++) {
                    var appUrl = urls.objectAtIndex(i);
                    var bundle = $.NSBundle.bundleWithURL(appUrl);
                    var bundleId = bundle ? ObjC.unwrap(bundle.bundleIdentifier) : '';
                    if (!bundleId) continue;
                    result.push({
                        name: ObjC.unwrap(appUrl.URLByDeletingPathExtension.lastPathComponent),
                        bundleId: bundleId,
                        appPath: ObjC.unwrap(appUrl.path)
                    });
                }
                JSON.stringify(result);
                """;

            var (exitCode, output) = RunProcess(3000, "/usr/bin/osascript", "-l", "JavaScript", "-e", script);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
                return [];

            var results = JsonSerializer.Deserialize<List<LaunchServicesApp>>(output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
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
            var (exitCode, output) = RunProcess(1500, "/usr/bin/mdfind", $"kMDItemCFBundleIdentifier == '{bundleIdentifier}'");
            if (exitCode != 0) return false;
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(path => path.EndsWith(".app", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static (int ExitCode, string Output) RunProcess(int timeoutMs, string fileName, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process == null) return (-1, string.Empty);

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                TryKillProcess(process);
                return (-1, string.Empty);
            }

            _ = errorTask.GetAwaiter().GetResult();
            return (process.ExitCode, outputTask.GetAwaiter().GetResult());
        }
        catch
        {
            return (-1, string.Empty);
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
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
