using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views.Dialogs;

public partial class SettingsDialog
{
    private bool _changingPhotoConsent;
    private PhotoLocationConsent PhotoConsent => App.Services.GetRequiredService<PhotoLocationConsent>();

    private void LoadPrivacySettings()
    {
        PhotoLocationToggle.IsChecked = PhotoConsent.Enabled;
        PrivacyPolicyStatus.Text = PrivacyPolicy.Status;
        RefreshCopilotConsent();
    }

    private void RefreshCopilotConsent()
    {
        try
        {
            var allowed = CopilotConnection.HasMetadataConsent(CopilotCredentialStore.Read());
            CopilotConsentStatus.Text = allowed ? MacExplorer.Services.Impl.LocalizationText.Get("已允许向当前 AI 接收方分享文件信息。") : MacExplorer.Services.Impl.LocalizationText.Get("尚未许可；下一次发送需要确认接收方。");
        }
        catch (Exception ex) { CopilotConsentStatus.Text = ex.Message; }
    }

    private async void OnPhotoLocationChanged(object? sender, RoutedEventArgs e)
    {
        if (_initializing || _changingPhotoConsent) return;
        _changingPhotoConsent = true;
        try
        {
            var allowed = PhotoLocationToggle.IsChecked == true;
            if (allowed)
            {
                var confirm = new ConfirmDialog("允许照片地点联网解析", "照片 GPS 经纬度会发送给 Apple 的地理编码服务，用于转换为地点名称。可随时在设置中关闭。本地坐标、OCR 和其他分析无需此授权；已有记录不会自动重分析。", "允许", "取消");
                allowed = await confirm.ShowDialog<bool>(this);
            }
            PhotoConsent.SetAllowed(allowed);
            PhotoLocationToggle.IsChecked = allowed;
            PhotoLocationStatus.Text = allowed ? "已允许；仅后续需要分析的照片可联网解析。" : "已关闭；后续请求使用本地坐标。已有结果保留。";
        }
        catch (Exception ex) { PhotoLocationToggle.IsChecked = PhotoConsent.Enabled; PhotoLocationStatus.Text = ex.Message; }
        finally { _changingPhotoConsent = false; }
    }

    private void OnRevokeCopilotConsent(object? sender, RoutedEventArgs e)
    {
        try
        {
            CopilotConnection.RevokeMetadataSharing();
            RefreshCopilotConsent();
        }
        catch (Exception ex)
        {
            CopilotConsentStatus.Text = $"撤回未能保存：{ex.Message} 当前会话已停止后续分享；请修复存储后重试，避免重启后恢复旧许可。";
        }
    }

    private async void OnPrivacyPolicy(object? sender, RoutedEventArgs e)
    {
        try { await PrivacyPolicy.ShowAsync(this); }
        catch (Exception ex) { PrivacyPolicyStatus.Text = ex.Message; }
    }
}
