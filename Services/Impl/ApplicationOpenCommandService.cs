using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using MacExplorer.Models;
using MacExplorer.ViewModels;
using AppIcons = MacExplorer.Assets.Icons;

namespace MacExplorer.Services.Impl;

public sealed class ApplicationOpenCommandService : IDisposable
{
    private readonly Func<Task<List<AppListItem>>> _getApps;
    private readonly IApplicationLauncherService _launcher;
    private readonly Func<AppListItem, Task<string?>>? _getIcon;
    private readonly ConcurrentDictionary<string, Lazy<Bitmap?>> _icons = new(StringComparer.Ordinal);

    public ApplicationOpenCommandService(IOpenWithAppService apps, IApplicationLauncherService launcher)
        : this(apps.GetInstalledAppsAsync, launcher, app => apps.GetAppIconBase64ByPathAsync(app.AppPath)) { }

    internal ApplicationOpenCommandService(Func<Task<List<AppListItem>>> getApps, IApplicationLauncherService launcher,
        Func<AppListItem, Task<string?>>? getIcon = null)
        => (_getApps, _launcher, _getIcon) = (getApps, launcher, getIcon);

    internal static bool TryParse(string? input, out string application)
    {
        var value = input?.Trim() ?? string.Empty;
        application = string.Empty;
        if (!value.StartsWith("以", StringComparison.Ordinal) || !value.EndsWith("打开", StringComparison.Ordinal))
            return false;
        application = value[1..^2].Trim();
        return application.Length > 0;
    }

    public async Task<IReadOnlyList<OmniboxSuggestion>> GetSuggestionsAsync(
        FileListViewModel viewModel, string input, CancellationToken cancellationToken)
    {
        if (!TryParse(input, out var application))
        {
            application = input.Trim();
            if (application.Length == 0 || application.Contains('/') || application.StartsWith('~')
                || OmniboxService.IsNavigablePath(OmniboxService.NormalizePath(application)))
                return [];
        }
        var apps = await _getApps().WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var matches = Match(apps, application);
        return await Task.WhenAll(matches.Select(async app => new OmniboxSuggestion(
            OmniboxSuggestionKind.Application, LocalizationText.Get("在 {0} 中打开", app.Name), app.BundleId,
            app.BundleId, AppIcons.Apps, "#7C8798",
            ExecuteAction: () => ExecuteAsync(viewModel, app),
            IconImage: await GetIconAsync(app, cancellationToken))));
    }

    private async Task<Bitmap?> GetIconAsync(AppListItem app, CancellationToken cancellationToken)
    {
        try
        {
            var base64 = app.IconBase64;
            if (string.IsNullOrWhiteSpace(base64) && _getIcon != null)
                base64 = await _getIcon(app).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(base64)) return null;
            var payload = base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? base64[(base64.IndexOf(',') + 1)..] : base64;
            return await Task.Run(() => _icons.GetOrAdd(payload, data => new Lazy<Bitmap?>(() =>
            {
                try
                {
                    using var stream = new MemoryStream(Convert.FromBase64String(data));
                    return new Bitmap(stream);
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
                {
                    return null;
                }
            })).Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
            if (icon.IsValueCreated) icon.Value?.Dispose();
        _icons.Clear();
    }

    internal static IReadOnlyList<AppListItem> Match(IEnumerable<AppListItem> apps, string query)
    {
        var installed = apps.ToArray();
        var exactBundles = installed.Where(app => app.BundleId.Equals(query, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(app => app.BundleId, StringComparer.OrdinalIgnoreCase).ToArray();
        if (exactBundles.Length > 0) return exactBundles;
        return installed.Select(app => (App: app, Rank: MatchRank(app, query)))
            .Where(item => item.Rank >= 0).OrderBy(item => item.Rank)
            .ThenBy(item => item.App.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.App.BundleId, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(item => item.App.BundleId, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.App).ToArray();
    }

    private static int MatchRank(AppListItem app, string query)
    {
        var names = app.Names.Concat([app.Name, Path.GetFileName(app.AppPath), Path.GetFileNameWithoutExtension(app.AppPath), app.BundleId])
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        if (names.Any(name => name.Equals(query, StringComparison.OrdinalIgnoreCase))) return 0;
        var aliases = app.BundleId.ToLowerInvariant() switch
        {
            "com.apple.terminal" => new[] { "终端", "terminal" },
            "com.microsoft.vscode" => new[] { "vscode", "code", "Visual Studio Code", "VS Code" },
            "com.microsoft.vscodeinsiders" => new[] { "vscode insiders", "code insiders" },
            "com.googlecode.iterm2" => new[] { "iterm", "iterm2" },
            "com.sublimetext.4" or "com.sublimetext.3" => new[] { "sublime", "sublime text", "st" },
            _ => Array.Empty<string>()
        };
        if (aliases.Any(alias => alias.Equals(query, StringComparison.OrdinalIgnoreCase))) return 1;
        if (names.Any(name => Abbreviation(name).Equals(query, StringComparison.OrdinalIgnoreCase))) return 2;
        return names.Concat(aliases).Any(name => name.Contains(query, StringComparison.OrdinalIgnoreCase)) ? 3 : -1;
    }

    private static string Abbreviation(string name)
        => string.Concat(name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word[0]));

    internal static bool HasLocalDirectory(FileListViewModel viewModel)
        => !viewModel.IsHomePage && !viewModel.IsRemoteView && !viewModel.IsArchiveView
            && !viewModel.IsAiView && !viewModel.IsTagView
            && Path.IsPathFullyQualified(viewModel.CurrentPath) && Directory.Exists(viewModel.CurrentPath);

    internal async Task ExecuteAsync(FileListViewModel viewModel, AppListItem app)
    {
        if (!HasLocalDirectory(viewModel))
            throw new InvalidOperationException(LocalizationText.Get("请先进入本地目录"));
        var path = viewModel.CurrentPath;
        await (app.BundleId.ToLowerInvariant() switch
        {
            "com.apple.terminal" => _launcher.OpenInTerminalAsync(path),
            "com.microsoft.vscode" => _launcher.OpenInEditorAsync(path, "code", app.BundleId),
            "com.microsoft.vscodeinsiders" => _launcher.OpenInEditorAsync(path, "code-insiders", app.BundleId),
            _ => _launcher.OpenFileWithAppAsync(path, app.BundleId)
        });
        viewModel.StatusText = LocalizationText.Get("已在 {0} 中打开", app.Name);
    }
}
