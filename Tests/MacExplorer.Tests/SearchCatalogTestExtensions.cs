using MacExplorer.Indexing;
using MacExplorer.Services.Search;

namespace MacExplorer.Tests;

internal static class SearchCatalogTestExtensions
{
    internal static async Task ReconcileDirectoryAsync(this SearchCatalog catalog, string directory,
        IReadOnlyList<SearchEntry>? entries = null)
    {
        await using var scan = await catalog.BeginDirectoryScanAsync(directory);
        if (entries != null) await scan.RecordAsync(entries);
        await scan.CompleteAsync();
    }
}
