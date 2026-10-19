using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MacExplorer.Controls;
using MacExplorer.Copilot;

namespace MacExplorer.Views.Dialogs;

internal static class CopilotPrivacyConsent
{
    private static Task<bool>? _pending;

    internal static async Task<bool> EnsureAsync(Window owner, CopilotSettings settings)
    {
        if (settings.HasMetadataConsent()) return true;
        if (_pending != null) return await _pending && settings.HasMetadataConsent();
        _pending = ShowAsync(owner, settings);
        try { return await _pending; }
        finally { _pending = null; }
    }

    private static async Task<bool> ShowAsync(Window owner, CopilotSettings settings)
    {
        var endpoint = settings.Endpoint; var model = settings.Model;
        var dialog = new DialogWindow { Title = MacExplorer.Services.Impl.LocalizationText.Get("允许向 AI 发送文件信息"), Width = 440,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Resources["TitleBarBackgroundBrush"] = Brushes.Transparent;
        dialog.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MacExplorer/")) { Source = new Uri("avares://MacExplorer/Views/Dialogs/SettingsStyles.axaml") });
        var cancel = new Button { Content = MacExplorer.Services.Impl.LocalizationText.Get("取消"), Classes = { "secondary", "compact" } };
        var allow = new Button { Content = MacExplorer.Services.Impl.LocalizationText.Get("允许发送"), Classes = { "primary", "compact" } };
        cancel.Click += (_, _) => dialog.Close(false);
        allow.Click += (_, _) => dialog.Close(true);
        var recipient = new TextBlock { Text = MacExplorer.Services.Impl.LocalizationText.Get("接收服务：{0}\n模型：{1}", endpoint, model), TextWrapping = TextWrapping.Wrap };
        recipient.Classes.Add("settings-label");
        var notice = new TextBlock { Text = MacExplorer.Services.Impl.LocalizationText.Get("发送对话会把你输入的文字、当前路径、选中文件名称、附件路径及工具返回的搜索结果等信息发送给此服务，用于回答和执行请求。服务方按其政策处理这些数据。文件正文仍需单独确认。此授权在应用内只需一次，更换 AI 配置后也会沿用，可在设置中撤回。"),
            TextWrapping = TextWrapping.Wrap };
        notice.Classes.Add("settings-description");
        var policy = new Button { Content = MacExplorer.Services.Impl.LocalizationText.Get("隐私政策"), Classes = { "ghost", "compact" }, HorizontalAlignment = HorizontalAlignment.Left };
        policy.Click += async (_, _) => await PrivacyPolicy.ShowAsync(dialog);
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children =
        {
            recipient, notice, policy,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8, Children = { cancel, allow } }
        } };
        dialog.Opened += (_, _) => cancel.Focus();
        if (!await dialog.ShowDialog<bool>(owner)) return false;
        settings.AllowMetadataSharing();
        return true;
    }
}
