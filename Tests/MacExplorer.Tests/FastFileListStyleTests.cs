using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FastListUsesLegacyNameAndSelectionGeometryAcrossThemesAndFontSizes(bool grid, bool grouped)
    {
        using var theme = new FastListTestTheme();
        var styles = (Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml"));
        Application.Current!.Styles.Add(styles);
        using var vm = CreateViewModel(new FakeFileService("/tmp/FastStyleTests"),
            sortFilter: new SortFilterViewModel { ViewMode = grid ? ViewMode.Grid : ViewMode.List, GroupField = grouped ? GroupField.Type : GroupField.None });
        var names = new[] { "Readme.txt", "一个比较长的中文文件名称.txt", "项目资料", "a-very-long-file-name.csproj", "短名称.txt", "人物相册" };
        foreach (var (name, i) in names.Select((name, i) => (name, i)))
            vm.Entries.Add(new FileSystemEntry
            {
                FullPath = $"/tmp/FastStyleTests/{name}", Name = name, Extension = Path.GetExtension(name),
                IsDirectory = i is 2 or 5, IsVirtual = i == 5, VirtualItemCount = 123
            });
        if (grouped)
        {
            vm.Groups.Add(new FileGroup { Name = "文档", Entries = vm.Entries.Take(3).ToList() });
            vm.Groups.Add(new FileGroup { Name = "文件夹", Entries = vm.Entries.Skip(3).ToList() });
        }
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 640, Height = 680, Content = view };
        var fast = view.FindControl<FastFileList>("FastList")!;
        try
        {
            window.Show();
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var scale in new[] { 1d, 1.25 })
            {
                window.RequestedThemeVariant = variant;
                window.Resources["FontSizeBody"] = 13 * scale;
                window.Resources["FontSizeLabel"] = 12 * scale;
                window.Resources["FontSizeCaption"] = 11 * scale;
                window.Resources["FontSizeMeta"] = 10 * scale;
                window.Resources["TypographyListRowMinHeight"] = 28 * scale;
                vm.UseFastFileList = false;
                Dispatcher.UIThread.RunJobs();
                var labels = view.GetVisualDescendants().OfType<TextBlock>()
                    .Where(c => c.IsEffectivelyVisible && c.Classes.Contains("entry-name-text"))
                    .ToDictionary(c => ((FileSystemEntry)c.DataContext!).FullPath);
                Assert.Equal(names.Length, labels.Count);
                var expected = labels.ToDictionary(pair => pair.Key, pair => BoundsInView(pair.Value));
                var targets = view.GetVisualDescendants().OfType<Border>()
                    .Where(c => c.IsEffectivelyVisible && c.Classes.Contains("file-grid-icon-target"))
                    .ToDictionary(c => ((FileSystemEntry)c.DataContext!).FullPath, BoundsInView);
                var foreground = labels.Values.First().Foreground;
                var fontWeight = labels.Values.First().FontWeight;
                vm.UseFastFileList = true;
                Dispatcher.UIThread.RunJobs();
                var origin = fast.TranslatePoint(default, view)!.Value;
                foreach (var entry in vm.Entries)
                {
                    var index = fast.IndexOf(entry);
                    Assert.Equal(expected[entry.FullPath], fast.NameBounds(index).Translate((Vector)origin));
                    if (grid) Assert.Equal(targets[entry.FullPath], fast.GridIconTargetBounds(index).Translate((Vector)origin));
                }
                Assert.Equal(foreground, fast.Foreground);
                Assert.Equal(fontWeight, fast.FontWeight);
                Assert.Equal(12 * scale, fast.DetailFontSize);
                Assert.Equal(10 * scale, fast.MetaFontSize);
                Assert.NotEqual(default, fast.SelectionOutline);
            }
        }
        finally { window.Close(); Application.Current!.Styles.Remove(styles); }

        Rect BoundsInView(Control control) => new(control.TranslatePoint(default, view)!.Value, control.Bounds.Size);
    }
}
