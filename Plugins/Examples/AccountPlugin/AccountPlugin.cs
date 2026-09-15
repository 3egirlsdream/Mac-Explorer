using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MacExplorer.PluginSdk;
using MacExplorer.PluginUi;

namespace AccountPlugin;

public sealed class ExamplePlugin : IFileActionPlugin, IPluginAccessProvider
{
    private bool _signedIn;
    private bool _paid;
    public Task<PluginAccessResult> CheckAccessAsync(PluginAccessRequest request, CancellationToken cancellationToken)
        => Task.FromResult(_paid ? new(PluginAccessStatus.Allowed, "本次会话已模拟授权。")
            : request.Trial.StartedAt != null && !request.Trial.Active ? new(PluginAccessStatus.TrialExpired, "试用已到期，可打开示例窗口模拟购买。")
            : new PluginAccessResult(PluginAccessStatus.TrialAvailable, "可开始 7 天试用，或打开插件账号窗口。"));
    public async Task<PluginInteractionResult> ShowAccountAsync(PluginInteractionRequest request, CancellationToken cancellationToken)
        => new(await PluginWindows.ShowAsync(() =>
        {
            var window = new Window { Title = "插件账号 · 开发示例", Width = 430, Height = 300, CanResize = false };
            var status = new TextBlock { Text = "此窗口由插件进程创建，不会进行真实支付。", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var login = new Button { Content = "模拟登录" };
            var buy = new Button { Content = "模拟已购买并继续", IsEnabled = _signedIn };
            login.Click += (_, _) => { _signedIn = true; buy.IsEnabled = true; status.Text = "已模拟登录。生产插件应在此调用自己的账号服务。"; };
            buy.Click += (_, _) => { _paid = true; PluginWindows.Complete(window); };
            var cancel = new Button { Content = "取消" }; cancel.Click += (_, _) => window.Close();
            window.Content = new StackPanel { Margin = new Thickness(24), Spacing = 18, Children = { status, login, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { buy, cancel } } } };
            return window;
        }, cancellationToken));
    public Task<PluginPreparation> PrepareAsync(PluginInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.CommandId != "copy" || invocation.Files.Length != 1 ||
            invocation.Files[0].Source != "local" || invocation.Files[0].IsDirectory ||
            !string.Equals(Path.GetExtension(invocation.Files[0].Path), ".txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择一个本地文本文件。");
        return Task.FromResult(new PluginPreparation());
    }
    public async Task<PluginResult> ExecuteAsync(PluginInvocation invocation, IProgress<PluginProgress> progress, CancellationToken cancellationToken)
    {
        await PrepareAsync(invocation, cancellationToken);
        var output = Path.Combine(invocation.WorkDirectory, "copy.txt");
        await using var source = File.OpenRead(invocation.Files.Single().Path);
        await using var destination = File.Create(output);
        await source.CopyToAsync(destination, cancellationToken);
        return new([new(output, "副本.txt")], []);
    }
}
