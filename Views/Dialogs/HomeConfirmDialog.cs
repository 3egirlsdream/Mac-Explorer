using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MacExplorer.Controls;

namespace MacExplorer.Views.Dialogs;

internal sealed class HomeConfirmDialog : DialogWindow
{
    public HomeConfirmDialog(string title, string message)
    {
        Title = title; Width = 440; Height = 230;
        var panel = new StackPanel { Spacing = 18 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20 });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Classes = { "secondary" } };
        cancel.Click += (_, _) => Close(false);
        var confirm = new Button { Content = "清空记录", Classes = { "primary" } };
        confirm.Click += (_, _) => Close(true);
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); panel.Children.Add(buttons);
        Content = new Border { Classes = { "home-dialog-surface" }, Child = panel };
        Opened += (_, _) => cancel.Focus();
    }
}
