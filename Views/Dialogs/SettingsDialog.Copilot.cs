using Avalonia.Controls;
using Avalonia.Interactivity;
using MacExplorer.Copilot;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views.Dialogs;

public partial class SettingsDialog
{
    private CopilotSettings CopilotConnection => App.Services.GetRequiredService<CopilotSettings>();
    private CopilotKeychain CopilotKeychain => App.Services.GetRequiredService<CopilotKeychain>();
    private CopilotSkillCatalog CopilotSkills => App.Services.GetRequiredService<CopilotSkillCatalog>();
    private string? _savedCopilotKey;

    private void LoadCopilotSettings()
    {
        CopilotEndpointBox.Text = CopilotConnection.Endpoint;
        CopilotModelBox.Text = CopilotConnection.Model;
        RefreshCopilotSkills();
        try
        {
            _savedCopilotKey = CopilotKeychain.Read();
            CopilotKeyBox.Text = _savedCopilotKey;
        }
        catch (Exception ex) { CopilotSettingsStatus.Text = ex.Message; }
    }

    private void SaveCopilotConnection(object? sender, RoutedEventArgs e)
    {
        try
        {
            var endpoint = CopilotEndpointBox.Text?.Trim() ?? string.Empty;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                    && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
                throw new ArgumentException("API 地址须使用 HTTPS；本机回环地址可使用 HTTP。");
            var model = CopilotModelBox.Text?.Trim();
            if (string.IsNullOrEmpty(model)) throw new ArgumentException("请输入模型名称。");
            var key = CopilotKeyBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(key) && key != _savedCopilotKey)
            {
                CopilotKeychain.Save(key);
                _savedCopilotKey = key;
            }
            CopilotConnection.Endpoint = endpoint;
            CopilotConnection.Model = model;
            CopilotKeyBox.Text = _savedCopilotKey;
            CopilotSettingsStatus.Text = string.Empty;
        }
        catch (Exception ex) { CopilotSettingsStatus.Text = ex.Message; }
    }

    private void RefreshCopilotSkills(string? selected = null)
    {
        var names = CopilotSkills.List().Select(item => item.Name).ToArray();
        CopilotSkillPicker.ItemsSource = names;
        CopilotSkillPicker.SelectedItem = names.FirstOrDefault(name => name == selected) ?? names.FirstOrDefault();
    }

    private void OnCopilotSkillSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CopilotSkillPicker.SelectedItem is not string name) return;
        var skill = CopilotSkills.List().FirstOrDefault(item => item.Name == name);
        if (skill == null) return;
        CopilotSkillNameBox.Text = name;
        CopilotSkillContentBox.Text = skill.Content;
    }

    private void NewCopilotSkill(object? sender, RoutedEventArgs e)
    {
        CopilotSkillPicker.SelectedItem = null;
        CopilotSkillNameBox.Text = string.Empty;
        CopilotSkillContentBox.Text = "---\nname: my-skill\ndescription: Describe when to use this skill.\n---\n\n# 新技能\n\n只调用已登记能力；修改前先预览并请求批准。\n";
        CopilotSkillNameBox.Focus();
    }

    private void SaveCopilotSkill(object? sender, RoutedEventArgs e)
    {
        try
        {
            var name = CopilotSkillNameBox.Text?.Trim() ?? string.Empty;
            CopilotSkills.Save(name, CopilotSkillContentBox.Text ?? string.Empty);
            RefreshCopilotSkills(name);
            CopilotSettingsStatus.Text = "技能已保存。下一次对话将加载新内容。";
        }
        catch (Exception ex) { CopilotSettingsStatus.Text = ex.Message; }
    }

    private void ToggleCopilotSkill(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (CopilotSkillPicker.SelectedItem is not string name) return;
            var skill = CopilotSkills.List().First(item => item.Name == name);
            CopilotSkills.SetEnabled(name, !skill.Enabled);
            RefreshCopilotSkills(name);
            CopilotSettingsStatus.Text = skill.Enabled ? "技能已禁用。" : "技能已启用。";
        }
        catch (Exception ex) { CopilotSettingsStatus.Text = ex.Message; }
    }

    private void ResetCopilotSkill(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (CopilotSkillPicker.SelectedItem is not string name) return;
            CopilotSkills.Reset(name);
            RefreshCopilotSkills(name);
            CopilotSettingsStatus.Text = "已恢复内置技能。";
        }
        catch (Exception ex) { CopilotSettingsStatus.Text = ex.Message; }
    }
}
