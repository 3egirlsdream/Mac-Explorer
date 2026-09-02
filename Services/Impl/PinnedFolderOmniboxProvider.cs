using MacExplorer.Models;
using MacExplorer.ViewModels;
using AppIcons = MacExplorer.Assets.Icons;

namespace MacExplorer.Services.Impl;

/// <summary>Surfaces Finder-sidebar favourites in the global quick-search results.</summary>
public sealed class PinnedFolderOmniboxProvider : IOmniboxProvider
{
    private readonly IPinnedFolderService? _pinnedFolderService;

    public PinnedFolderOmniboxProvider(IPinnedFolderService? pinnedFolderService = null)
    {
        _pinnedFolderService = pinnedFolderService;
    }

    public string Name => "Pinned folders";
    public int Priority => 12;

    public async Task<IReadOnlyList<OmniboxSuggestion>> GetSuggestionsAsync(
        FileListViewModel viewModel,
        string input,
        CancellationToken cancellationToken)
    {
        if (_pinnedFolderService == null)
            return [];

        var query = input?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return [];

        var pins = await _pinnedFolderService.GetAllAsync();
        var suggestions = new List<OmniboxSuggestion>();
        foreach (var pin in pins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pin.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !pin.FolderPath.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            suggestions.Add(new OmniboxSuggestion(
                OmniboxSuggestionKind.Path,
                pin.DisplayName,
                $"收藏夹 · {pin.FolderPath}",
                pin.FolderPath,
                AppIcons.Folder,
                "#F59E0B"));
        }

        return suggestions;
    }
}
