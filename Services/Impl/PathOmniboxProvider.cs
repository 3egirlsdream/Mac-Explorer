using MacExplorer.Models;
using MacExplorer.ViewModels;
using AppIcons = MacExplorer.Assets.Icons;

namespace MacExplorer.Services.Impl;

/// <summary>Provides path navigation suggestions (directory completion, fuzzy home matches).</summary>
public class PathOmniboxProvider : IOmniboxProvider
{
    private const int MaxPathSuggestions = 6;

    public string Name => "Path";
    public int Priority => 0;

    public Task<IReadOnlyList<OmniboxSuggestion>> GetSuggestionsAsync(
        FileListViewModel viewModel,
        string input,
        CancellationToken cancellationToken)
    {
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
            return Task.FromResult<IReadOnlyList<OmniboxSuggestion>>([]);

        var suggestions = GetPathSuggestions(value, viewModel.HomeDirectory, cancellationToken);
        return Task.FromResult<IReadOnlyList<OmniboxSuggestion>>(suggestions);
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
            AddFuzzyDirectoryMatches(homeDirectory, value, suggestions, cancellationToken);
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
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return suggestions;
    }

    private static void AddFuzzyDirectoryMatches(
        string root,
        string value,
        ICollection<OmniboxSuggestion> suggestions,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return;

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
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    internal static OmniboxSuggestion CreatePathSuggestion(string path)
    {
        var normalized = VirtualPath.IsRemotePath(path) ? path : Path.GetFullPath(path);
        var title = VirtualPath.IsRemotePath(path)
            ? path
            : normalized == Path.GetPathRoot(normalized)
                ? normalized
                : Path.GetFileName(normalized.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));

        var isFile = File.Exists(normalized);
        return new OmniboxSuggestion(
            OmniboxSuggestionKind.Path,
            string.IsNullOrEmpty(title) ? normalized : title,
            normalized,
            normalized,
            isFile ? AppIcons.File : AppIcons.Folder,
            isFile ? "#7C8798" : "#54A3F7");
    }

    internal static bool LooksLikePath(string value)
        => value.StartsWith('~')
           || value.StartsWith('/')
           || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
           || value.Contains(Path.DirectorySeparatorChar)
           || value.Contains(Path.AltDirectorySeparatorChar);

    internal static bool IsNavigablePath(string path)
        => OmniboxService.IsNavigablePath(path);

    internal static string NormalizePath(string value)
        => OmniboxService.NormalizePath(value);
}
