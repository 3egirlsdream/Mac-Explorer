using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MacExplorer.Controls;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views.Dialogs;

internal sealed class TabGroupNameDialog : DialogWindow
{
    internal TabGroupNameDialog(string initial, string titleKey = "tabs.group.rename", string nameKey = "tabs.group.name")
    {
        Title = LocalizationText.Get(titleKey);
        var title = new TextBlock { Text = Title, Classes = { "dialog-window-title" } };
        ToolTip.SetTip(title, Title);
        TitleBarContent = title;
        Width = 360;
        MinWidth = 300;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MacExplorer/"))
            { Source = new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml") });
        var input = new TextBox { Name = "GroupNameInput", Text = initial, MaxLength = 80, Classes = { "settings-inline-input" } };
        AutomationProperties.SetName(input, LocalizationText.Get(nameKey));
        var label = new TextBlock { Text = LocalizationText.Get(nameKey), VerticalAlignment = VerticalAlignment.Center };
        AppTypography.BindFontSize(label, AppTypography.Body);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12,
            Classes = { "settings-row" } };
        row.Children.Add(label);
        Grid.SetColumn(input, 1);
        row.Children.Add(input);
        var cancel = new AppButton { Content = LocalizationText.Get("取消"), Classes = { "secondary", "compact" } };
        var save = new AppButton { Name = "SaveButton", Content = LocalizationText.Get("保存"), Classes = { "primary", "compact" },
            IsEnabled = !string.IsNullOrWhiteSpace(initial) };
        void Save() { if (!string.IsNullOrWhiteSpace(input.Text)) Close(input.Text.Trim()); }
        input.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Save(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => Save();
        Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children =
        {
            new Border { Classes = { "settings-group" }, Child = row },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8, Children = { cancel, save } }
        } };
        Opened += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
