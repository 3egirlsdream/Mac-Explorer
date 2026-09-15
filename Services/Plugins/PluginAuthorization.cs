using Avalonia.Controls;
using MacExplorer.PluginSdk;

namespace MacExplorer.Services.Plugins;

public static class PluginAuthorization
{
    public static async Task<bool> EnsureAsync(PluginManager manager, PluginManifest manifest, PluginSession session,
        PluginInvocation invocation, Window owner, CancellationToken token, bool manageAccount = false)
    {
        if (!manifest.Paid && !manageAccount) return true;
        if (manageAccount)
        {
            var done = await session.CallAsync<PluginInteractionResult>("account", new PluginInteractionRequest(
                new(invocation, manager.GetTrial(manifest.Id)), "manage"), Timeout.InfiniteTimeSpan, token);
            if (!done.Completed) return false;
        }
        while (true)
        {
            var trial = manager.GetTrial(manifest.Id);
            var access = await session.CallAsync<PluginAccessResult>("check-access", new PluginAccessRequest(invocation, trial), TimeSpan.FromSeconds(30), token);
            if (!Enum.IsDefined(access.Status)) throw new InvalidDataException("插件返回未知授权状态。");
            manager.SetAccess(manifest.Id, access);
            if (access.Status == PluginAccessStatus.Allowed) return true;
            if (access.Status == PluginAccessStatus.TrialAvailable && trial.Active) return true;
            if (manageAccount) return false;
            var canStart = access.Status == PluginAccessStatus.TrialAvailable && trial.StartedAt == null && manifest.TrialDays > 0;
            var message = access.Message;
            if (access.Status == PluginAccessStatus.TrialAvailable && trial.StartedAt != null && !trial.Active)
                message = "试用已到期，请登录或购买后继续。";
            var choice = await PromptAsync(owner, manifest.Name, string.IsNullOrWhiteSpace(message) ? "此功能需要授权后使用。" : message,
                canStart, manifest.HasUserInterface, token);
            if (choice == "trial") { manager.GetTrial(manifest.Id, start: true); continue; }
            if (choice != "account") return false;
            var result = await session.CallAsync<PluginInteractionResult>("account", new PluginInteractionRequest(new(invocation, trial), "authorize"), Timeout.InfiniteTimeSpan, token);
            if (!result.Completed) return false;
        }
    }

    private static async Task<string?> PromptAsync(Window owner, string title, string message, bool trial, bool account, CancellationToken token)
    {
        var dialog = new MacExplorer.Controls.DialogWindow { Title = title, Width = 440, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        void Add(string label, string result) { var button = new Button { Content = label }; button.Click += (_, _) => dialog.Close(result); buttons.Children.Add(button); }
        if (trial) Add("开始试用", "trial");
        if (account) Add("登录 / 购买", "account");
        Add("取消", "cancel");
        dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 18, Children =
        { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons } };
        using var registration = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => dialog.Close(null)));
        return await dialog.ShowDialog<string?>(owner);
    }
}
