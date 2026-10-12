using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "FileList")]
public sealed class FileListContextRetargetViewModelTests : FileListViewModelTestBase
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RightClickingAnotherResultReplacesTheOpenMenuAndSelection(bool search, bool lateDismiss)
    {
        var files = new FakeFileService("/private/tmp/fk-context-retarget");
        var navigation = new NavigationViewModel(files)
        {
            CurrentPath = files.HomeDirectory, IsHomePage = false, IsSearchMode = search
        };
        using var vm = CreateViewModel(files, navigation: navigation,
            sortFilter: new SortFilterViewModel { ViewMode = ViewMode.List });
        vm.SearchScopePath = files.HomeDirectory;
        navigation.SearchQuery = search ? "result" : string.Empty;
        var entries = Enumerable.Range(0, 3).Select(index => new FileSystemEntry
        {
            Name = $"result-{index}.txt", FullPath = $"{files.HomeDirectory}/nested/result-{index}.txt",
            Extension = ".txt", IconKey = "file-text"
        }).ToArray();
        foreach (var entry in entries)
        {
            files.Seed(entry);
            vm.Entries.Add(entry);
        }
        using var tab = new ExplorerTabViewModel(vm) { IsActive = true };
        using var workspace = new ExplorerWorkspaceView { DataContext = tab };
        var view = workspace.FindControl<FileListView>("FileListControl")!;
        var list = view.FindControl<FastFileList>("FastList")!;
        var window = new Window { Width = 1280, Height = 800, Content = workspace };
        window.Styles.Add(new FluentTheme());
        window.AddHandler(InputElement.PointerPressedEvent, (_, e) => workspace.CloseTransientUi(e.Source),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var origin = list.TranslatePoint(default, window)!.Value;
            ContextMenu? previous = null;
            using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            for (var index = 0; index < entries.Length; index++)
            {
                // Move left of the previous popup so the secondary click reaches another row.
                var point = new Point(origin.X + 180 - index * 50, origin.Y + list.RowBounds(index).Center.Y);
                if (lateDismiss && previous != null)
                {
                    // Exercise the native popup's pass-through dismiss layer directly.
                    var overlay = window.GetVisualDescendants().OfType<Control>().Single(control =>
                        control.GetType().FullName == "Avalonia.Controls.Primitives.LightDismissOverlayLayer");
                    overlay.RaiseEvent(new PointerPressedEventArgs(overlay, pointer, window, point, 0,
                        new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed),
                        KeyModifiers.None));
                    list.RaiseEvent(new PointerReleasedEventArgs(list, pointer, window, point, 1,
                        new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased),
                        KeyModifiers.None, MouseButton.Right));
                }
                else
                {
                    window.MouseDown(point, MouseButton.Right, RawInputModifiers.RightMouseButton);
                    window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
                }
                Dispatcher.UIThread.RunJobs();

                Assert.Same(entries[index], Assert.Single(vm.SelectedEntries));
                Assert.Same(entries[index], vm.ContextMenuEntry);
                var menu = Assert.Single(window.GetVisualDescendants().OfType<ContextMenu>());
                Assert.True(menu.IsOpen);
                if (previous != null) Assert.False(previous.IsOpen);
                Assert.Contains(menu.Items.OfType<MenuItem>(), item =>
                    item.Tag is ContextMenuAction { Label: "复制路径" });
                previous = menu;
            }
        }
        finally
        {
            view.DismissContextMenu();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

}
