using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MacExplorer.Controls;

namespace MacExplorer.Views.Dialogs;

internal sealed class HomeNameDialog : DialogWindow
{
    public HomeNameDialog(string title, string initial = "")
    {
        Title = title;
        TitleBarContent = new TextBlock { Text = title, Classes = { "dialog-window-title", "centered" } };
        ToolTip.SetTip(TitleBarContent, title);
        Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        Styles.Add(new DialogSettingsStyles());
        Width = 380;
        MinWidth = 320;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var input = new TextBox
        {
            Text = initial,
            PlaceholderText = MacExplorer.Services.Impl.LocalizationText.Get("标签名称"),
            MaxLength = 80,
            Classes = { "settings-inline-input" }
        };
        AutomationProperties.SetName(input, MacExplorer.Services.Impl.LocalizationText.Get("标签名称"));
        var error = new TextBlock
        {
            Classes = { "settings-description" },
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        error.Bind(TextBlock.ForegroundProperty, error.GetResourceObservable("DangerBrush"));

        void TrySave()
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                error.Text = MacExplorer.Services.Impl.LocalizationText.Get("请输入标签名称。");
                error.IsVisible = true;
                return;
            }
            Close(input.Text.Trim());
        }

        input.TextChanged += (_, _) => error.IsVisible = false;
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; TrySave(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };

        var cancel = new AppButton { Content = MacExplorer.Services.Impl.LocalizationText.Get("取消"), Classes = { "secondary", "compact" } };
        cancel.Click += (_, _) => Close();
        var save = new AppButton { Content = MacExplorer.Services.Impl.LocalizationText.Get("保存"), Classes = { "primary", "compact" }, IsDefault = true };
        save.Click += (_, _) => TrySave();

        Content = new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 16),
            Spacing = 12,
            Children =
            {
                new Border
                {
                    Classes = { "settings-group" },
                    Child = new Border { Classes = { "settings-compact-row" }, Child = input }
                },
                error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, save }
                }
            }
        };
        Opened += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
