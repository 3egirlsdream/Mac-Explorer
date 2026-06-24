using MacExplorer.Models;
using MacExplorer.ViewModels;
using AppIcons = MacExplorer.Assets.Icons;

namespace MacExplorer.Services;

public static class OmniboxService
{
    private const int MaxPathSuggestions = 6;
    private const int MaxSearchSuggestions = 10;

    public static async Task<IReadOnlyList<OmniboxSuggestion>> GetSuggestionsAsync(
        FileListViewModel viewModel,
        string? input,
        CancellationToken cancellationToken)
    {
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
            return [];

        var suggestions = await Task.Run(
            () => GetPathSuggestions(
                value,
                viewModel.HomeDirectory,
                cancellationToken),
            cancellationToken);
        var seenPaths = new HashSet<string>(
            suggestions.Select(suggestion => suggestion.Value),
            StringComparer.OrdinalIgnoreCase);

        var results = await viewModel.GetSearchSuggestionsAsync(
            value,
            MaxSearchSuggestions,
            cancellationToken);

        foreach (var entry in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seenPaths.Add(entry.FullPath))
                suggestions.Add(CreateResultSuggestion(entry));
        }

        return suggestions;
    }

    public static async Task ExecuteAsync(
        FileListViewModel viewModel,
        OmniboxSuggestion suggestion)
    {
        if (suggestion.Kind == OmniboxSuggestionKind.Result && suggestion.Entry != null)
        {
            await viewModel.RevealFileAsync(suggestion.Entry);
            return;
        }

        await NavigateToPathAsync(viewModel, suggestion.Value);
    }

    public static async Task ExecuteInputAsync(
        FileListViewModel viewModel,
        string input)
    {
        var value = input.Trim();
        var path = NormalizePath(value);
        if (IsNavigablePath(path))
            await NavigateToPathAsync(viewModel, path);
        else
            await viewModel.SearchAsync(value);
    }

    private static List<OmniboxSuggestion> GetPathSuggestions(
        string value,
        string homeDirectory,
        CancellationToken cancellationToken)
    {
        var suggestions = new List<OmniboxSuggestion>();
        var path = NormalizePath(value);
        if (IsNavigablePath(path))
            suggestions.Add(CreatePathSuggestion(path));

        if (!LooksLikePath(value))
        {
            AddFuzzyDirectoryMatches(
                homeDirectory,
                value,
                suggestions,
                cancellationToken);
            return suggestions;
        }

        var endsWithSeparator = path.EndsWith(Path.DirectorySeparatorChar)
                                || path.EndsWith(Path.AltDirectorySeparatorChar);
        var directory = endsWithSeparator ? path : Path.GetDirectoryName(path);
        var prefix = endsWithSeparator ? string.Empty : Path.GetFileName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return suggestions;

        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(directory)
                         .Where(candidate => Path.GetFileName(candidate)
                             .Contains(prefix, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(candidate => Path.GetFileName(candidate), StringComparer.OrdinalIgnoreCase)
                         .Take(MaxPathSuggestions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(candidate, path, StringComparison.Ordinal))
                    suggestions.Add(CreatePathSuggestion(candidate));
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        return suggestions;
    }

    private static void AddFuzzyDirectoryMatches(
        string root,
        string value,
        ICollection<OmniboxSuggestion> suggestions,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            return;

        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(root)
                         .Where(candidate => Path.GetFileName(candidate)
                             .Contains(value, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(candidate => Path.GetFileName(candidate), StringComparer.OrdinalIgnoreCase)
                         .Take(MaxPathSuggestions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                suggestions.Add(CreatePathSuggestion(candidate));
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static OmniboxSuggestion CreatePathSuggestion(string path)
    {
        var normalized = VirtualPath.IsRemotePath(path) ? path : Path.GetFullPath(path);
        var title = VirtualPath.IsRemotePath(path)
            ? path
            : normalized == Path.GetPathRoot(normalized)
                ? normalized
                : Path.GetFileName(normalized.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));

        return new OmniboxSuggestion(
            OmniboxSuggestionKind.Path,
            string.IsNullOrEmpty(title) ? normalized : title,
            normalized,
            normalized,
            AppIcons.Folder,
            "#54A3F7");
    }

    private static OmniboxSuggestion CreateResultSuggestion(FileSystemEntry entry)
        => new(
            OmniboxSuggestionKind.Result,
            entry.Name,
            entry.FullPath,
            entry.FullPath,
            entry.IsDirectory ? AppIcons.Folder : AppIcons.File,
            entry.IsDirectory ? "#54A3F7" : "#7C8798",
            entry);

    private static bool LooksLikePath(string value)
        => value.StartsWith('~')
           || value.StartsWith('/')
           || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
           || value.Contains(Path.DirectorySeparatorChar)
           || value.Contains(Path.AltDirectorySeparatorChar);

    private static bool IsNavigablePath(string path)
        => Directory.Exists(path) || VirtualPath.IsRemotePath(path);

    private static string NormalizePath(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
            value = uri.LocalPath;
        else
        {
            try
            {
                value = Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
            }
        }

        if (value.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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
    }
}
