using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MacExplorer.Controls;
using MacExplorer.Copilot;

namespace MacExplorer.Views.Dialogs;

internal static class CopilotPrivacyConsent
{
    internal static async Task<bool> EnsureAsync(Window owner, CopilotSettings settings, CopilotCredentialStore credentials)
    {
        var key = credentials.Read();
        if (settings.HasMetadataConsent(key)) return true;
        var endpoint = settings.Endpoint; var model = settings.Model;
        var dialog = new DialogWindow { Title = "允许向 AI 发送文件信息", Width = 440,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        dialog.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MacExplorer/")) { Source = new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml") });
        var cancel = new Button { Content = "取消", Classes = { "secondary", "compact" } };
        var allow = new Button { Content = "允许发送", Classes = { "primary", "compact" } };
        cancel.Click += (_, _) => dialog.Close(false);
        allow.Click += (_, _) => dialog.Close(true);
        var recipient = new TextBlock { Text = $"接收服务：{endpoint}\n模型：{model}", TextWrapping = TextWrapping.Wrap };
        recipient.Classes.Add("settings-label");
        var notice = new TextBlock { Text = "发送对话会把你输入的文字、当前路径、选中文件名称、附件路径及工具返回的搜索结果等信息发送给此服务，用于回答和执行请求。服务方按其政策处理这些数据。文件正文仍需单独确认。更换接收配置后需重新许可，可在设置中撤回。",
            TextWrapping = TextWrapping.Wrap };
        notice.Classes.Add("settings-description");
        var policy = new Button { Content = "隐私政策", Classes = { "ghost", "compact" }, HorizontalAlignment = HorizontalAlignment.Left };
        policy.Click += async (_, _) => await PrivacyPolicy.ShowAsync(dialog);
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children =
        {
            recipient, notice, policy,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8, Children = { cancel, allow } }
        } };
        dialog.Opened += (_, _) => cancel.Focus();
        if (!await dialog.ShowDialog<bool>(owner)) return false;
        // A settings window may have changed the receiver while this dialog was open.
        if (settings.Endpoint != endpoint || settings.Model != model || credentials.Read() != key) return false;
        settings.AllowMetadataSharing(key);
        return true;
    }
}
