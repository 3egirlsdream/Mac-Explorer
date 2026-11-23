using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Search")]
public sealed class GlobalSearchTests
{
    [Fact]
    public async Task SearchOmniboxProviderQueriesTheAppWideIndex()
    {
        var entry = new FileSystemEntry
        {
            FullPath = "/Users/example/Archives/2026-invoice.pdf",
            Name = "2026-invoice.pdf",
            Extension = ".pdf"
        };
        var index = new RecordingFileIndex([entry]);
        var provider = new SearchOmniboxProvider(index);

        var results = await provider.GetSuggestionsAsync(null!, "invoice", CancellationToken.None);

        Assert.Equal("invoice", index.LastSearchPattern);
        var result = Assert.Single(results);
        Assert.Equal(entry.FullPath, result.Value);
        Assert.Same(entry, result.Entry);
        Assert.Equal("/Users/example/Archives/2026-invoice.pdf", result.Subtitle);
    }

    [Fact]
    public void CustomFolderAppendPreservesExistingLocations()
    {
        var settings = new InMemorySettingsService();
        var service = new GlobalSearchScopeService(settings);
        service.SetCustomFolders(["/tmp/mac-explorer-first"]);

        service.SetCustomFolders(service.CustomFolders.Concat(["/tmp/mac-explorer-second"]));

        Assert.Contains("/tmp/mac-explorer-first", service.CustomFolders);
        Assert.Contains("/tmp/mac-explorer-second", service.CustomFolders);
        Assert.Equal(2, service.CustomFolders.Count);
    }

    private sealed class RecordingFileIndex(IReadOnlyList<FileSystemEntry> results) : IFileIndex
    {
        public string? LastSearchPattern { get; private set; }

        public Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string parentPath)
            => Task.FromResult<IReadOnlyList<FileSystemEntry>>([]);

        public Task<FileSystemEntry?> GetEntryAsync(string path)
            => Task.FromResult<FileSystemEntry?>(null);

        public Task<IReadOnlyList<FileSystemEntry>> SearchByNameAsync(string pattern, int limit = 100)
        {
            LastSearchPattern = pattern;
            return Task.FromResult(results);
        }

        public Task<bool> IsDirectoryFreshAsync(string path, TimeSpan freshnessThreshold)
            => Task.FromResult(false);
    }

}
