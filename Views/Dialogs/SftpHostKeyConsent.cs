using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MacExplorer.Controls;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views.Dialogs;

internal static class SftpHostKeyConsent
{
    internal static async Task<bool> ConfirmAsync(SftpHostKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var pending = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var owner = desktop?.Windows.FirstOrDefault(window => window.IsActive) ?? desktop?.MainWindow;
            return owner == null || ct.IsCancellationRequested ? Task.FromResult(false) : ShowAsync(owner, key, ct);
        });
        return pending;
    }

    internal static async Task<bool> ShowAsync(Window owner, SftpHostKey key, CancellationToken ct)
    {
        var dialog = new DialogWindow { Title = "信任 SFTP 服务器", Width = 460, MinWidth = 340,
            MaxHeight = 540, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        dialog.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MacExplorer/")) { Source = new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml") });
        var reject = new Button { Content = "取消", Classes = { "secondary", "compact" } };
        var trust = new Button { Content = "信任并连接", Classes = { "primary", "compact" } };
        reject.Click += (_, _) => dialog.Close(false);
        trust.Click += (_, _) => dialog.Close(true);
        var identity = new TextBlock { Text = $"{key.Host}:{key.Port}\n{key.Algorithm}", TextWrapping = TextWrapping.Wrap,
            Classes = { "settings-label" } };
        var fingerprint = new TextBox { Text = key.Fingerprint, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            Classes = { "multiline" } };
        Avalonia.Automation.AutomationProperties.SetName(fingerprint, "服务器 SHA256 指纹");
        var notice = new TextBlock { Text = "首次连接，请通过独立渠道向服务器管理员核对 SHA256 指纹。确认后将保存此主机与端口的身份；后续密钥变化会拒绝连接。",
            TextWrapping = TextWrapping.Wrap, Classes = { "settings-description" } };
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children =
        {
            identity, fingerprint, notice,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8, Children = { reject, trust } }
        } };
        dialog.Opened += (_, _) => reject.Focus();
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        return await dialog.ShowDialog<bool>(owner) && !ct.IsCancellationRequested;
    }
}
