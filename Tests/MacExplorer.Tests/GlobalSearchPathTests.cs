using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData("pending", false)]
    [InlineData("pending", true)]
    [InlineData("ready", false)]
    [InlineData("ready", true)]
    [InlineData("stale", false)]
    public async Task GlobalSearchExactPathEnterRevealsFile(string state, bool fileUri)
    {
        var root = Directory.CreateTempSubdirectory("MacExplorer_GlobalSearch_");
        try
        {
            using var theme = new FastListTestTheme();
            await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
            var path = Path.Combine(root.FullName, "定位 target #1.txt");
            await File.WriteAllTextAsync(path, "test", TestContext.Current.CancellationToken);
            fixture.Files.Seed(new FileSystemEntry { FullPath = path, Name = Path.GetFileName(path) });
            var viewModel = fixture.Model.FileList;
            var window = fixture.Window;
            var overlay = window.FindControl<Border>("GlobalSearchOverlay")!;
            var input = window.FindControl<TextBox>("GlobalSearchBox")!;
            var results = window.FindControl<ListBox>("GlobalSearchResults")!;
            var suggestions = Assert.IsAssignableFrom<ICollection<OmniboxSuggestion>>(results.ItemsSource);
            window.KeyPress(Key.K, RawInputModifiers.Meta, PhysicalKey.K, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(overlay.IsVisible);

            var staleExecuted = false;
            if (state == "stale")
            {
                suggestions.Add(new OmniboxSuggestion(OmniboxSuggestionKind.Command, "Old", "", "old", "", "",
                    ExecuteAction: () => { staleExecuted = true; return Task.CompletedTask; }));
                results.SelectedIndex = 0;
            }

            input.Text = fileUri ? new Uri(path).AbsoluteUri : path;
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(suggestions);
            if (state == "ready")
            {
                var paths = await new PathOmniboxProvider().GetSuggestionsAsync(viewModel, input.Text,
                    TestContext.Current.CancellationToken);
                suggestions.Add(Assert.Single(paths));
                results.SelectedIndex = 0;
            }

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (viewModel.SelectedEntries.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.False(staleExecuted);
            Assert.False(overlay.IsVisible);
            Assert.Equal(root.FullName, viewModel.CurrentPath);
            Assert.Equal(path, Assert.Single(viewModel.SelectedEntries).FullPath);
            Assert.False(viewModel.IsSearchMode);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task GlobalSearchEnterStillExecutesSelectedSuggestionForOrdinaryQuery()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        var window = fixture.Window;
        var overlay = window.FindControl<Border>("GlobalSearchOverlay")!;
        var input = window.FindControl<TextBox>("GlobalSearchBox")!;
        var results = window.FindControl<ListBox>("GlobalSearchResults")!;
        window.KeyPress(Key.K, RawInputModifiers.Meta, PhysicalKey.K, null);
        Dispatcher.UIThread.RunJobs();
        input.Text = "ordinary query";
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.True(overlay.IsVisible);

        var executed = false;
        var suggestions = Assert.IsAssignableFrom<ICollection<OmniboxSuggestion>>(results.ItemsSource);
        suggestions.Add(new OmniboxSuggestion(OmniboxSuggestionKind.Command, "Match", "", "match", "", "",
            ExecuteAction: () => { executed = true; return Task.CompletedTask; }));
        results.SelectedIndex = 0;
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.True(executed);
        Assert.False(overlay.IsVisible);
    }

    [AvaloniaFact]
    public async Task GlobalSearchDoubleDDoesNotOpenAndConventionalShortcutsStillWork()
    {
        using var theme = new FastListTestTheme();
        await using var fixture = await TabCacheFixture.CreateAsync(entries: 0);
        var window = fixture.Window;
        var overlay = window.FindControl<Border>("GlobalSearchOverlay")!;
        window.Focus();
        window.KeyPress(Key.D, RawInputModifiers.None, PhysicalKey.D, null);
        window.KeyPress(Key.D, RawInputModifiers.None, PhysicalKey.D, null);
        Assert.False(overlay.IsVisible);

        window.KeyPress(Key.K, RawInputModifiers.Meta, PhysicalKey.K, null);
        Assert.True(overlay.IsVisible);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(overlay.IsVisible);
        window.KeyPress(Key.F, RawInputModifiers.Meta | RawInputModifiers.Shift, PhysicalKey.F, null);
        Assert.True(overlay.IsVisible);
    }
}
