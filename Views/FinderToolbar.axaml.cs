using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Views.Dialogs;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public partial class FinderToolbar : UserControl
{
    // Callback to open settings dialog via MainWindow
    public Action? OpenSettingsCallback { get; set; }

    public FinderToolbar()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => _ = UpdateOfficeTemplateVisibilityAsync();
    }

    private async Task UpdateOfficeTemplateVisibilityAsync()
    {
        var contextMenu = App.Services.GetRequiredService<IContextMenuService>();
        NewWordButton.IsVisible = false;
        NewExcelButton.IsVisible = false;
        NewPowerPointButton.IsVisible = false;
        NewPagesButton.IsVisible = false;
        NewNumbersButton.IsVisible = false;
        NewKeynoteButton.IsVisible = false;

        var availability = await Task.Run(() =>
        {
            var hasWps = contextMenu.IsAppInstalled("com.kingsoft.wpsoffice.mac");
            return new
            {
                Word = hasWps || contextMenu.IsAppInstalled("com.microsoft.Word"),
                Excel = hasWps || contextMenu.IsAppInstalled("com.microsoft.Excel"),
                PowerPoint = hasWps || contextMenu.IsAppInstalled("com.microsoft.Powerpoint"),
                Pages = contextMenu.IsAppInstalled("com.apple.iWork.Pages"),
                Numbers = contextMenu.IsAppInstalled("com.apple.iWork.Numbers"),
                Keynote = contextMenu.IsAppInstalled("com.apple.iWork.Keynote")
            };
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            NewWordButton.IsVisible = availability.Word;
            NewExcelButton.IsVisible = availability.Excel;
            NewPowerPointButton.IsVisible = availability.PowerPoint;
            NewPagesButton.IsVisible = availability.Pages;
            NewNumbersButton.IsVisible = availability.Numbers;
            NewKeynoteButton.IsVisible = availability.Keynote;
        });
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    private void ToggleNewDropdown(object? sender, RoutedEventArgs e)
    {
        var shouldOpen = !NewDropdown.IsOpen;
        CloseDropdowns();
        NewDropdown.IsOpen = shouldOpen;
    }

    private async void NewFolder(object? sender, RoutedEventArgs e)
    {
        NewDropdown.IsOpen = false;
        if (ViewModel == null) return;
        await ViewModel.CreateNewFolderAsync();
    }

    private async void NewFile(object? sender, RoutedEventArgs e)
    {
        NewDropdown.IsOpen = false;
        if (sender is not Button btn || btn.Tag is not string ext || ViewModel == null) return;
        await ViewModel.CreateNewFileAsync(ext);
    }

    private void CutSelected(object? sender, RoutedEventArgs e) => ViewModel?.CutSelected();
    private void CopySelected(object? sender, RoutedEventArgs e) => ViewModel?.CopySelected();
    private async void PasteItems(object? sender, RoutedEventArgs e) { if (ViewModel != null) await ViewModel.PasteAsync(); }
    private void DeleteSelected(object? sender, RoutedEventArgs e) => ViewModel?.ShowDeleteConfirmDialog();

    private void ToggleGridView(object? sender, RoutedEventArgs e) => ViewModel?.SetViewMode(ViewMode.Grid);
    private void ToggleListView(object? sender, RoutedEventArgs e) => ViewModel?.SetViewMode(ViewMode.List);

    private void ToggleSortDirection(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.SetSort(ViewModel.SortField, !ViewModel.SortAscending);
            SortDirectionButton.Content = ViewModel.SortAscending ? "升序 ↑" : "降序 ↓";
        }
        SortDropdown.IsOpen = false;
    }

    private void ToggleSortDropdown(object? sender, RoutedEventArgs e)
    {
        var shouldOpen = !SortDropdown.IsOpen;
        CloseDropdowns();
        SortDropdown.IsOpen = shouldOpen;
        if (ViewModel != null)
            SortDirectionButton.Content = ViewModel.SortAscending ? "升序 ↑" : "降序 ↓";
    }

    private void SelectSortField(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && Enum.TryParse<SortField>(value, out var field))
            ViewModel?.SetSort(field);
        SortDropdown.IsOpen = false;
    }

    private void SelectGroupField(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && Enum.TryParse<GroupField>(value, out var field) && ViewModel != null)
            ViewModel.GroupField = field;
        SortDropdown.IsOpen = false;
    }

    private void GoHome(object? sender, RoutedEventArgs e) => ViewModel?.GoHome();

    private void TogglePreviewPane(object? sender, RoutedEventArgs e)
    {
        ViewModel?.TogglePreviewPane();
    }

    private void ToggleMetadataPanel(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleMetadataPanel();
    }

    private void ToggleInfoPanel(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleInfoPanel();
    }

    private void ToggleMoreDropdown(object? sender, RoutedEventArgs e)
    {
        var shouldOpen = !MoreDropdown.IsOpen;
        CloseDropdowns();
        MoreDropdown.IsOpen = shouldOpen;
    }

    public void CloseDropdowns()
    {
        NewDropdown.IsOpen = false;
        SortDropdown.IsOpen = false;
        MoreDropdown.IsOpen = false;
    }

    public void CloseDropdownsFromPointerSource(object? source)
    {
        var visual = source as Visual;
        if (IsInsideVisual(visual, NewBtn)
            || IsInsideVisual(visual, SortButton)
            || IsInsideVisual(visual, MoreBtn)
            || IsInsideVisual(visual, NewDropdown.Child as Visual)
            || IsInsideVisual(visual, SortDropdown.Child as Visual)
            || IsInsideVisual(visual, MoreDropdown.Child as Visual))
            return;

        CloseDropdowns();
    }

    private static bool IsInsideVisual(Visual? visual, Visual? target)
    {
        if (target == null) return false;
        for (; visual != null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, target))
                return true;
        return false;
    }

    private void OnDropdownStateChanged(object? sender, EventArgs e)
    {
        NewBtn.Classes.Set("dropdown-open", NewDropdown.IsOpen);
        SortButton.Classes.Set("dropdown-open", SortDropdown.IsOpen);
        MoreBtn.Classes.Set("dropdown-open", MoreDropdown.IsOpen);
    }

    private async void OnConnectRemoteServer(object? sender, RoutedEventArgs e)
    {
        MoreDropdown.IsOpen = false;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window window) return;

        var dialog = new RemoteConnectionDialog();
        var result = await dialog.ShowDialog<RemoteServerInfo?>(window);
        if (result != null && dialog.Connected && ViewModel != null)
        {
            await ViewModel.ConnectToServerAsync(result);
        }
    }
}
