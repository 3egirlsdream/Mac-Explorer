using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using MacExplorer.Models;
using MacExplorer.ViewModels;

namespace MacExplorer.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        HomeCtxMenu.Opened += OnContextMenuOpened;
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    private async void OnContextMenuOpened(object? sender, EventArgs e)
    {
        if (ViewModel == null) { HomeCtxMenu.Items.Clear(); return; }
        await ViewModel.ShowBackgroundContextMenuAsync(0, 0);
        PopulateMenu(HomeCtxMenu, ViewModel.ContextMenuActions);
    }

    private static void PopulateMenu(ItemsControl parent, System.Collections.Generic.IList<ContextMenuAction> actions)
    {
        parent.Items.Clear();
        if (actions.Count == 0) return;

        foreach (var action in actions)
        {
            if (action.IsSeparator)
            {
                parent.Items.Add(new Separator());
                continue;
            }

            var item = new MenuItem { Header = action.Label, IsEnabled = action.IsEnabled };

            if (!string.IsNullOrEmpty(action.ShortcutText))
                item.InputGesture = ParseShortcut(action.ShortcutText);

            if (!string.IsNullOrEmpty(action.IconSvg))
            {
                try { item.Icon = new PathIcon { Data = Geometry.Parse(action.IconSvg), Width = 16, Height = 16 }; }
                catch { }
            }

            if (action.Execute != null) { var c = action; item.Click += async (_, _) => await c.Execute(); }
            if (action.SubItems is { Count: > 0 })
            {
                PopulateMenu(item, action.SubItems.ToList());
                ContextMenuPopupStyler.Attach(item);
            }

            parent.Items.Add(item);
        }
    }

    private static KeyGesture? ParseShortcut(string text)
    {
        var parsed = text
            .Replace("⌘", "Cmd+").Replace("⇧", "Shift+").Replace("⌥", "Alt+")
            .Replace("⌃", "Ctrl+").Replace("⌫", "Back").Replace("⌦", "Delete")
            .Replace("↩", "Enter").Replace("⇥", "Tab").Replace("⎋", "Escape");
        try { return KeyGesture.Parse(parsed); } catch { return null; }
    }


    private async void OnHomeSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel == null || string.IsNullOrWhiteSpace(HomeSearchBox.Text))
            return;

        await ViewModel.SearchAsync(HomeSearchBox.Text);
        e.Handled = true;
    }

    private async void NavigateHomeDirectory(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        await ViewModel.NavigateToAsync(ViewModel.HomeDirectory);
    }

    private async void NavigateDesktop(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        await ViewModel.NavigateToAsync(System.IO.Path.Combine(ViewModel.HomeDirectory, "Desktop"));
    }

    private async void NavigateMacintoshHd(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        await ViewModel.NavigateToAsync("/");
    }
}
