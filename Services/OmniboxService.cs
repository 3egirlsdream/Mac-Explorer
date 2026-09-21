using MacExplorer.Models;
using MacExplorer.ViewModels;
using AppIcons = MacExplorer.Assets.Icons;

namespace MacExplorer.Services;

public static class OmniboxService
{
    private static readonly List<IOmniboxProvider> _providers = [];

    /// <summary>Register an omnibox provider. Called at startup from App.axaml.cs.</summary>
    public static void RegisterProvider(IOmniboxProvider provider)
    {
        if (!_providers.Contains(provider))
            _providers.Add(provider);
    }

    /// <summary>Clear all registered providers (for testing).</summary>
    public static void ClearProviders() => _providers.Clear();

    public static async Task<IReadOnlyList<OmniboxSuggestion>> GetSuggestionsAsync(
        FileListViewModel viewModel,
        string? input,
        CancellationToken cancellationToken)
    {
        var value = input?.Trim() ?? string.Empty;

        // If no providers registered, return empty (providers are registered at startup)
        if (_providers.Count == 0)
            return [];

        var allSuggestions = new List<OmniboxSuggestion>();
        var seenValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providers.OrderBy(p => p.Priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var results = await provider.GetSuggestionsAsync(viewModel, value, cancellationToken);
                foreach (var suggestion in results)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Deduplicate by Value (path or command title)
                    if (seenValues.Add(suggestion.Value))
                        allSuggestions.Add(suggestion);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* A failing provider should not break the entire palette */ }
        }

        return allSuggestions;
    }

    public static async Task ExecuteAsync(
        FileListViewModel viewModel,
        OmniboxSuggestion suggestion)
    {
        // If the suggestion has an explicit execute action (e.g., commands), run it
        if (suggestion.ExecuteAction != null)
        {
            await suggestion.ExecuteAction();
            return;
        }

        // Result: reveal the file in the list
        if (suggestion.Kind == OmniboxSuggestionKind.Result && suggestion.Entry != null)
        {
            await viewModel.RevealFileAsync(suggestion.Entry);
            return;
        }

        // Path or RecentDirectory: navigate to the path
        await NavigateToPathAsync(viewModel, suggestion.Value);
    }

    public static async Task ExecuteInputAsync(
        FileListViewModel viewModel,
        string input)
    {
        var value = input.Trim();
        var path = NormalizePath(value);
        if (IsNavigablePath(path))
            await NavigateToPathAsync(viewModel, value);
        else
            await viewModel.SearchAsync(value);
    }

    // ── Static helpers used by ExecuteAsync / ExecuteInputAsync ──

    internal static bool IsNavigablePath(string path)
        => Directory.Exists(path) || File.Exists(path) || VirtualPath.IsRemotePath(path);

    internal static string NormalizePath(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
            value = uri.LocalPath;
        else
        {
            try { value = Uri.UnescapeDataString(value); }
            catch (UriFormatException) { }
        }

        if (value.StartsWith('~'))
        {
            var home = RuntimePaths.HomeDirectory;
            value = home + value[1..];
        }

        return value;
    }

    private static async Task NavigateToPathAsync(
        FileListViewModel viewModel,
        string value)
    {
        var path = NormalizePath(value);
        if (viewModel.IsRemoteView
            && !VirtualPath.IsRemotePath(path)
            && viewModel.CurrentRemoteServerId != null)
        {
            path = VirtualPath.BuildRemotePath(viewModel.CurrentRemoteServerId, path);
        }

        if (VirtualPath.IsRemotePath(path))
        {
            await viewModel.NavigateToAsync(path);
            return;
        }

        if (Directory.Exists(path))
            await viewModel.NavigateToAsync(Path.GetFullPath(path));
        else if (File.Exists(path))
            await viewModel.RevealFileAsync(new FileSystemEntry
            {
                FullPath = Path.GetFullPath(path),
                Name = Path.GetFileName(path)
            });
    }
}
