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
    internal TabGroupNameDialog(string initial, string titleKey = "tabs.group.rename", string nameKey = "tabs.group.name",
        Func<string, Task>? saveAsync = null)
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
        var label = new TextBlock { Text = LocalizationText.Get(nameKey), VerticalAlignment = VerticalAlignment.Center,
            Classes = { "settings-label" } };
        AppTypography.BindFontSize(label, AppTypography.Body);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        row.Children.Add(label);
        Grid.SetColumn(input, 1);
        row.Children.Add(input);
        var cancel = new AppButton { Content = LocalizationText.Get("取消"), Classes = { "secondary", "compact" } };
        var save = new AppButton { Name = "SaveButton", Content = LocalizationText.Get("保存"), Classes = { "primary", "compact" },
            IsEnabled = !string.IsNullOrWhiteSpace(initial) };
        var error = new TextBlock { Name = "GroupNameError", Classes = { "settings-description" },
            TextWrapping = TextWrapping.Wrap, IsVisible = false };
        error.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("DangerBrush"));
        var saving = false;
        async Task SaveAsync()
        {
            if (saving || string.IsNullOrWhiteSpace(input.Text)) return;
            saving = true;
            save.IsEnabled = input.IsEnabled = false;
            try
            {
                var name = input.Text.Trim();
                if (saveAsync != null) await saveAsync(name);
                Close(name);
            }
            catch (Exception ex) { error.Text = ex.Message; error.IsVisible = true; }
            finally
            {
                saving = false;
                input.IsEnabled = true;
                save.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
            }
        }
        input.TextChanged += (_, _) =>
        {
            save.IsEnabled = !saving && !string.IsNullOrWhiteSpace(input.Text);
            error.IsVisible = false;
        };
        input.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await SaveAsync(); }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        cancel.Click += (_, _) => Close();
        save.Click += async (_, _) => await SaveAsync();
        Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children =
        {
            new Border { Classes = { "settings-group" }, Child = new Border
                { Classes = { "settings-compact-row" }, Child = row } },
            error,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8, Children = { cancel, save } }
        } };
        Opened += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
