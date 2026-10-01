using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views.Dialogs;

public partial class RemoteConnectionDialog : DialogWindow
{
    private readonly IRemoteConnectionService _connectionService;
    private RemoteServerInfo? _editingServer;
    private readonly CancellationTokenSource _connectionCancellation = new();
    public RemoteServerInfo? Result { get; private set; }
    public bool Connected { get; private set; }

    public RemoteConnectionDialog()
    {
        InitializeComponent();
        KeyPathBox.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty) return;
            KeyPassphraseBox.Text = "";
            RememberKeyPassphrase.IsChecked = false;
        };
        _connectionService = App.Services.GetRequiredService<IRemoteConnectionService>();
        Height = _connectionService.GetSavedServers().Count > 0 ? 650 : 550;
        Loaded += OnLoaded;
        Closed += (_, _) => _connectionCancellation.Cancel();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RefreshSavedServers();
        ConnectionStatus.Text = _connectionService.CredentialLoadError ?? "";
        HostBox.Focus();
    }

    private void RefreshSavedServers()
    {
        var servers = _connectionService.GetSavedServers();
        SavedServersList.ItemsSource = servers;
        SavedServersPanel.IsVisible = servers.Count > 0;
    }

    private void OnSavedServerSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (SavedServersList.SelectedItem is not RemoteServerInfo server) return;
        _editingServer = server;
        NameBox.Text = server.Name;
        HostBox.Text = server.Host;
        PortBox.Text = server.Port.ToString();
        UsernameBox.Text = server.Username;
        DefaultPathBox.Text = server.DefaultPath;
        DeleteButton.IsVisible = true;

        PasswordBox.Text = server.Password;
        KeyPathBox.Text = server.PrivateKeyPath;
        KeyPassphraseBox.Text = server.PrivateKeyPassphrase;
        RememberKeyPassphrase.IsChecked = server.RememberPrivateKeyPassphrase;
        if (server.AuthMethod == RemoteAuthMethod.PrivateKey)
        {
            KeyRadio.IsChecked = true;
            KeyPathBox.Text = server.PrivateKeyPath;
        }
        else
        {
            PasswordRadio.IsChecked = true;
            PasswordBox.Text = server.Password;
        }
        OnAuthMethodChanged(this, e);
    }

    private void OnAuthMethodChanged(object? sender, RoutedEventArgs e)
    {
        var isPassword = PasswordRadio.IsChecked == true;
        PasswordPanel.IsVisible = isPassword;
        KeyPanel.IsVisible = !isPassword;
        KeyPassphrasePanel.IsVisible = !isPassword;
    }

    private async void OnBrowseKeyFile(object? sender, RoutedEventArgs e)
    {
        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var files = await storage.OpenAuthorizedFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择私钥文件",
            AllowMultiple = false,
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/.ssh")
        });

        if (files.Count > 0)
        {
            KeyPathBox.Text = files[0].Path.LocalPath;
            KeyPassphraseBox.Text = "";
            RememberKeyPassphrase.IsChecked = false;
        }
    }

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        var server = BuildServerInfo();
        if (server == null) return;

        ConnectButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        ConnectButton.Content = "连接中...";

        try
        {
            await _connectionService.ConnectAsync(server, _connectionCancellation.Token);
            _connectionCancellation.Token.ThrowIfCancellationRequested();
            _connectionService.SaveServer(server);
            Result = server;
            Connected = true;
            Close(server);
        }
        catch (Exception ex)
        {
            Connected = false;
            _connectionService.Disconnect(server.Id);
            ConnectionStatus.Text = $"连接或保存未完成：{ex.Message} 修正问题后可重新点击连接；输入内容已保留。";
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
            ConnectButton.Content = "连接";
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var server = BuildServerInfo();
        if (server == null) return;
        try
        {
            _connectionService.SaveServer(server);
            _editingServer = server;
            RefreshSavedServers();
            ConnectionStatus.Text = "已保存。";
        }
        catch (Exception ex) { ConnectionStatus.Text = $"保存失败：{ex.Message} 输入内容已保留，可重试保存。"; }
    }

    private void OnDeleteServer(object? sender, RoutedEventArgs e)
    {
        if (_editingServer == null) return;
        try
        {
            _connectionService.RemoveServer(_editingServer.Id);
            _editingServer = null;
            ClearForm();
            RefreshSavedServers();
            ConnectionStatus.Text = "已删除连接配置；主机信任可单独撤销。";
        }
        catch (Exception ex) { ConnectionStatus.Text = $"删除失败：{ex.Message} 配置已保留，可重试删除。"; }
    }

    private void OnRetryCredentials(object? sender, RoutedEventArgs e)
    {
        var selectedId = _editingServer?.Id;
        _connectionService.RetryCredentialMigration();
        ConnectionStatus.Text = _connectionService.CredentialLoadError ?? "凭据已重新加载；迁移完成。";
        RefreshSavedServers();
        if (selectedId != null)
            SavedServersList.SelectedItem = _connectionService.GetSavedServers().FirstOrDefault(server => server.Id == selectedId);
    }

    private async void OnForgetHostKey(object? sender, RoutedEventArgs e)
    {
        var server = BuildServerInfo();
        if (server == null) return;
        try
        {
            var key = _connectionService.GetTrustedHostKey(server.Host, server.Port);
            if (key == null) { ConnectionStatus.Text = "此主机与端口没有已保存的信任。"; return; }
            var confirm = new ConfirmDialog("撤销主机信任", $"{key.Host}:{key.Port}\n{key.Fingerprint}\n撤销后会断开此主机的连接，下一次连接需要重新核对身份。请先通过独立渠道核实密钥变更。", "撤销信任");
            if (!await confirm.ShowDialog<bool>(this)) return;
            _connectionService.ForgetHostKey(server.Host, server.Port);
            ConnectionStatus.Text = "已撤销此主机与端口的信任；未改动其他主机记录。";
        }
        catch (Exception ex) { ConnectionStatus.Text = ex.Message; }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private RemoteServerInfo? BuildServerInfo()
    {
        var host = HostBox.Text?.Trim();
        HostError.Text = "请输入主机地址";
        HostError.IsVisible = string.IsNullOrEmpty(host);
        if (HostError.IsVisible)
        {
            HostBox.Focus();
            return null;
        }

        if (!int.TryParse(string.IsNullOrWhiteSpace(PortBox.Text) ? "22" : PortBox.Text.Trim(), out var port) || port < 1 || port > 65535)
        {
            HostError.Text = "端口应为 1–65535。"; HostError.IsVisible = true; PortBox.Focus(); return null;
        }
        // Editing is a candidate until persistence succeeds; failed saves cannot mutate saved objects.
        var server = new RemoteServerInfo { Id = _editingServer?.Id ?? Guid.NewGuid().ToString("N") };
        server.Name = NameBox.Text?.Trim() ?? "";
        server.Host = host!;
        server.Port = port;
        server.Username = string.IsNullOrWhiteSpace(UsernameBox.Text) ? "root" : UsernameBox.Text.Trim();
        server.DefaultPath = string.IsNullOrWhiteSpace(DefaultPathBox.Text) ? "/" : DefaultPathBox.Text.Trim();
        server.AuthMethod = PasswordRadio.IsChecked == true ? RemoteAuthMethod.Password : RemoteAuthMethod.PrivateKey;
        server.Password = server.AuthMethod == RemoteAuthMethod.Password ? PasswordBox.Text ?? "" : "";
        server.PrivateKeyPath = KeyPathBox.Text?.Trim() ?? "";
        server.PrivateKeyPassphrase = server.AuthMethod == RemoteAuthMethod.PrivateKey ? KeyPassphraseBox.Text ?? "" : "";
        server.RememberPrivateKeyPassphrase = server.AuthMethod == RemoteAuthMethod.PrivateKey && RememberKeyPassphrase.IsChecked == true;
        return server;
    }

    private void ClearForm()
    {
        _editingServer = null;
        NameBox.Text = "";
        HostBox.Text = "";
        PortBox.Text = "";
        UsernameBox.Text = "";
        PasswordBox.Text = "";
        KeyPathBox.Text = "";
        KeyPassphraseBox.Text = "";
        RememberKeyPassphrase.IsChecked = false;
        DefaultPathBox.Text = "/";
        PasswordRadio.IsChecked = true;
        DeleteButton.IsVisible = false;
    }
}
