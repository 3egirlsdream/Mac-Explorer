using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Models;

namespace MacExplorer.Views.Dialogs;

internal static class SftpPrivateKeyConsent
{
    internal static async Task<(string Passphrase, bool Remember)?> RequestAsync(RemoteServerInfo server, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var pending = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var owner = desktop?.Windows.FirstOrDefault(window => window.IsActive) ?? desktop?.MainWindow;
            return owner == null || ct.IsCancellationRequested
                ? Task.FromResult<(string Passphrase, bool Remember)?>(null) : ShowAsync(owner, server, ct);
        });
        return pending;
    }

    internal static async Task<(string Passphrase, bool Remember)?> ShowAsync(Window owner, RemoteServerInfo server, CancellationToken ct)
    {
        var dialog = new DialogWindow { Title = "私钥口令 · " + Path.GetFileName(server.PrivateKeyPath),
            Width = 420, MinWidth = 340, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        dialog.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MacExplorer/"))
            { Source = new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml") });
        var input = new TextBox { PasswordChar = '•', Classes = { "settings-inline-input" }, PlaceholderText = "私钥解密口令" };
        Avalonia.Automation.AutomationProperties.SetName(input, "私钥解密口令");
        var remember = new CheckBox { Content = "将私钥口令保存到钥匙串", IsChecked = false };
        var cancel = new Button { Content = "取消", Classes = { "secondary", "compact" } };
        var connect = new Button { Content = "连接", Classes = { "primary", "compact" } };
        cancel.Click += (_, _) => dialog.Close();
        connect.Click += (_, _) => dialog.Close((input.Text ?? "", remember.IsChecked == true));
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 8, Children =
        {
            new Border { Classes = { "settings-group" }, Child = new StackPanel { Children =
            {
                new Border { Classes = { "settings-row", "settings-divider" }, Child = input },
                new Border { Classes = { "settings-row" }, Child = remember }
            } } },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, connect } }
        } };
        dialog.Opened += (_, _) => input.Focus();
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close()));
        var result = await dialog.ShowDialog<(string, bool)?>(owner);
        input.Text = "";
        return ct.IsCancellationRequested ? null : result;
    }
}
