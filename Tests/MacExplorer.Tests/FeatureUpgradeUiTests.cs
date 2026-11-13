using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "FeatureUpgrade")]
public sealed class FeatureUpgradeUiTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public async Task LargeTextStillHighlightsTheIndexedExcerptWithinThePreviewBudget()
    {
        using var theme = new FastListTestTheme();
        var path = Path.Combine(RuntimePaths.TestRoot!, "large-content.txt");
        await File.WriteAllTextAsync(path, new string('\n', 8999) + "indexed needle excerpt\n" + new string('x', 5 * 1024 * 1024));
        var match = new DocumentMatch("indexed needle excerpt", new("line", 9000));
        var entry = new FileSystemEntry { FullPath = path, Name = Path.GetFileName(path), SearchMatches = [match] };
        var dialog = new MacExplorer.Views.Dialogs.ContentMatchDialog(entry, "needle");
        try
        {
            dialog.Show(); AvaloniaEdit.TextEditor? editor = null;
            await Until(() => (editor = dialog.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().FirstOrDefault())?.SelectedText == "needle");
            dialog.UpdateLayout();
            Assert.Same(dialog, TopLevel.GetTopLevel(editor!.TextArea)); Assert.True(editor.TextArea.Bounds.Height > 0);
            Assert.False(editor!.ShowLineNumbers);
            Assert.Equal("indexed needle excerpt", editor.Document.Text);
            var previewHost = Assert.IsType<Panel>(editor.Parent);
            Assert.True(editor.Bounds.Width > dialog.Bounds.Width * .85);
            Assert.Equal(626, dialog.Bounds.Height);
            Assert.Equal(550, previewHost.Bounds.Height);
            dialog.Width = 520; dialog.Height = 400; Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
            Assert.True(editor.Bounds.Height > 0);
            Assert.True(previewHost.Bounds.Height < 550);
            Assert.Equal(dialog.Bounds.Height - 16, previewHost.TranslatePoint(default, dialog)!.Value.Y + previewHost.Bounds.Height);
            Assert.Empty(previewHost.GetVisualDescendants().OfType<MacExplorer.Controls.AppButton>());
            dialog.GetVisualDescendants().OfType<MacExplorer.Controls.WindowTitleBar>().Single()
                .FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(dialog.IsVisible);
        }
        finally { dialog.Close(); File.Delete(path); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task FullWidthTextPreviewNavigatesToTheRequestedHitWithoutASidePanel()
    {
        using var theme = new FastListTestTheme();
        var path = Path.Combine(RuntimePaths.TestRoot!, "content-locations.txt");
        await File.WriteAllTextAsync(path, "heading\nfirst needle\nsecond needle\n");
        var first = new DocumentMatch("first needle", new("line", 2));
        var second = new DocumentMatch("second needle", new("line", 3));
        var entry = new FileSystemEntry { FullPath = path, Name = Path.GetFileName(path), SearchMatches = [first, second] };
        var dialog = new MacExplorer.Views.Dialogs.ContentMatchDialog(entry, "needle", second);
        try
        {
            dialog.Show(); AvaloniaEdit.TextEditor? editor = null;
            await Until(() => (editor = dialog.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().FirstOrDefault())?.SelectedText == "needle");
            dialog.UpdateLayout();
            Assert.Same(dialog, TopLevel.GetTopLevel(editor!.TextArea)); Assert.True(editor.TextArea.Bounds.Height > 0);
            Assert.Equal(3, editor!.Document.GetLineByOffset(editor.SelectionStart).LineNumber);
            Assert.IsType<Panel>(editor.Parent);
            Assert.True(editor.Bounds.Width > dialog.Bounds.Width * .85);
        }
        finally { dialog.Close(); File.Delete(path); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task MultipleSelectionRestoresTheOriginalPlaceholderAndSingleSelectionStillShowsDetails()
    {
        using var language = new LocalizationService(new StartupSettings(), () => AppLanguage.English);
        using var vm = CreateViewModel(new FakeFileService("/tmp/multi-info"));
        vm.IsInfoPanelVisible = true;
        var first = new FileSystemEntry { FullPath = "/tmp/multi-info/first", Name = "first", IsDirectory = true };
        var second = new FileSystemEntry { FullPath = "/tmp/multi-info/second", Name = "second", IsDirectory = true };
        vm.SelectedEntries.Add(first);
        var calls = 0;
        var panel = new InfoPanelView(null, (path, token) =>
        {
            calls++; return Task.FromResult(new DirectorySizeResult(4096, true));
        })
        { DataContext = vm };
        var window = new Window { Content = panel, Width = 320, Height = 700 };
        try
        {
            window.Show(); Assert.Equal(1, calls);
            vm.SelectedEntries.Add(second);
            await Until(() => !panel.FindControl<StackPanel>("DetailsPanel")!.IsVisible
                && panel.FindControl<TextBlock>("InfoPath")!.Text == "—");
            Assert.Equal(1, calls);
            Assert.Null(panel.FindControl<StackPanel>("MultiSelectionPanel"));
            Assert.Null(panel.FindControl<StackPanel>("SearchMatchesPanel"));
            vm.SelectedEntries.Remove(second);
            await Until(() => panel.FindControl<TextBlock>("InfoPath")!.Text == first.FullPath);
            Assert.True(panel.FindControl<StackPanel>("DetailsPanel")!.IsVisible);
            Assert.Equal(2, calls);
        }
        finally { window.Close(); }
    }

    private static Task Until(Func<bool> condition) => UntilAsync(() => Task.FromResult(condition()));
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (await condition()) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Fail("UI state did not settle.");
    }
}
