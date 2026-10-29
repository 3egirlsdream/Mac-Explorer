using Avalonia.Controls;
using Avalonia.Input;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacOS;

internal static class ApplicationMenu
{
    internal static NativeMenu Create(ILocalizationService language, Action showAbout, Action showMainWindow)
    {
        var menu = new NativeMenu();
        var about = new NativeMenuItem();
        about.Click += (_, _) => showAbout();
        menu.Add(about);
        menu.Add(new NativeMenuItemSeparator());
        var showWindow = new NativeMenuItem { Gesture = new KeyGesture(Key.D0, KeyModifiers.Meta) };
        showWindow.Click += (_, _) => showMainWindow();
        menu.Add(showWindow);
        NativeMenuItem? quit = null;
        void Refresh()
        {
            about.Header = language.Get("app.menu_about", AppBranding.Name);
            showWindow.Header = language.Get("app.menu_show_main_window");
            // Avalonia appends the standard system commands when it exports this menu.
            quit ??= menu.Items.OfType<NativeMenuItem>().FirstOrDefault(item =>
                item.Gesture is { Key: Key.Q, KeyModifiers: KeyModifiers.Meta });
            if (quit != null) quit.Header = language.Get("app.menu_quit", AppBranding.Name);
        }
        Refresh();
        menu.NeedsUpdate += (_, _) => Refresh();
        return menu;
    }
}
