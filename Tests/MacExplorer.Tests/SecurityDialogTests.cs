using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Copilot;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "SecurityDialog")]
public sealed class SecurityDialogTests
{
    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopilotSharesOnePromptAcrossWindowsAndOnlyPromptsAgainAfterRevocation(bool allow)
    {
        var settingsStore = new PluginTestEnvironment.MemorySettings();
        var settings = new CopilotSettings(settingsStore);
        var otherSettings = new CopilotSettings(settingsStore);
        var owner = InputAppearanceTests.CreateWindow(new TextBlock(), false);
        var otherOwner = InputAppearanceTests.CreateWindow(new TextBlock(), true);
        try
        {
            var first = CopilotPrivacyConsent.EnsureAsync(owner, settings);
            var second = CopilotPrivacyConsent.EnsureAsync(otherOwner, otherSettings);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(otherOwner.OwnedWindows);
            var dialog = owner.OwnedWindows.Single();
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, allow ? "允许发送" : "取消"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(allow, await first);
            Assert.Equal(allow, await second);
            Assert.Equal(allow, settings.HasMetadataConsent());
            if (allow)
            {
                settings.Model = "different";
                settings.Endpoint = "https://different.test/v1";
                Assert.True(await CopilotPrivacyConsent.EnsureAsync(otherOwner, otherSettings));
                Assert.Empty(otherOwner.OwnedWindows);
                otherSettings.RevokeMetadataSharing();
            }
            var retry = CopilotPrivacyConsent.EnsureAsync(otherOwner, otherSettings);
            Dispatcher.UIThread.RunJobs();
            otherOwner.OwnedWindows.Single().Close(false);
            Assert.False(await retry);
            Assert.False(settings.HasMetadataConsent());
        }
        finally
        {
            foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(false);
            foreach (var dialog in otherOwner.OwnedWindows.ToArray()) dialog.Close(false);
            owner.Close(); otherOwner.Close();
        }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmationRequiresAcceptAndEscapeOrCancelReturnFalse(bool dark)
    {
        var owner = InputAppearanceTests.CreateWindow(new TextBlock(), dark);
        try
        {
            foreach (var choice in new[] { "取消", "Escape", "允许" })
            {
                var dialog = new ConfirmDialog("授权确认", "GPS 经纬度会发给 Apple，用于转换地点名称。", "允许")
                    { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
                var pending = dialog.ShowDialog<bool>(owner); Dispatcher.UIThread.RunJobs();
                if (choice == "Escape") dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                else dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, choice))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(choice == "允许", await pending);
                Assert.True(owner.IsVisible);
            }
        }
        finally { owner.Close(); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateKeyPromptDefaultsToOneUseAndCancelReturnsNoCredential(bool dark)
    {
        var owner = InputAppearanceTests.CreateWindow(new TextBlock(), dark);
        try
        {
            var pending = SftpPrivateKeyConsent.ShowAsync(owner, new RemoteServerInfo { PrivateKeyPath = "/fixture/key.pem" }, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            var dialog = owner.OwnedWindows.Single();
            var input = dialog.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.Equal('•', input.PasswordChar);
            Assert.False(dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked == true);
            input.Text = "fixture-private-key-secret";
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "取消"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(await pending); Assert.Empty(input.Text!);
        }
        finally { owner.Close(); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRemoteDialogSaveAndDeleteKeepFormAndSavedServer(bool dark)
    {
        var root = Path.Combine(RuntimePaths.TestRoot ?? Path.GetTempPath(), "remote-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "servers.json");
        var credentials = new PrivacySecurityFollowupTests.MemoryCredentials();
        using var service = new RemoteConnectionService(config, credentials);
        service.SaveServer(new RemoteServerInfo { Id = "fixture", Name = "fixture", Host = "original.test", Password = "original" });
        service.SaveServer(new RemoteServerInfo { Id = "key-fixture", Host = "key.test", AuthMethod = RemoteAuthMethod.PrivateKey });
        var previous = App.Services;
        using var services = new ServiceCollection().AddSingleton<IRemoteConnectionService>(service).BuildServiceProvider();
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        var owner = InputAppearanceTests.CreateWindow(new TextBlock(), dark);
        var dialog = new RemoteConnectionDialog { Width = 400, Height = 450, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            dialog.Show(owner); Dispatcher.UIThread.RunJobs();
            dialog.FindControl<ListBox>("SavedServersList")!.SelectedIndex = 1;
            Assert.True(dialog.FindControl<Border>("KeyPanel")!.IsVisible);
            Assert.True(dialog.FindControl<Border>("KeyPassphrasePanel")!.IsVisible);
            Assert.Equal('•', dialog.FindControl<TextBox>("KeyPassphraseBox")!.PasswordChar);
            Assert.False(dialog.FindControl<CheckBox>("RememberKeyPassphrase")!.IsChecked == true);
            dialog.FindControl<TextBox>("KeyPassphraseBox")!.Text = "fixture-private-key-secret";
            dialog.FindControl<CheckBox>("RememberKeyPassphrase")!.IsChecked = true;
            dialog.FindControl<TextBox>("KeyPathBox")!.Text = "/fixture/other-key";
            Assert.Empty(dialog.FindControl<TextBox>("KeyPassphraseBox")!.Text!);
            Assert.False(dialog.FindControl<CheckBox>("RememberKeyPassphrase")!.IsChecked == true);
            Assert.False(dialog.FindControl<Border>("PasswordPanel")!.IsVisible);
            dialog.FindControl<ListBox>("SavedServersList")!.SelectedIndex = 0;
            Assert.True(dialog.FindControl<Border>("PasswordPanel")!.IsVisible);
            dialog.FindControl<TextBox>("HostBox")!.Text = "edited.test";
            Directory.CreateDirectory(config + ".tmp");
            dialog.FindControl<Button>("SaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("保存失败") == true);
            Assert.Equal("edited.test", dialog.FindControl<TextBox>("HostBox")!.Text);
            Assert.Equal("original.test", service.GetSavedServers().Single(server => server.Id == "fixture").Host);
            credentials.FailDelete = true;
            dialog.FindControl<Button>("DeleteButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("删除失败") == true);
            Assert.Equal("edited.test", dialog.FindControl<TextBox>("HostBox")!.Text);
            Assert.Equal(2, service.GetSavedServers().Count);
            var save = dialog.FindControl<Button>("SaveButton")!;
            Assert.True(save.IsVisible); Assert.True(save.Bounds.Height >= 28);
            Assert.True(dialog.FindControl<Button>("ConnectButton")!.Bounds.Right <= dialog.ClientSize.Width);
            credentials.FailDelete = false;
            credentials.Save("fixture", "reloaded-password");
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "重试凭据加载"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("reloaded-password", dialog.FindControl<TextBox>("PasswordBox")!.Text);
        }
        finally
        {
            dialog.Close(); owner.Close();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, previous);
            Directory.Delete(root, true);
        }
    }

}
