using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MacExplorer.Controls;

namespace MacExplorer.Views.Dialogs;

internal sealed class ConfirmDialog : DialogWindow
{
    internal ConfirmDialog(string title, string message, string accept, string cancelText = "取消")
    {
        Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MacExplorer/")) { Source = new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml") });
        Title = title; Width = 440; MinWidth = 340; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new Button { Content = cancelText, Classes = { "secondary", "compact" } };
        var confirm = new Button { Content = accept, Classes = { "primary", "compact" } };
        cancel.Click += (_, _) => Close(false);
        confirm.Click += (_, _) => Close(true);
        Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children =
        {
            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Classes = { "settings-description" } },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8, Children = { cancel, confirm } }
        } };
        Opened += (_, _) => cancel.Focus();
    }
}
