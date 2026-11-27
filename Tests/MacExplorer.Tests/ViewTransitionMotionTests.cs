using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using LiquidGlassAvaloniaUI;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "UI")]
public sealed class ViewTransitionMotionTests : FileListViewModelTestBase
{
    [AvaloniaFact]
    public void FastFileListStartsLayoutMorphWhenSwitchingModesAndSurvivesSameRowsResync()
    {
        var previous = ViewMotion.ForceReducedMotionForTesting;
        var window = new Window { Width = 720, Height = 480 };
        var list = new FastFileList { Width = 720, Height = 480 };
        window.Content = list;
        window.Show();
        try
        {
            var entries = Enumerable.Range(0, 12)
                .Select(i => new FileSystemEntry
                {
                    Name = $"file-{i:D2}.txt",
                    FullPath = $"/tmp/motion-test/file-{i:D2}.txt",
                    Extension = ".txt",
                    Size = 1024 + i
                })
                .ToArray();
            list.SetRows(entries);
            window.UpdateLayout();

            ViewMotion.ForceReducedMotionForTesting = false;
            list.IsGrid = true;
            Assert.True(list.IsLayoutMorphActive);

            // Re-syncing the exact same entries during ViewMode switch must not cancel the morph.
            list.SetRows(entries.ToArray());
            Assert.True(list.IsLayoutMorphActive);

            // Grid -> Tree (with expanded child rows and grouped tree headers) preserves the morph.
            list.CancelLayoutMorph();
            list.IsGrid = false;
            Assert.True(list.IsLayoutMorphActive);
            var treeRows = entries.Select(e => new FileTreeRow(e, 0, false, false, false, false))
                .Append(new FileTreeRow(new FileSystemEntry
                {
                    Name = "child.txt",
                    FullPath = "/tmp/motion-test/sub/child.txt",
                    Extension = ".txt"
                }, 1, false, false, false, false))
                .ToArray();
            list.SetTreeRows(treeRows, [new FastFileListGroup("All", treeRows.Length, entries.Length)]);
            Assert.True(list.IsLayoutMorphActive);

            // Tree (with expanded child rows) -> Grid preserves the morph for top-level entries.
            list.CancelLayoutMorph();
            list.IsGrid = true;
            Assert.True(list.IsLayoutMorphActive);
            list.SetRows(entries, [new FastFileListGroup("All", entries.Length)]);
            Assert.True(list.IsLayoutMorphActive);

            // Switching to different directory rows cancels the morph cleanly.
            list.SetRows([new FileSystemEntry { Name = "other.txt", FullPath = "/tmp/other/other.txt", Extension = ".txt" }]);
            Assert.False(list.IsLayoutMorphActive);

            list.SetRows(entries);
            list.GridIconSize = 96;
            Assert.True(list.IsLayoutMorphActive);
            list.CancelLayoutMorph();
            Assert.False(list.IsLayoutMorphActive);

            ViewMotion.ForceReducedMotionForTesting = true;
            list.IsGrid = false;
            Assert.False(list.IsLayoutMorphActive);
        }
        finally
        {
            ViewMotion.ForceReducedMotionForTesting = previous;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ToolbarViewModeThumbMatchesListModeEvenWhenInitializedHidden()
    {
        using var theme = new FastListTestTheme();
        using var vm = CreateViewModel(new FakeFileService("/tmp/motion-test"));
        Assert.Equal(ViewMode.List, vm.ViewMode);

        var toolbar = new FinderToolbar { DataContext = vm, IsVisible = false };
        var window = new Window { Width = 800, Height = 120, Content = toolbar };
        window.Show();
        try
        {
            window.UpdateLayout();
            var thumb = toolbar.FindControl<Border>("ViewModeSliderThumb")!;
            Assert.Equal(32d, Assert.IsType<TranslateTransform>(thumb.RenderTransform).X, 3);

            toolbar.IsVisible = true;
            window.UpdateLayout();
            Assert.Equal(32d, Assert.IsType<TranslateTransform>(thumb.RenderTransform).X, 3);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ViewMotionRespectsReducedMotionFlag()
    {
        var previous = ViewMotion.ForceReducedMotionForTesting;
        var window = new Window { Width = 400, Height = 300 };
        var border = new Border { Width = 120, Height = 80 };
        window.Content = border;
        window.Show();
        try
        {
            window.UpdateLayout();
            ViewMotion.ForceReducedMotionForTesting = false;
            Assert.True(ViewMotion.ShouldAnimate(border));

            ViewMotion.AnimateTranslationX(border, 0, 64, 160);
            var visual = ElementComposition.GetElementVisual(border);
            if (visual != null)
                Assert.Equal(64d, visual.Translation.X, 3);

            ViewMotion.ForceReducedMotionForTesting = true;
            Assert.False(ViewMotion.ShouldAnimate(border));

            ViewMotion.AnimateEnter(border, fromX: 20, fromY: 10, fromScale: 0.95, fromOpacity: 0, durationMs: 200);
            if (visual != null)
            {
                Assert.Equal(1f, visual.Opacity, 3);
                Assert.Equal(default, visual.Translation);
            }
        }
        finally
        {
            ViewMotion.ForceReducedMotionForTesting = previous;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FastFileListMorphBatchesRenderAndPreservesListAndGridNameCaches()
    {
        var previous = ViewMotion.ForceReducedMotionForTesting;
        using var theme = new FastListTestTheme();
        var list = new FastFileList { Width = 720, Height = 360 };
        var window = new Window { Width = 720, Height = 360, Content = list };
        window.Show();
        try
        {
            var entries = Enumerable.Range(0, 48)
                .Select(i => new FileSystemEntry
                {
                    Name = $"sample-document-name-{i:D2}.txt",
                    FullPath = $"/tmp/morph-batch/sample-document-name-{i:D2}.txt",
                    Extension = ".txt",
                    Size = 2048 + i,
                    IsSelected = i is 0 or 3,
                    IsCut = i is 1 or 4,
                    GitStatus = i % 5 == 0 ? GitFileStatus.Modified : GitFileStatus.None
                })
                .ToArray();
            list.SetRows(entries);
            window.UpdateLayout();

            using var target = new RenderTargetBitmap(new PixelSize(720, 360));
            target.Render(list);
            Assert.True(list.CachedTextCount > 0);

            ViewMotion.ForceReducedMotionForTesting = false;
            list.PendingMorphStartOffsetY = 44;
            list.IsGrid = true;
            list.PendingMorphStartOffsetY = 0;
            Assert.True(list.IsLayoutMorphActive);

            // Render active batched morph frame (including selected, cut, Git-badged, and appearing items).
            target.Render(list);
            Assert.True(list.LastRenderedRowCount > 0);

            // Height-only bounds change (e.g., ListHeaderPanel hiding) preserves active morph.
            list.Height = 404;
            window.UpdateLayout();
            Assert.True(list.IsLayoutMorphActive);

            // Finish morph and render Grid to populate grid name cache.
            list.CancelLayoutMorph();
            target.Render(list);
            var gridCacheCount = list.CachedGridNameCount;
            Assert.True(gridCacheCount > 0);

            // Switch Grid -> List and verify grid name cache is retained across mode switches,
            // while target list rows are progressively warmed during morph frames.
            list.PendingMorphStartOffsetY = -44;
            list.IsGrid = false;
            list.PendingMorphStartOffsetY = 0;
            Assert.True(list.IsLayoutMorphActive);
            target.Render(list);
            target.Render(list);

            // ColumnWidths update clears list texts without evicting cached grid names.
            list.ColumnWidths = FileListColumnLayoutService.Minimums;
            Assert.Equal(gridCacheCount, list.CachedGridNameCount);

            // Grid -> Tree morph renders disclosures + list columns cleanly.
            list.CancelLayoutMorph();
            list.IsGrid = true;
            list.CancelLayoutMorph();
            target.Render(list);
            var treeRows = entries.Select(e => new FileTreeRow(e, 0, true, false, false, false)).ToArray();
            list.IsGrid = false;
            list.SetTreeRows(treeRows, []);
            Assert.True(list.IsLayoutMorphActive);
            target.Render(list);

            // Rapid mid-morph reversal stays valid and renders cleanly.
            list.IsGrid = true;
            Assert.True(list.IsLayoutMorphActive);
            target.Render(list);

            list.CancelLayoutMorph();
            list.IsGrid = false;
            list.CancelLayoutMorph();
            target.Render(list);
            Assert.True(list.CachedGridNameCount > 0);
        }
        finally
        {
            ViewMotion.ForceReducedMotionForTesting = previous;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void LiquidGlassExcludedSceneSuppressionOnlyAppliesToExcludedSubtree()
    {
        var excludedChild = new Border { Width = 100, Height = 100 };
        var excludedHost = new Grid { Children = { excludedChild } };
        LiquidGlassBackdrop.SetIsExcludedFromCapture(excludedHost, true);
        var normalChild = new Border { Width = 100, Height = 100 };

        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = new StackPanel { Children = { excludedHost, normalChild } }
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.Null(LiquidGlassBackdrop.BeginExcludedSceneSuppression(normalChild));

            var token = LiquidGlassBackdrop.BeginExcludedSceneSuppression(excludedChild);
            Assert.NotNull(token);
            token!.Dispose();
            token.Dispose(); // Idempotent double-dispose must not throw or underflow.
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void SortFilterViewModeChangesUseDeferredPersistence()
    {
        var settings = new DeferredTrackingSettingsService();
        var sortFilter = new SortFilterViewModel(settings);

        sortFilter.ViewMode = ViewMode.Grid;
        sortFilter.ViewMode = ViewMode.Tree;
        sortFilter.ViewMode = ViewMode.List;

        Assert.Empty(settings.SyncWrites);
        Assert.Equal(["ViewMode", "ViewMode", "ViewMode"], settings.DeferredWrites);
        Assert.Equal(ViewMode.List, settings.Get("ViewMode", ViewMode.Grid));
    }

    [AvaloniaFact]
    public void MidMorphNameColumnWidthChangeRetainsNewlyWarmedListTexts()
    {
        var previous = ViewMotion.ForceReducedMotionForTesting;
        using var theme = new FastListTestTheme();
        var list = new FastFileList { Width = 760, Height = 360, IsGrid = true };
        var window = new Window { Width = 760, Height = 360, Content = list };
        window.Show();
        try
        {
            var entries = Enumerable.Range(0, 16)
                .Select(i => new FileSystemEntry
                {
                    Name = $"report-file-{i:D2}.txt",
                    FullPath = $"/tmp/morph-col/report-file-{i:D2}.txt",
                    Extension = ".txt",
                    Size = 1024 + i
                })
                .ToArray();
            list.SetRows(entries);
            window.UpdateLayout();

            using var target = new RenderTargetBitmap(new PixelSize(760, 360));
            target.Render(list);

            ViewMotion.ForceReducedMotionForTesting = false;
            list.IsGrid = false;
            Assert.True(list.IsLayoutMorphActive);

            // Name-only width adjustment during morph must not wipe texts freshly warmed at the new width when landing.
            var adjusted = list.ColumnWidths.With(FileListColumn.Name, list.ColumnWidths.Name - 24);
            list.ColumnWidths = adjusted;
            target.Render(list);
            var cachedDuringMorph = list.CachedTextCount;
            Assert.True(cachedDuringMorph > 0);

            list.CancelLayoutMorph();
            Assert.True(list.CachedTextCount >= cachedDuringMorph);
        }
        finally
        {
            ViewMotion.ForceReducedMotionForTesting = previous;
            window.Close();
        }
    }

    private sealed class DeferredTrackingSettingsService : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public List<string> SyncWrites { get; } = [];
        public List<string> DeferredWrites { get; } = [];
        public event Action<string>? SettingChanged;

        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public T Get<T>(string key, T defaultValue)
        {
            if (!_values.TryGetValue(key, out var raw)) return defaultValue;
            if (typeof(T).IsEnum) return (T)Enum.Parse(typeof(T), raw, ignoreCase: true);
            if (typeof(T) == typeof(bool)) return (T)(object)bool.Parse(raw);
            return (T)(object)raw;
        }

        public void Set(string key, string value)
        {
            SyncWrites.Add(key);
            _values[key] = value;
            SettingChanged?.Invoke(key);
        }

        public void SetDeferred(string key, string value)
        {
            DeferredWrites.Add(key);
            _values[key] = value;
            SettingChanged?.Invoke(key);
        }

        public void Set<T>(string key, T value) => Set(key, value?.ToString() ?? "");

        public Dictionary<string, string> GetAll() => new(_values, StringComparer.OrdinalIgnoreCase);
    }
}
