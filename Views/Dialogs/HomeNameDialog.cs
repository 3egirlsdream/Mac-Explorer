using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MacExplorer.Controls;

namespace MacExplorer.Views.Dialogs;

internal sealed class HomeNameDialog : DialogWindow
{
    public HomeNameDialog(string title, string initial = "")
    {
        Title = title;
        Width = 420;
        Height = 245;
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20 });
        var input = new TextBox { Text = initial, PlaceholderText = "标签名称", MaxLength = 80 };
        panel.Children.Add(input);
        var error = new TextBlock { Classes = { "home-secondary" } };
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Classes = { "secondary" } };
        cancel.Click += (_, _) => Close();
        var save = new Button { Content = "保存", Classes = { "primary" } };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) { error.Text = "请输入标签名称。"; return; }
            Close(input.Text.Trim());
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = new Border { Classes = { "home-dialog-surface" }, Child = panel };
        Opened += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
